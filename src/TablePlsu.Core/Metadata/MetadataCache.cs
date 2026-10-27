using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TablePlsu.Core.Db;
using TablePlsu.Core.IO;
using TablePlsu.Core.Paths;

namespace TablePlsu.Core.Metadata;

public sealed record MetadataCacheOptions
{
    /// <summary>settings.sidebar.loadSystemSchemas.</summary>
    public bool IncludeSystemSchemas { get; init; }

    /// <summary>
    /// settings.sidebar.lazyLoadSchemas. When true, <see cref="MetadataCache.RefreshAsync"/> lists every schema but only
    /// loads the contents of schemas that are already loaded plus the search_path schemas; others load on
    /// <see cref="MetadataCache.EnsureSchemaLoadedAsync"/>. When false, refresh loads the whole database.
    /// </summary>
    public bool LazySchemas { get; init; }
}

public enum MetadataChangeReason { LoadedFromDisk, Refreshed, SchemaLoaded, Cleared }

public sealed class MetadataChangedEventArgs(DbMetadata metadata, MetadataChangeReason reason, string? schema = null) : EventArgs
{
    public DbMetadata Metadata { get; } = metadata;
    public MetadataChangeReason Reason { get; } = reason;
    /// <summary>The schema that changed for <see cref="MetadataChangeReason.SchemaLoaded"/>; null otherwise.</summary>
    public string? Schema { get; } = schema;
}

/// <summary>
/// The metadata of one connection + database: an in-memory <see cref="DbMetadata"/> snapshot (<see cref="Current"/>,
/// never null, swapped atomically) backed by a JSON file under <c>AppPaths.MetadataCacheDir\{connectionId}\{db}.json</c>.
/// <see cref="LoadCachedAsync"/> gives instant data on open; <see cref="RefreshAsync"/> reloads from the server on the
/// session's dedicated metadata connection and rewrites the file. Concurrent refresh calls share one load.
/// <see cref="Changed"/> is raised on a thread-pool thread: marshal to the UI yourself.
/// </summary>
public sealed class MetadataCache : IDisposable
{
    private readonly DbSession? _session;
    private readonly object _gate = new();
    private DbMetadata _current;
    private Task<DbMetadata>? _refreshTask;
    private readonly Dictionary<string, Task<SchemaInfo?>> _schemaLoads = new(StringComparer.Ordinal);
    private CancellationTokenSource _cts = new();
    private bool _disposed;

    public MetadataCache(string connectionId, string database, DbSession? session, AppPaths? paths = null, MetadataCacheOptions? options = null)
    {
        ConnectionId = connectionId;
        Database = database;
        _session = session;
        Options = options ?? new MetadataCacheOptions();
        var p = paths ?? AppPaths.Current;
        CacheFile = Path.Combine(p.MetadataCacheDir, AppPaths.SafeSegment(connectionId), FileNameFor(database) + ".json");
        _current = DbMetadata.Empty(database);
    }

    public string ConnectionId { get; }
    public string Database { get; }
    public string CacheFile { get; }
    public MetadataCacheOptions Options { get; set; }

    /// <summary>The latest snapshot. Empty (no schemas) until the disk cache or a refresh has loaded.</summary>
    public DbMetadata Current => Volatile.Read(ref _current);

    public bool HasData => !Current.IsEmpty;

    public bool IsRefreshing { get { lock (_gate) return _refreshTask is { IsCompleted: false }; } }

    /// <summary>When the last successful server refresh finished (UTC), null if none yet in this session.</summary>
    public DateTimeOffset? LastRefreshed { get; private set; }

    /// <summary>The error of the last failed refresh (null after a success).</summary>
    public Exception? LastError { get; private set; }

    /// <summary>Raised (thread-pool thread) after <see cref="Current"/> changed.</summary>
    public event EventHandler<MetadataChangedEventArgs>? Changed;

    /// <summary>Raised (thread-pool thread) when a refresh starts (true) and ends (false).</summary>
    public event EventHandler<bool>? RefreshingChanged;

    // ---- lookups (delegate to Current) -----------------------------------------------------------------------

    /// <inheritdoc cref="DbMetadata.FindTable"/>
    public TableInfo? FindTable(string? schema, string name) => Current.FindTable(schema, name);

    public SchemaInfo? FindSchema(string name) => Current.FindSchema(name);

    /// <inheritdoc cref="DbMetadata.FindFunctions"/>
    public IReadOnlyList<FunctionInfo> FindFunctions(string? schema, string name) => Current.FindFunctions(schema, name);

    // ---- disk ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Loads the JSON cache (off the caller's thread). True when it was read and published. Does nothing (false) when
    /// the file is missing, unreadable or another format version, or when a server refresh already published data.
    /// </summary>
    public Task<bool> LoadCachedAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        try
        {
            if (!File.Exists(CacheFile)) return false;
            using var stream = new FileStream(CacheFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var md = JsonSerializer.Deserialize(stream, MetadataJsonContext.Default.DbMetadata);
            if (md is null || md.FormatVersion != DbMetadata.CurrentFormatVersion) return false;
            lock (_gate)
            {
                if (_disposed || LastRefreshed is not null) return false;
                Volatile.Write(ref _current, md);
            }
            Changed?.Invoke(this, new MetadataChangedEventArgs(md, MetadataChangeReason.LoadedFromDisk));
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }, ct);

