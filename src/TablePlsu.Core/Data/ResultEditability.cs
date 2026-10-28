using Npgsql;
using TablePlsu.Core.Db;
using TablePlsu.Core.Metadata;

namespace TablePlsu.Core.Data;

/// <summary>
/// Whether an editor result can be edited inline (spec §10 "Edit results inline") and, when it can, what the edits apply
/// to. <see cref="Reason"/> is a short human sentence ("result combines columns from several tables") shown as
/// "Read-only — reason" when it cannot.
/// </summary>
public sealed class ResultEditInfo
{
    private ResultEditInfo(string? reason, TableInfo? table, ChangeTarget? target, IReadOnlyList<ColumnInfo?> columnInfos)
    {
        Reason = reason;
        Table = table;
        Target = target;
        ColumnInfos = columnInfos;
    }

    public bool IsEditable => Reason is null;

    /// <summary>Why the result is read-only, or null when it is editable.</summary>
    public string? Reason { get; }

    /// <summary>The source table (editable results only).</summary>
    public TableInfo? Table { get; }

    /// <summary>The change target: the source table, the BASE column names in result order (aliases resolved) and the
    /// result indexes of the primary-key columns. Null when read-only.</summary>
    public ChangeTarget? Target { get; }

    /// <summary>The source column of each result column (aligned with the result; empty when read-only).</summary>
    public IReadOnlyList<ColumnInfo?> ColumnInfos { get; }

    public static ResultEditInfo ReadOnly(string reason) => new(reason, null, null, []);

    internal static ResultEditInfo Editable(TableInfo table, ChangeTarget target, IReadOnlyList<ColumnInfo?> columns) => new(null, table, target, columns);

    /// <summary>The same verdict after checking the loaded rows: rows that repeat a key can't be told apart.</summary>
    public ResultEditInfo WithRows(IReadOnlyList<object?[]> rows) =>
        Target is null || ResultEditability.DuplicateKeyReason(rows, Target.KeyColumns) is not { } reason ? this : ReadOnly(reason);
}

/// <summary>
/// Decides editability from each column's provenance (<see cref="ResultColumn.TableOid"/> / <see cref="ResultColumn.AttributeNumber"/>,
/// straight from the server's RowDescription; the SQL is never parsed or rewritten). A result is editable when it is a
/// SELECT / TABLE / WITH result whose every column is a distinct plain column of ONE ordinary or partitioned table that has
/// a primary key, every key column is in the result, and every key type round-trips exactly through the grid's values
/// (rows are matched by those values in <c>WHERE pk = …</c>).
/// </summary>
public static class ResultEditability
{
    /// <summary>Key types whose grid value (the CLR value read by Npgsql) is sent back as exactly the stored value.</summary>
    private static readonly HashSet<string> ExactKeyTypes = new(StringComparer.Ordinal)
    {
        "smallint", "integer", "bigint", "int2", "int4", "int8", "oid",
        "text", "character varying", "varchar", "character", "bpchar", "name",
        "uuid", "date", "boolean", "bool",
    };

    private static readonly HashSet<string> SelectTags = new(StringComparer.Ordinal) { "SELECT", "TABLE", "WITH" };

    private static readonly HashSet<string> SystemSchemas = new(StringComparer.Ordinal) { "pg_catalog", "information_schema", "pg_toast" };

