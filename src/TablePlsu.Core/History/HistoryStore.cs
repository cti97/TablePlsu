using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TablePlsu.Core.IO;
using TablePlsu.Core.Paths;

namespace TablePlsu.Core.History;

/// <summary>One statement sent to the server.</summary>
public sealed record HistoryEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..16];
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string Database { get; init; } = "";
    public string Sql { get; init; } = "";
    public double DurationMs { get; init; }
    public long? Rows { get; init; }
    public bool Error { get; init; }
    /// <summary>"editor", "commit", "admin"… (free-form).</summary>
    public string? Source { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HistoryEntry))]
internal sealed partial class HistoryJsonContext : JsonSerializerContext;

/// <summary>
/// Append-only JSONL history per connection (<c>history\{connId}.jsonl</c>), capped at <see cref="MaxEntries"/>
/// (the file is compacted atomically once it exceeds the cap by 10%). Thread-safe; I/O errors are logged, not thrown.
/// </summary>
public sealed class HistoryStore(AppPaths paths, Action<string>? log = null)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _lineCounts = new(StringComparer.Ordinal);

    public bool Enabled { get; set; } = true;
    public int MaxEntries { get; set; } = 10_000;

    public void Append(string connectionId, HistoryEntry entry)
    {
        if (!Enabled) return;
        var line = JsonSerializer.Serialize(entry, HistoryJsonContext.Default.HistoryEntry) + "\n";
        lock (_gate)
        {
            try
            {
                var file = paths.HistoryFile(connectionId);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.AppendAllText(file, line, new UTF8Encoding(false));
                var count = (_lineCounts.TryGetValue(connectionId, out var c) ? c : CountLines(file) - 1) + 1;
                _lineCounts[connectionId] = count;
                if (count > MaxEntries + Math.Max(1, MaxEntries / 10)) Compact(connectionId, file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log?.Invoke("History write failed: " + ex.Message);
            }
        }
    }

    /// <summary>Newest first, at most <see cref="MaxEntries"/>.</summary>
    public IReadOnlyList<HistoryEntry> Load(string connectionId)
    {
        lock (_gate)
        {
            var entries = ReadAll(paths.HistoryFile(connectionId));
            var skip = Math.Max(0, entries.Count - MaxEntries);
            var result = entries.Skip(skip).ToList();
            result.Reverse();
            return result;
        }
    }

    /// <summary>Entries whose SQL or database contains every whitespace-separated term (case-insensitive), newest first.</summary>
    public IReadOnlyList<HistoryEntry> Search(string connectionId, string query, int limit = 500)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Load(connectionId)
            .Where(e => terms.All(t => e.Sql.Contains(t, StringComparison.OrdinalIgnoreCase) || e.Database.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .Take(limit)
            .ToList();
    }

    public void Delete(string connectionId, string entryId) =>
        Rewrite(connectionId, entries => entries.Where(e => e.Id != entryId));

    public void Clear(string connectionId)
    {
        lock (_gate)
        {
            try
            {
                var file = paths.HistoryFile(connectionId);
                if (File.Exists(file)) File.Delete(file);
                _lineCounts[connectionId] = 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log?.Invoke("History clear failed: " + ex.Message);
            }
        }
    }

    private void Compact(string connectionId, string file)
    {
        var entries = ReadAll(file);
        var keep = entries.Skip(Math.Max(0, entries.Count - MaxEntries)).ToList();
        WriteAll(file, keep);
        _lineCounts[connectionId] = keep.Count;
    }

    private void Rewrite(string connectionId, Func<IEnumerable<HistoryEntry>, IEnumerable<HistoryEntry>> transform)
    {
        lock (_gate)
        {
            try
            {
                var file = paths.HistoryFile(connectionId);
                var entries = transform(ReadAll(file)).ToList();
                WriteAll(file, entries);
                _lineCounts[connectionId] = entries.Count;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log?.Invoke("History rewrite failed: " + ex.Message);
            }
        }
    }

    private static void WriteAll(string file, IEnumerable<HistoryEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var e in entries) sb.Append(JsonSerializer.Serialize(e, HistoryJsonContext.Default.HistoryEntry)).Append('\n');
        AtomicFile.WriteAllText(file, sb.ToString());
    }

    private List<HistoryEntry> ReadAll(string file)
    {
        var list = new List<HistoryEntry>();
        try
        {
            if (!File.Exists(file)) return list;
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    if (JsonSerializer.Deserialize(line, HistoryJsonContext.Default.HistoryEntry) is { } e) list.Add(e);
                }
                catch (JsonException)
                {
                    // Skip a torn or corrupt line.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke("History read failed: " + ex.Message);
        }
        return list;
    }

    private static int CountLines(string file)
    {
        var n = 0;
        foreach (var _ in File.ReadLines(file)) n++;
        return n;
    }
}
