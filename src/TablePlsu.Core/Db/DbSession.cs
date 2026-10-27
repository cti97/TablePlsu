using System.Diagnostics;
using Npgsql;
using TablePlsu.Core.Connections;
using TablePlsu.Core.Sql;

namespace TablePlsu.Core.Db;

public enum DbSessionState { Disconnected, Connecting, Connected, Broken }

/// <summary>Session tuning that comes from settings rather than the profile.</summary>
public sealed record DbSessionOptions
{
    /// <summary>settings.connections.keepAlive: ping idle connections every <see cref="KeepAliveInterval"/>.</summary>
    public bool KeepAlive { get; init; } = true;
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>settings.connections.commandTimeoutSeconds (0 = none); default for ExecuteOptions.</summary>
    public int CommandTimeoutSeconds { get; init; }
    public string ApplicationName { get; init; } = "TablePlsu";
}

/// <summary>
/// One open workspace's database access: the optional SSH tunnel, one <see cref="NpgsqlDataSource"/> (bootstrap SQL runs
/// on every physical connection), a dedicated query connection (session state like SET persists across runs) and a
/// separate metadata connection so the sidebar never waits for a running query.
/// <see cref="ExecuteAsync"/> splits SQL into statements, streams rows in batches, honours the row limit by cancelling the
/// rest server-side, and returns per-statement results. One execution at a time; all I/O is async and off the caller's context.
/// </summary>
public sealed class DbSession : IAsyncDisposable
{
    private readonly ConnectionSecrets _secrets;
    private readonly DbSessionOptions _options;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _execGate = new(1, 1);
    private readonly SemaphoreSlim _metaGate = new(1, 1);

    private SshTunnel? _tunnel;
    private NpgsqlDataSource? _dataSource;
    private NpgsqlConnection? _queryConn;
    private NpgsqlConnection? _metaConn;
    private volatile NpgsqlCommand? _currentCommand;
    private volatile List<QueryNotice>? _currentNotices;
    private volatile bool _cancelRequested;
    private CancellationTokenSource? _keepAliveCts;
    private volatile DbSessionState _state = DbSessionState.Disconnected;
    private int _busy;

    public DbSession(ConnectionProfile profile, ConnectionSecrets secrets, string? database = null, DbSessionOptions? options = null)
    {
        Profile = profile;
        _secrets = secrets;
        Database = string.IsNullOrEmpty(database) ? profile.Database : database;
        _options = options ?? new DbSessionOptions();
    }

    public ConnectionProfile Profile { get; }
    public string Database { get; }
    public DbSessionState State => _state;
    public string? ServerVersion { get; private set; }
    public string? LastError { get; private set; }
    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public bool IsTunnelled => _tunnel is not null;

    /// <summary>The pooled data source (valid while connected). Use <see cref="OpenConnectionAsync"/> for extra connections.</summary>
    public NpgsqlDataSource DataSource => _dataSource ?? throw new InvalidOperationException("Not connected.");

    public event EventHandler<DbSessionState>? StateChanged;

    /// <summary>Every statement sent (query, metadata, keep-alive failures), for the console log. Raised on background threads.</summary>
    public event EventHandler<string>? Log;