    /// <summary>
    /// Full analysis: finds the single source table by OID through <paramref name="findTable"/> (see
    /// <see cref="FindTableByOidAsync"/>) and applies <see cref="Analyze"/>.
    /// </summary>
    public static async Task<ResultEditInfo> AnalyzeAsync(string? commandTag, IReadOnlyList<ResultColumn> columns,
        Func<uint, CancellationToken, Task<TableInfo?>> findTable, CancellationToken ct = default)
    {
        if (SourceReason(commandTag, columns, out var oid) is { } reason) return ResultEditInfo.ReadOnly(reason);
        TableInfo? table;
        try
        {
            table = await findTable(oid, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            return ResultEditInfo.ReadOnly("table metadata isn't available (" + ex.Message + ")");
        }
        return Analyze(commandTag, columns, table);
    }

    /// <summary>The rules, given the table the columns come from (null = not found).</summary>
    public static ResultEditInfo Analyze(string? commandTag, IReadOnlyList<ResultColumn> columns, TableInfo? table)
    {
        if (SourceReason(commandTag, columns, out var oid) is { } reason) return ResultEditInfo.ReadOnly(reason);
        if (table is null) return ResultEditInfo.ReadOnly("table metadata isn't available");
        if (IsSystemSchema(table.Schema)) return ResultEditInfo.ReadOnly("result comes from a system catalog");
        // Commits run on another connection, which can't see the query session's temporary tables.
        if (IsTempSchema(table.Schema)) return ResultEditInfo.ReadOnly("result comes from a temporary table");
        switch (table.Kind)
        {
            case RelationKind.View: return ResultEditInfo.ReadOnly("result comes from a view");
            case RelationKind.MaterializedView: return ResultEditInfo.ReadOnly("result comes from a materialized view");
            case RelationKind.ForeignTable: return ResultEditInfo.ReadOnly("result comes from a foreign table");
        }
        var stale = $"table metadata for {table.QualifiedName} is out of date (reload with Ctrl+R)";
        if (table.Oid != 0 && table.Oid != oid) return ResultEditInfo.ReadOnly(stale);

        var infos = new ColumnInfo?[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            var att = columns[i].AttributeNumber;
            infos[i] = table.Columns.FirstOrDefault(c => c.Ordinal == att);
            if (infos[i] is null) return ResultEditInfo.ReadOnly(stale);
        }
        if (!table.HasPrimaryKey) return ResultEditInfo.ReadOnly($"table {table.QualifiedName} has no primary key");
        var keys = new int[table.PrimaryKey.Count];
        for (var k = 0; k < keys.Length; k++)
        {
            var name = table.PrimaryKey[k];
            keys[k] = Array.FindIndex(infos, c => c!.Name == name);
            if (keys[k] < 0) return ResultEditInfo.ReadOnly($"primary key column {name} is not in the result");
            var type = BaseTypeName(columns[keys[k]].DataTypeName);
            if (!ExactKeyTypes.Contains(type)) return ResultEditInfo.ReadOnly($"primary key type {type} can't be matched exactly");
        }
        var target = new ChangeTarget(table.Schema, table.Name, [.. infos.Select(c => c!.Name)], keys);
        return ResultEditInfo.Editable(table, target, infos);
    }

    /// <summary>The checks that need only the columns: a SELECT, every column a plain column, one table, no repeats.</summary>
    private static string? SourceReason(string? commandTag, IReadOnlyList<ResultColumn> columns, out uint oid)
    {
        oid = 0;
        if (commandTag is null || !SelectTags.Contains(commandTag)) return "only SELECT results can be edited";
        if (columns.Count == 0) return "the result has no columns";
        foreach (var c in columns)
            if (!c.IsTableColumn) return $"column {c.Name} is computed, not read from a table";
        oid = columns[0].TableOid;
        foreach (var c in columns)
            if (c.TableOid != oid) return "result combines columns from several tables";
        var seen = new HashSet<short>();
        foreach (var c in columns)
            if (!seen.Add(c.AttributeNumber)) return $"column {c.Name} appears more than once";
        return null;
    }

    /// <summary>"character varying(20)" → "character varying"; "integer" stays.</summary>
    private static string BaseTypeName(string dataTypeName)
    {
        var paren = dataTypeName.IndexOf('(');
        return (paren < 0 ? dataTypeName : dataTypeName[..paren]).Trim();
    }

    /// <summary>A reason when two rows carry the same key values (rows can't be addressed one by one), else null.</summary>
    public static string? DuplicateKeyReason(IReadOnlyList<object?[]> rows, IReadOnlyList<int> keyColumns)
    {
        var seen = new HashSet<RowKey>();
        foreach (var row in rows)
            if (!seen.Add(new RowKey(keyColumns.Select(k => row[k])))) return "rows repeat the same primary key (a join?)";
        return null;
    }

    /// <summary>
    /// The whole pipeline for a finished editor result: <see cref="AnalyzeAsync"/> with the table found through
    /// <see cref="FindTableByOidAsync"/>, then <see cref="ResultEditInfo.WithRows"/> and <see cref="VerifyRowsAsync"/>.
    /// </summary>
    public static async Task<ResultEditInfo> AnalyzeResultAsync(string? commandTag, IReadOnlyList<ResultColumn> columns,
        IReadOnlyList<object?[]> rows, MetadataCache cache, DbSession session, CancellationToken ct = default)
    {
        var info = await AnalyzeAsync(commandTag, columns, (oid, c) => FindTableByOidAsync(cache, session, oid, c), ct).ConfigureAwait(false);
        if (info.IsEditable) info = info.WithRows(rows);
        if (info.IsEditable) info = await VerifyRowsAsync(info, rows, session, ct).ConfigureAwait(false);
        return info;
    }

    /// <summary>Rows re-read per verification query (one text[] parameter per key column, so the size only bounds memory).</summary>
    private const int VerifyChunk = 5000;

    /// <summary>
    /// Re-reads the loaded rows from the source table by primary key (on a fresh connection, i.e. what a commit will see)
    /// and compares every shown column. RowDescription provenance can't tell two range-table entries of the same table
    /// apart, so a self-join / LATERAL / CTE that shows another row's column under this row's key passes
    /// <see cref="Analyze"/>; an UPDATE keyed by the shown key would then write the wrong row. Any row whose values differ
    /// (that case, or a row changed or uncommitted since the query ran) or that is gone makes the result read-only.
    /// </summary>
    public static async Task<ResultEditInfo> VerifyRowsAsync(ResultEditInfo info, IReadOnlyList<object?[]> rows, DbSession session, CancellationToken ct = default)
    {
        if (info.Target is not { } target || rows.Count == 0) return info;
        try
        {
            await using var conn = await session.OpenConnectionAsync(ct).ConfigureAwait(false);
            for (var start = 0; start < rows.Count; start += VerifyChunk)
            {
                var chunk = rows.Skip(start).Take(VerifyChunk).ToList();
                if (!await ChunkMatchesAsync(conn, target, info.Table!, chunk, ct).ConfigureAwait(false))
                    return ResultEditInfo.ReadOnly("shown values don't match the table's rows (a self-join, or rows changed since the query ran)");
            }
            return info;
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            return ResultEditInfo.ReadOnly("couldn't re-read the rows to check them (" + ex.Message + ")");
        }
    }

    private static async Task<bool> ChunkMatchesAsync(NpgsqlConnection conn, ChangeTarget target, TableInfo table, List<object?[]> rows, CancellationToken ct)
    {
        var keys = target.KeyColumns;
        var keyTypes = keys.Select(k => KeyCastType(table.Columns.First(c => c.Name == target.Columns[k]).DataType)).ToList();
        var sql = new System.Text.StringBuilder("SELECT ")
            .Append(string.Join(", ", target.Columns.Select(TablePlsu.Core.Sql.SqlIdent.Quote)))
            .Append(" FROM ").Append(TablePlsu.Core.Sql.SqlIdent.Quote(target.Schema)).Append('.').Append(TablePlsu.Core.Sql.SqlIdent.Quote(target.Table))
            .Append(" WHERE (").Append(string.Join(", ", keys.Select(k => TablePlsu.Core.Sql.SqlIdent.Quote(target.Columns[k])))).Append(") IN (SELECT ")
            .Append(string.Join(", ", keys.Select((_, i) => $"u.k{i}::{keyTypes[i]}")))
            .Append(" FROM unnest(").Append(string.Join(", ", keys.Select((_, i) => $"${i + 1}::text[]")))
            .Append(") AS u(").Append(string.Join(", ", keys.Select((_, i) => $"k{i}"))).Append("))");
        await using var cmd = new NpgsqlCommand(sql.ToString(), conn);
        for (var i = 0; i < keys.Count; i++)
        {
            var k = keys[i];
            cmd.Parameters.Add(new NpgsqlParameter
            {
                Value = rows.Select(r => KeyText(r[k])).ToArray(),
                NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text,
            });
        }
        var current = new Dictionary<RowKey, object?[]>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var row = new object?[target.Columns.Count];
                for (var c = 0; c < row.Length; c++) row[c] = ReadValue(reader, c);
                current[new RowKey(keys.Select(k => row[k]))] = row;
            }
        }
        foreach (var row in rows)
        {
            if (!current.TryGetValue(new RowKey(keys.Select(k => row[k])), out var now)) return false;
            for (var c = 0; c < now.Length; c++)
                if (!CellEquals(row[c], now[c])) return false;
        }
        return true;
    }

    /// <summary>The type a key's text is cast back to: its own (always one of <see cref="ExactKeyTypes"/>, so safe to
    /// inline), without a length (char(n) as bpchar).</summary>
    private static string KeyCastType(string dataType) => BaseTypeName(dataType) switch
    {
        "character" or "bpchar" => "bpchar",
        "character varying" or "varchar" => "text",
        var t when ExactKeyTypes.Contains(t) => t,
        _ => "text",
    };

    private static string? KeyText(object? value) => value switch
    {
        null => null,
        bool b => b ? "true" : "false",
        DateOnly d => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        DateTime d => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    /// <summary>Same as the editor's row reader (DbSession): display values, unreadable values as a marker string.</summary>
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

    /// <summary>Value equality, structural for collections (bit strings, hstore…).</summary>
    internal static bool CellEquals(object? a, object? b)
    {
        if (Equals(a, b)) return true;
        if (a is null || b is null || a is string || b is string) return false;
        if (a is System.Collections.IEnumerable ea && b is System.Collections.IEnumerable eb)
        {
            var ia = ea.GetEnumerator();
            var ib = eb.GetEnumerator();
            while (true)
            {
                var ma = ia.MoveNext();
                if (ma != ib.MoveNext()) return false;
                if (!ma) return true;
                if (!CellEquals(ia.Current, ib.Current)) return false;
            }
        }
        return false;
    }

    /// <summary>
    /// The table/view with <paramref name="oid"/>: from the metadata snapshot (checked against the catalog's current
    /// column names and primary key, and re-loaded when they differ: a rename would otherwise make the generated UPDATE
    /// address other columns), else the relation's schema is looked up (one catalog query on the metadata connection) and
    /// loaded (re-loaded when it was loaded without this relation, i.e. stale). System catalogs are returned as a bare
    /// <see cref="TableInfo"/> without loading pg_catalog. Null when the relation no longer exists or its metadata can't be
    /// brought up to date.
    /// </summary>
    public static async Task<TableInfo?> FindTableByOidAsync(MetadataCache cache, DbSession session, uint oid, CancellationToken ct = default)
    {
        if (Find(cache.Current, oid) is { } known)
        {
            var shape = await CurrentShapeAsync(session, oid, ct).ConfigureAwait(false);
            if (shape is null) return null;
            if (Matches(known, shape.Value)) return known;
            await cache.EnsureSchemaLoadedAsync(known.Schema, force: true, ct).ConfigureAwait(false);
            return Find(cache.Current, oid) is { } reloaded && Matches(reloaded, shape.Value) ? reloaded : null;
        }
        var rel = await session.WithMetadataConnectionAsync(async (conn, c) =>
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT n.nspname, c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.oid = $1::oid", conn);
            cmd.Parameters.Add(new NpgsqlParameter { Value = (long)oid, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint });
            await using var reader = await cmd.ExecuteReaderAsync(c).ConfigureAwait(false);
            return await reader.ReadAsync(c).ConfigureAwait(false) ? (reader.GetString(0), reader.GetString(1)) : ((string, string)?)null;
        }, ct).ConfigureAwait(false);
        if (rel is not { } r) return null;
        if (IsSystemSchema(r.Item1) || IsTempSchema(r.Item1))
            return new TableInfo { Schema = r.Item1, Name = r.Item2, Oid = oid };
        var loaded = cache.Current.FindSchema(r.Item1) is { IsLoaded: true };
        await cache.EnsureSchemaLoadedAsync(r.Item1, force: loaded, ct).ConfigureAwait(false);
        return Find(cache.Current, oid);
    }

    /// <summary>The relation's current columns (attnum → name) and primary-key column names in key order; null when gone.</summary>
    private static Task<(Dictionary<int, string> Columns, List<string> Key)?> CurrentShapeAsync(DbSession session, uint oid, CancellationToken ct) =>
        session.WithMetadataConnectionAsync(async (conn, c) =>
        {
            await using var cmd = new NpgsqlCommand(
                """
                SELECT a.attnum, a.attname,
                       (SELECT array_position(i.indkey::int2[], a.attnum) FROM pg_index i WHERE i.indrelid = a.attrelid AND i.indisprimary)
                FROM pg_attribute a
                WHERE a.attrelid = $1::oid AND a.attnum > 0 AND NOT a.attisdropped
                """, conn);
            cmd.Parameters.Add(new NpgsqlParameter { Value = (long)oid, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint });
            var columns = new Dictionary<int, string>();
            var key = new List<(int Pos, string Name)>();
            await using var reader = await cmd.ExecuteReaderAsync(c).ConfigureAwait(false);
            while (await reader.ReadAsync(c).ConfigureAwait(false))
            {
                var name = reader.GetString(1);
                columns[reader.GetInt16(0)] = name;
                if (!reader.IsDBNull(2)) key.Add((reader.GetInt32(2), name));
            }
            return columns.Count == 0 ? ((Dictionary<int, string>, List<string>)?)null : (columns, key.OrderBy(k => k.Pos).Select(k => k.Name).ToList());
        }, ct);

    private static bool Matches(TableInfo table, (Dictionary<int, string> Columns, List<string> Key) shape) =>
        table.Columns.Count == shape.Columns.Count
        && table.Columns.All(c => shape.Columns.TryGetValue(c.Ordinal, out var name) && name == c.Name)
        && table.PrimaryKey.SequenceEqual(shape.Key);

    private static bool IsSystemSchema(string schema) => SystemSchemas.Contains(schema) || schema.StartsWith("pg_toast", StringComparison.Ordinal);

    private static bool IsTempSchema(string schema) => schema.StartsWith("pg_temp", StringComparison.Ordinal);

    private static TableInfo? Find(DbMetadata md, uint oid)
    {
        foreach (var t in md.AllTables())
            if (t.Oid == oid) return t;
        return null;
    }
}