    /// <summary>Writes <paramref name="metadata"/> to <see cref="CacheFile"/> atomically.</summary>
    public void Save(DbMetadata metadata)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(metadata, MetadataJsonContext.Default.DbMetadata);
        AtomicFile.WriteAllBytes(CacheFile, bytes);
    }

    /// <summary>Deletes the disk cache and empties <see cref="Current"/>.</summary>
    public void Clear()
    {
        try { if (File.Exists(CacheFile)) File.Delete(CacheFile); }
        catch (IOException) { }
        var empty = DbMetadata.Empty(Database);
        Volatile.Write(ref _current, empty);
        Changed?.Invoke(this, new MetadataChangedEventArgs(empty, MetadataChangeReason.Cleared));
    }

    // ---- server ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Reloads from the server in the background (whole DB, or lazy per <see cref="Options"/>), publishes, raises
    /// <see cref="Changed"/> and saves to disk. If a refresh is running, returns that one. Exceptions propagate to the
    /// awaiter and are kept in <see cref="LastError"/>; <see cref="Current"/> is left untouched on failure.
    /// </summary>
    public Task<DbMetadata> RefreshAsync(CancellationToken ct = default)
    {
        var session = _session ?? throw new InvalidOperationException("This MetadataCache has no DbSession.");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_refreshTask is { IsCompleted: false } running) return running;
            var token = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct).Token;
            _refreshTask = Task.Run(() => RefreshCoreAsync(session, token), token);
            return _refreshTask;
        }
    }

    private async Task<DbMetadata> RefreshCoreAsync(DbSession session, CancellationToken ct)
    {
        RefreshingChanged?.Invoke(this, true);
        try
        {
            var opts = BuildLoadOptions();
            var md = await session.WithMetadataConnectionAsync((conn, c) => MetadataLoader.LoadAsync(conn, opts, c), ct).ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed) return md;
                // Lazy mode: a schema loaded (EnsureSchemaLoadedAsync) after this refresh was planned is newer than the
                // refresh's "listed only" entry for it; keep it instead of publishing over it.
                foreach (var s in Current.Schemas)
                    if (s.IsLoaded && md.FindSchema(s.Name) is { IsLoaded: false })
                        md = md.WithSchema(s);
                Volatile.Write(ref _current, md);
                LastRefreshed = md.LoadedAt;
                LastError = null;
            }
            Changed?.Invoke(this, new MetadataChangedEventArgs(md, MetadataChangeReason.Refreshed));
            TrySave(md);
            return md;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = ex;
            throw;
        }
        finally
        {
            RefreshingChanged?.Invoke(this, false);
        }
    }

    private MetadataLoadOptions BuildLoadOptions()
    {
        if (!Options.LazySchemas) return new MetadataLoadOptions { IncludeSystemSchemas = Options.IncludeSystemSchemas };
        var current = Current;
        var wanted = new HashSet<string>(StringComparer.Ordinal) { "public" };
        foreach (var s in current.Schemas) if (s.IsLoaded) wanted.Add(s.Name);
        foreach (var s in current.SearchPath) wanted.Add(s);
        return new MetadataLoadOptions { IncludeSystemSchemas = Options.IncludeSystemSchemas, Schemas = wanted };
    }

    /// <summary>
    /// Lazy mode: makes sure <paramref name="schema"/>'s contents are loaded (no server call when already loaded unless
    /// <paramref name="force"/>). Publishes a new snapshot and raises <see cref="Changed"/> (SchemaLoaded). Returns the
    /// loaded schema, or null when it does not exist.
    /// </summary>
    public Task<SchemaInfo?> EnsureSchemaLoadedAsync(string schema, bool force = false, CancellationToken ct = default)
    {
        if (!force && Current.FindSchema(schema) is { IsLoaded: true } loaded) return Task.FromResult<SchemaInfo?>(loaded);
        var session = _session ?? throw new InvalidOperationException("This MetadataCache has no DbSession.");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_schemaLoads.TryGetValue(schema, out var running) && !running.IsCompleted) return running;
            var token = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct).Token;
            var task = Task.Run(async () =>
            {
                var list = await session.WithMetadataConnectionAsync((conn, c) => MetadataLoader.LoadSchemasAsync(conn, [schema], c), token).ConfigureAwait(false);
                var info = list.FirstOrDefault();
                if (info is null) return null;
                DbMetadata md;
                lock (_gate)
                {
                    if (_disposed) return info;
                    md = Current.WithSchema(info);
                    Volatile.Write(ref _current, md);
                }
                Changed?.Invoke(this, new MetadataChangedEventArgs(md, MetadataChangeReason.SchemaLoaded, info.Name));
                TrySave(md);
                return info;
            }, token);
            _schemaLoads[schema] = task;
            return task;
        }
    }

    private void TrySave(DbMetadata md)
    {
        try { Save(md); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the cache is best effort */ }
    }

    /// <summary>A file-name-safe name for a database: the name itself when safe, else "db-" + a short hash.</summary>
    public static string FileNameFor(string database)
    {
        if (AppPaths.IsSafeSegment(database) && database.Length <= 100 && !database.EndsWith('.') && !database.EndsWith(' ')) return database;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(database));
        return "db-" + Convert.ToHexStringLower(hash, 0, 8);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _cts.Cancel();
        _cts.Dispose();
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DbMetadata))]
internal sealed partial class MetadataJsonContext : JsonSerializerContext;