    /// <summary>Builds the Npgsql connection string for a profile (host/port overridden when tunnelled).</summary>
    public static string BuildConnectionString(ConnectionProfile profile, ConnectionSecrets secrets, string? database = null,
        string? hostOverride = null, int? portOverride = null, int commandTimeoutSeconds = 0, string applicationName = "TablePlsu")
    {
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = hostOverride ?? profile.Host,
            Port = portOverride ?? profile.Port,
            Username = profile.User,
            Database = string.IsNullOrEmpty(database) ? profile.Database : database,
            ApplicationName = applicationName,
            SslMode = profile.SslMode switch
            {
                SslModeOption.Disable => SslMode.Disable,
                SslModeOption.Allow => SslMode.Allow,
                SslModeOption.Require => SslMode.Require,
                SslModeOption.VerifyCA => SslMode.VerifyCA,
                SslModeOption.VerifyFull => SslMode.VerifyFull,
                _ => SslMode.Prefer,
            },
            CommandTimeout = commandTimeoutSeconds,
            IncludeErrorDetail = true,
            // Arrays containing NULLs are read as nullable element arrays instead of throwing.
            ArrayNullabilityMode = ArrayNullabilityMode.PerInstance,
            Pooling = true,
            Timeout = 15,
        };
        if (!string.IsNullOrEmpty(secrets.Password)) b.Password = secrets.Password;
        if (!string.IsNullOrEmpty(profile.SslClientCertFile)) b.SslCertificate = profile.SslClientCertFile;
        if (!string.IsNullOrEmpty(profile.SslClientKeyFile)) b.SslKey = profile.SslClientKeyFile;
        if (!string.IsNullOrEmpty(profile.SslRootCertFile)) b.RootCertificate = profile.SslRootCertFile;
        return b.ConnectionString;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync().ConfigureAwait(false);
            SetState(DbSessionState.Connecting);
            try
            {
                string? host = null;
                int? port = null;
                if (Profile.SshEnabled)
                {
                    _tunnel = await SshTunnel.OpenAsync(Profile, _secrets, _options.KeepAliveInterval, ct).ConfigureAwait(false);
                    host = _tunnel.LocalHost;
                    port = _tunnel.LocalPort;
                }

                var cs = BuildConnectionString(Profile, _secrets, Database, host, port, _options.CommandTimeoutSeconds, _options.ApplicationName);
                var builder = new NpgsqlDataSourceBuilder(cs);
                var bootstrap = Profile.Bootstrap;
                if (!string.IsNullOrWhiteSpace(bootstrap))
                {
                    builder.UsePhysicalConnectionInitializer(
                        conn =>
                        {
                            using var cmd = new NpgsqlCommand(bootstrap, conn);
                            cmd.ExecuteNonQuery();
                        },
                        async conn =>
                        {
                            await using var cmd = new NpgsqlCommand(bootstrap, conn);
                            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                        });
                }
                _dataSource = builder.Build();
                _queryConn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
                _queryConn.Notice += OnNotice;
                ServerVersion = _queryConn.ServerVersion;
                LastError = null;
                SetState(DbSessionState.Connected);
                StartKeepAlive();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                await DisconnectCoreAsync().ConfigureAwait(false);
                SetState(DbSessionState.Broken);
                throw;
            }
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>Tears everything down (including the tunnel) and connects again.</summary>
    public Task ReconnectAsync(CancellationToken ct = default)
    {
        Cancel();
        return ConnectAsync(ct);
    }

    public async Task DisconnectAsync()
    {
        Cancel();
        await _connectGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync().ConfigureAwait(false);
            SetState(DbSessionState.Disconnected);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>A new pooled connection (bootstrap applied) for commits, process list, etc. The caller disposes it.</summary>
    public async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct = default) =>
        await DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

