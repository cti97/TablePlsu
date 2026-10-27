using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Db;
using TablePlsu.Core.Ddl;
using TablePlsu.Core.Metadata;

namespace TablePlsu.App.Features.Structure;

/// <summary>Shared async helpers for the structure views and object editors (all DB work off the UI thread).</summary>
public static class StructureServices
{
    /// <summary>Shows a DDL error (message, title, detail). Replaceable by tests; defaults to <see cref="Dialogs.Error"/>.</summary>
    public static Action<string, string, string?> ReportError { get; set; } = (message, title, detail) => Dialogs.Error(message, title, detail: detail);

    /// <summary>
    /// The table from the metadata snapshot, loading its schema first when it is not loaded yet (lazy mode) and the
    /// workspace is connected. Null when it does not exist (or the metadata is not available yet).
    /// </summary>
    public static async Task<TableInfo?> FindTableAsync(IWorkspace ws, string schema, string table, CancellationToken ct = default)
    {
        var t = ws.Metadata.FindTable(schema, table);
        if (t is not null || ws.State != DbSessionState.Connected) return t;
        var loaded = ws.Metadata.FindSchema(schema);
        if (loaded is { IsLoaded: true } && ws.Metadata.HasData) return null;
        await ws.Metadata.EnsureSchemaLoadedAsync(schema, false, ct).ConfigureAwait(false);
        return ws.Metadata.FindTable(schema, table);
    }

    /// <summary>Constraint definitions and index comments; empty when not connected or on error (logged).</summary>
    public static async Task<TableDdlExtras> LoadExtrasAsync(IWorkspace ws, string schema, string table, CancellationToken ct = default)
    {
        if (ws.State != DbSessionState.Connected) return TableDdlExtras.Empty;
        try
        {
            return await Task.Run(() => ws.Session.WithMetadataConnectionAsync((c, t) => DdlLoader.LoadTableExtrasAsync(c, schema, table, t), ct), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException)
        {
            ws.Log($"Structure: could not load constraints of {schema}.{table}: {ex.Message}");
            return TableDdlExtras.Empty;
        }
    }

    /// <summary>
    /// Runs DDL statements through the Safe Mode gate, then in one transaction on a pooled connection. Reports errors in a
    /// dialog and the console. True when everything committed.
    /// </summary>
    public static async Task<bool> RunDdlAsync(IWorkspace ws, IReadOnlyList<string> statements, string what, bool transaction = true)
    {
        if (statements.Count == 0) return true;
        if (ws.State != DbSessionState.Connected)
        {
            ReportError("Not connected.", what, null);
            return false;
        }
        if (!await ws.SafeMode.ConfirmAsync(statements)) return false;
        try
        {
            await Task.Run(async () =>
            {
                await using var conn = await ws.Session.OpenConnectionAsync().ConfigureAwait(false);
                await DdlExecutor.ExecuteAsync(conn, statements, transaction).ConfigureAwait(false);
            });
            foreach (var s in statements) ws.Log(FirstLine(s) + " — OK");
            return true;
        }
        catch (DdlExecutionException ex)
        {
            var pg = ex.PostgresError;
            var msg = pg is null ? ex.InnerException?.Message ?? ex.Message : $"{pg.SqlState}: {pg.MessageText}";
            ws.Log($"{what} failed: {msg}");
            var detail = ex.Statement;
            if (pg?.Position is > 0 and var pos && pos <= ex.Statement.Length)
                detail = ex.Statement[..(pos - 1)] + " ⟶ " + ex.Statement[(pos - 1)..];
            if (pg?.Detail is { Length: > 0 } d) msg += "\n" + d;
            if (pg?.Hint is { Length: > 0 } h) msg += "\nHint: " + h;
            ReportError(msg, what + " failed", detail);
            return false;
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException or TimeoutException)
        {
            ws.Log($"{what} failed: {ex.Message}");
            ReportError(ex.Message, what + " failed", null);
            return false;
        }
    }

    private static string FirstLine(string sql)
    {
        var line = sql.TrimStart().Split('\n')[0].TrimEnd('\r', ' ', '(');
        return line.Length > 120 ? line[..120] + "…" : line;
    }
}
