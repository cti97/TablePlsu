using System.Diagnostics;
using Npgsql;
using TablePlsu.Core.Db;

namespace TablePlsu.Core.Data;

/// <summary>
/// One page of table data. <see cref="KeyTexts"/> (aligned with <see cref="Rows"/>) holds the primary-key values as the
/// server's text when the query asked for them (<see cref="TableQuery.KeyColumns"/>), else null.
/// </summary>
public sealed record TablePage(IReadOnlyList<ResultColumn> Columns, List<object?[]> Rows, TimeSpan Elapsed)
{
    public List<string?[]>? KeyTexts { get; init; }
}

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
        var keyCount = Math.Clamp(spec.KeyTextColumns, 0, reader.FieldCount);
        var columns = new ResultColumn[reader.FieldCount - keyCount];
        for (var c = 0; c < columns.Length; c++)
            columns[c] = new ResultColumn(c, reader.GetName(c), reader.GetDataTypeName(c), reader.GetDataTypeOID(c));
        var rows = new List<object?[]>();
        var keys = keyCount > 0 ? new List<string?[]>() : null;
        var inet = columns.Select(c => c.DataTypeName == "inet").ToArray();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = new object?[columns.Length];
            for (var c = 0; c < row.Length; c++) row[c] = inet[c] ? ReadInet(reader, c) : ReadValue(reader, c);
            rows.Add(row);
            if (keys is null) continue;
            var key = new string?[keyCount];
            for (var k = 0; k < keyCount; k++)
                key[k] = reader.IsDBNull(columns.Length + k) ? null : reader.GetString(columns.Length + k);
            keys.Add(key);
        }
        return new TablePage(columns, rows, sw.Elapsed) { KeyTexts = keys };
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

    /// <summary>inet as PostgreSQL prints it: the provider's default (IPAddress) drops the netmask ("10.0.0.1/8" → "10.0.0.1").</summary>
    private static object? ReadInet(NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        try
        {
            var inet = reader.GetFieldValue<NpgsqlTypes.NpgsqlInet>(ordinal);
            var full = inet.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
            return inet.Netmask == full ? inet.Address.ToString() : $"{inet.Address}/{inet.Netmask}";
        }
        catch (Exception ex) when (ex is InvalidCastException or NotSupportedException)
        {
            return ReadValue(reader, ordinal);
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
            if (TryReadNullableArray(reader, ordinal) is { } array) return DbValueConverter.ToDisplayValue(array);
            return $"<unreadable {reader.GetDataTypeName(ordinal)}: {ex.Message}>";
        }
    }

    /// <summary>A value-type array with NULL elements (int[] '{1,NULL}'): the default T[] read throws; read it as T?[].</summary>
    private static Array? TryReadNullableArray(NpgsqlDataReader reader, int ordinal)
    {
        try
        {
            // GetFieldType says only "System.Array" for arrays; the element types below are Npgsql's defaults.
            Type? element = reader.GetDataTypeName(ordinal) switch
            {
                "smallint[]" => typeof(short),
                "integer[]" => typeof(int),
                "bigint[]" => typeof(long),
                "oid[]" => typeof(uint),
                "numeric[]" => typeof(decimal),
                "real[]" => typeof(float),
                "double precision[]" => typeof(double),
                "boolean[]" => typeof(bool),
                "uuid[]" => typeof(Guid),
                "date[]" or "timestamp with time zone[]" or "timestamp without time zone[]" => typeof(DateTime),
                "time without time zone[]" or "interval[]" => typeof(TimeSpan),
                "time with time zone[]" => typeof(DateTimeOffset),
                _ => null,
            };
            if (element is null) return null;
            var method = typeof(NpgsqlDataReader).GetMethod(nameof(NpgsqlDataReader.GetFieldValue), [typeof(int)])!
                .MakeGenericMethod(typeof(Nullable<>).MakeGenericType(element).MakeArrayType());
            return method.Invoke(reader, [ordinal]) as Array;
        }
        catch (Exception ex) when (ex is InvalidCastException or NotSupportedException or ArgumentException or System.Reflection.TargetInvocationException)
        {
            return null;
        }
    }
}