    /// <summary>Runs <paramref name="work"/> on the dedicated metadata connection (serialized; reopened if broken).</summary>
    public async Task<T> WithMetadataConnectionAsync<T>(Func<NpgsqlConnection, CancellationToken, Task<T>> work, CancellationToken ct = default)
    {
        await _metaGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_metaConn is null || _metaConn.FullState.HasFlag(System.Data.ConnectionState.Broken) || _metaConn.State == System.Data.ConnectionState.Closed)
            {
                if (_metaConn is not null) await _metaConn.DisposeAsync().ConfigureAwait(false);
                _metaConn = await DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            }
            return await work(_metaConn, ct).ConfigureAwait(false);
        }
        finally
        {
            _metaGate.Release();
        }
    }

    /// <summary>Pings the query connection when idle. Returns false (and marks the session Broken) when it is dead.</summary>
    public async Task<bool> KeepAliveAsync(CancellationToken ct = default)
    {
        if (_state != DbSessionState.Connected || _queryConn is null) return false;
        if (!await _execGate.WaitAsync(0, ct).ConfigureAwait(false)) return true; // busy means alive
        try
        {
            await using var cmd = new NpgsqlCommand("SELECT 1", _queryConn) { CommandTimeout = 10 };
            await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            LastError = ex.Message;
            RaiseLog("keep-alive failed: " + ex.Message);
            SetState(DbSessionState.Broken);
            return false;
        }
        finally
        {
            _execGate.Release();
        }
    }

    /// <summary>
    /// Runs every statement in <paramref name="sql"/> on the query connection. Each statement reports
    /// <see cref="ResultBatch"/>es of up to <see cref="ExecuteOptions.BatchSize"/> rows and a final batch.
    /// Never throws for SQL errors or cancellation: they are recorded per statement. Use an ordered IProgress
    /// (not <see cref="Progress{T}"/>, which posts out of order to the thread pool when there is no sync context).
    /// </summary>
    public async Task<QueryResult> ExecuteAsync(string sql, ExecuteOptions? options = null, IProgress<ResultBatch>? progress = null, CancellationToken ct = default)
    {
        options ??= new ExecuteOptions { CommandTimeoutSeconds = _options.CommandTimeoutSeconds };
        var conn = _queryConn;
        if (_state != DbSessionState.Connected || conn is null) throw new InvalidOperationException("The session is not connected.");

        await _execGate.WaitAsync(ct).ConfigureAwait(false);
        Interlocked.Exchange(ref _busy, 1);
        _cancelRequested = false;
        var total = Stopwatch.StartNew();
        var results = new List<StatementResult>();
        try
        {
            using var reg = ct.Register(Cancel);
            var statements = StatementSplitter.Split(sql);
            for (var i = 0; i < statements.Count; i++)
            {
                if (_cancelRequested) break;
                var st = await RunStatementAsync(conn, sql, statements[i], i, options, progress).ConfigureAwait(false);
                results.Add(st);
                if (st.Cancelled) break;
                if (st.Error is not null)
                {
                    if (options.ReturnOnError) break;
                    if (conn.FullState.HasFlag(System.Data.ConnectionState.Broken)) { SetState(DbSessionState.Broken); break; }
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
            _execGate.Release();
        }
        return new QueryResult { Statements = results, Elapsed = total.Elapsed, Cancelled = _cancelRequested };
    }

    /// <summary>Sends a protocol cancel request for the running statement and stops the remaining ones.</summary>
    public void Cancel()
    {
        if (!IsBusy) return;
        _cancelRequested = true;
        var cmd = _currentCommand;
        if (cmd is null) return;
        _ = Task.Run(() =>
        {
            try { cmd.Cancel(); }
            catch (Exception ex) { RaiseLog("cancel failed: " + ex.Message); }
        });
    }

    private async Task<StatementResult> RunStatementAsync(NpgsqlConnection conn, string fullText, SqlStatement stmt, int index,
        ExecuteOptions options, IProgress<ResultBatch>? progress)
    {
        var result = new StatementResult { Index = index, Statement = stmt, Title = TabNameDirective.Parse(stmt.Text) };
        var limit = options.RowLimit is > 0 ? options.RowLimit.Value : int.MaxValue;
        var batchSize = Math.Max(1, options.BatchSize);
        var limitCancelled = false;
        var sw = Stopwatch.StartNew();
        RaiseLog(stmt.Text);

        var cmd = new NpgsqlCommand(stmt.Text, conn) { CommandTimeout = options.CommandTimeoutSeconds };
        _currentNotices = result.Notices;
        _currentCommand = cmd;
        NpgsqlDataReader? reader = null;
        try
        {
            if (_cancelRequested) { result.Cancelled = true; return result; }
            reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            if (reader.FieldCount > 0)
            {
                result.HasResultSet = true;
                var columns = new ResultColumn[reader.FieldCount];
                for (var c = 0; c < columns.Length; c++)
                    columns[c] = new ResultColumn(c, reader.GetName(c), reader.GetDataTypeName(c), reader.GetDataTypeOID(c));
                result.Columns = columns;

                var batch = new List<object?[]>(Math.Min(batchSize, 1024));
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    if (result.Rows.Count >= limit)
                    {
                        result.Truncated = true;
                        limitCancelled = true;
                        cmd.Cancel();
                        break;
                    }
                    var row = new object?[columns.Length];
                    for (var c = 0; c < row.Length; c++) row[c] = ReadValue(reader, c);
                    result.Rows.Add(row);
                    batch.Add(row);
                    if (batch.Count >= batchSize)
                    {
                        progress?.Report(new ResultBatch(index, stmt, columns, batch, result.Rows.Count, false, null));
                        batch = new List<object?[]>(batchSize);
                    }
                }
                if (batch.Count > 0) progress?.Report(new ResultBatch(index, stmt, columns, batch, result.Rows.Count, false, null));
            }
            await DisposeReaderAsync(reader, limitCancelled).ConfigureAwait(false);
            var affected = reader.RecordsAffected;
            reader = null;
            FillCommandInfo(result, affected, stmt);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.QueryCanceled && (_cancelRequested || limitCancelled))
        {
            result.Cancelled = _cancelRequested && !limitCancelled;
        }
        catch (PostgresException ex)
        {
            result.Error = ToError(ex, fullText, stmt);
        }
        catch (NpgsqlException ex) when (_cancelRequested && ex.InnerException is OperationCanceledException or TimeoutException)
        {
            result.Cancelled = true;
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or InvalidCastException or NotSupportedException or IOException)
        {
            result.Error = new QueryError(ex.Message, (ex as NpgsqlException)?.SqlState, null, null, null, null, null);
            if (conn.FullState.HasFlag(System.Data.ConnectionState.Broken)) LastError = ex.Message;
        }
        finally
        {
            if (reader is not null)
            {
                try { await DisposeReaderAsync(reader, true).ConfigureAwait(false); }
                catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or IOException) { /* connection state checked by caller */ }
            }
            _currentCommand = null;
            _currentNotices = null;
            await cmd.DisposeAsync().ConfigureAwait(false);
            result.Elapsed = sw.Elapsed;
        }

        progress?.Report(new ResultBatch(index, stmt, result.Columns, [], result.Rows.Count, true, result));
        return result;
    }

    private static async Task DisposeReaderAsync(NpgsqlDataReader reader, bool swallowCancel)
    {
        try
        {
            await reader.DisposeAsync().ConfigureAwait(false);
        }
        catch (PostgresException ex) when (swallowCancel && ex.SqlState == PostgresErrorCodes.QueryCanceled)
        {
            // Expected after stopping at the row limit.
        }
    }

    private static object? ReadValue(NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        try
        {
            return DbValueConverter.ToDisplayValue(reader.GetValue(ordinal));
        }
        catch (Exception ex) when (ex is InvalidCastException or NotSupportedException or OverflowException or ArgumentException)
        {
            return $"<unreadable {reader.GetDataTypeName(ordinal)}: {ex.Message}>";
        }
    }

    private static readonly HashSet<string> DmlTags = new(StringComparer.Ordinal) { "INSERT", "UPDATE", "DELETE", "MERGE", "COPY", "MOVE", "FETCH" };

    private static void FillCommandInfo(StatementResult result, int recordsAffected, SqlStatement stmt)
    {
        var tag = FirstKeyword(stmt.Text);
        result.CommandTag = tag;
        if (recordsAffected < 0) return;
        if (!result.HasResultSet || (tag is not null && DmlTags.Contains(tag)))
            result.AffectedRows = recordsAffected;
    }

    /// <summary>First word of the statement after leading comments, upper-cased ("CREATE", "DO", "VACUUM"…).</summary>
    internal static string? FirstKeyword(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            if (text.AsSpan(i).StartsWith("--"))
            {
                var nl = text.IndexOf('\n', i);
                i = nl < 0 ? text.Length : nl + 1;
                continue;
            }
            if (text.AsSpan(i).StartsWith("/*"))
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
                continue;
            }
            break;
        }
        var start = i;
        while (i < text.Length && char.IsLetter(text[i])) i++;
        return i > start ? text[start..i].ToUpperInvariant() : null;
    }

    internal static QueryError ToError(PostgresException ex, string fullText, SqlStatement stmt)
    {
        int? position = null, line = null, column = null;
        if (ex.Position > 0)
        {
            var abs = Math.Clamp(stmt.Start + ex.Position - 1, 0, fullText.Length);
            position = abs;
            var l = 1;
            var lineStart = 0;
            for (var k = 0; k < abs; k++)
                if (fullText[k] == '\n') { l++; lineStart = k + 1; }
            line = l;
            column = abs - lineStart + 1;
        }
        return new QueryError(ex.MessageText, ex.SqlState, ex.Detail, ex.Hint, position, line, column);
    }

    private void OnNotice(object? sender, NpgsqlNoticeEventArgs e)
    {
        var n = e.Notice;
        var notice = new QueryNotice(n.Severity, n.MessageText, n.Detail, n.Hint);
        var list = _currentNotices;
        if (list is not null)
        {
            lock (list) list.Add(notice);
        }
        RaiseLog($"{n.Severity}: {n.MessageText}");
    }

    private void StartKeepAlive()
    {
        if (!_options.KeepAlive || _options.KeepAliveInterval <= TimeSpan.Zero) return;
        var cts = new CancellationTokenSource();
        _keepAliveCts = cts;
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(_options.KeepAliveInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false))
                {
                    if (_state != DbSessionState.Connected) break;
                    if (!await KeepAliveAsync(cts.Token).ConfigureAwait(false)) break;
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    private async Task DisconnectCoreAsync()
    {
        _keepAliveCts?.Cancel();
        _keepAliveCts?.Dispose();
        _keepAliveCts = null;

        if (_queryConn is not null)
        {
            _queryConn.Notice -= OnNotice;
            try { await _queryConn.DisposeAsync().ConfigureAwait(false); } catch { /* broken connection */ }
            _queryConn = null;
        }
        await _metaGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_metaConn is not null)
            {
                try { await _metaConn.DisposeAsync().ConfigureAwait(false); } catch { /* broken connection */ }
                _metaConn = null;
            }
        }
        finally
        {
            _metaGate.Release();
        }
        if (_dataSource is not null)
        {
            try { await _dataSource.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
            _dataSource = null;
        }
        _tunnel?.Dispose();
        _tunnel = null;
    }

    private void SetState(DbSessionState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke(this, state);
    }

    private void RaiseLog(string message)
    {
        try { Log?.Invoke(this, message); } catch { /* never let a log handler break a query */ }
    }

    public async ValueTask DisposeAsync()
    {
        Cancel();
        await _connectGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync().ConfigureAwait(false);
            _state = DbSessionState.Disconnected;
        }
        finally
        {
            _connectGate.Release();
        }
    }
}
