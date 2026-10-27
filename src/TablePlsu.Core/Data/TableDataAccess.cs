using System.Diagnostics;
using Npgsql;
using TablePlsu.Core.Db;

namespace TablePlsu.Core.Data;

/// <summary>One page of table data.</summary>
public sealed record TablePage(IReadOnlyList<ResultColumn> Columns, List<object?[]> Rows, TimeSpan Elapsed);

/// <summary>Outcome of <see cref="TableDataAccess.CommitAsync"/>.</summary>
public sealed record CommitResult
{
    public bool Success { get; init; }
    /// <summary>Index of the statement that failed (-1 when none).</summary>
    public int FailedIndex { get; init; } = -1;
    public string? Error { get; init; }
    public IReadOnlyList<long> Affected { get; init; } = [];
    public TimeSpan Elapsed { get; init; }
}

/// <summary>
/// The table view's DB access, all async on a caller-supplied connection: page fetch, exact and estimated counts, and the
/// one-transaction commit of staged changes.
/// </summary>
public static class TableDataAccess
{
    public static async Task<TablePage> LoadPageAsync(NpgsqlConnection conn, SqlCommandSpec spec, int commandTimeoutSeconds = 0, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        await using var cmd = spec.CreateCommand(conn);
        cmd.CommandTimeout = commandTimeoutSeconds;
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var columns = new ResultColumn[reader.FieldCount];
        for (var c = 0; c < columns.Length; c++)
            columns[c] = new ResultColumn(c, reader.GetName(c), reader.GetDataTypeName(c), reader.GetDataTypeOID(c));
        var rows = new List<object?[]>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = new object?[columns.Length];
            for (var c = 0; c < row.Length; c++) row[c] = ReadValue(reader, c);
            rows.Add(row);
        }
        return new TablePage(columns, rows, sw.Elapsed);
    }

    public static async Task<long> CountAsync(NpgsqlConnection conn, SqlCommandSpec countSpec, int commandTimeoutSeconds = 0, CancellationToken ct = default)
    {
        await using var cmd = countSpec.CreateCommand(conn);
        cmd.CommandTimeout = commandTimeoutSeconds;
        var v = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary><c>pg_class.reltuples</c> for the relation, or null when never analyzed / unknown.</summary>
    public static async Task<long?> EstimateRowsAsync(NpgsqlConnection conn, string schema, string table, CancellationToken ct = default)
    {
        const string sql = "SELECT c.reltuples::bigint FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = $1 AND c.relname = $2";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = schema });
        cmd.Parameters.Add(new NpgsqlParameter { Value = table });
        var v = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (v is null or DBNull) return null;
        var n = Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture);
        return n < 0 ? null : n;
    }

    /// <summary>
    /// Runs every statement in ONE transaction. Any error (or an UPDATE/DELETE that does not affect exactly its
    /// <see cref="SqlCommandSpec.ExpectedRows"/>) rolls everything back and reports the failing statement.
    /// </summary>
    public static async Task<CommitResult> CommitAsync(NpgsqlConnection conn, IReadOnlyList<SqlCommandSpec> statements, int commandTimeoutSeconds = 0, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var affected = new List<long>();
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        for (var i = 0; i < statements.Count; i++)
        {
            var spec = statements[i];
            string? error = null;
            try
            {
                await using var cmd = spec.CreateCommand(conn, tx);
                cmd.CommandTimeout = commandTimeoutSeconds;
                var n = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                affected.Add(n);
                if (spec.ExpectedRows is { } expected && n != expected)
                    error = $"Statement {i + 1} affected {n} row(s), expected {expected} (the row was changed or deleted by someone else?).";
            }
            catch (PostgresException ex)
            {
                error = ex.MessageText + (string.IsNullOrEmpty(ex.Detail) ? "" : "\n" + ex.Detail);
            }
            catch (NpgsqlException ex)
            {
                error = ex.Message;
            }
            if (error is null) continue;
            try { await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception rex) when (rex is NpgsqlException or InvalidOperationException) { /* connection broken: nothing was committed */ }
            return new CommitResult { Success = false, FailedIndex = i, Error = error, Affected = affected, Elapsed = sw.Elapsed };
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new CommitResult { Success = true, Affected = affected, Elapsed = sw.Elapsed };
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
}
