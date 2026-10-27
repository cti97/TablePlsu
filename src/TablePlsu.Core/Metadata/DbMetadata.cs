using System.Text.Json.Serialization;

namespace TablePlsu.Core.Metadata;

/// <summary>pg_class.relkind of a table-like object: r, v, m, f, p.</summary>
public enum RelationKind { Table, View, MaterializedView, ForeignTable, PartitionedTable }

/// <summary>pg_proc.prokind: f, p, a, w.</summary>
public enum RoutineKind { Function, Procedure, Aggregate, Window }

/// <summary>pg_type.typtype of a user type: e (enum), d (domain), c (standalone composite).</summary>
public enum UserTypeKind { Enum, Domain, Composite }

/// <summary>
/// A snapshot of a database's catalog: schemas → tables/views/matviews, functions, sequences, types. Immutable once
/// published by <see cref="MetadataCache"/> (a refresh publishes a new instance), so it can be read from any thread.
/// Schemas with <see cref="SchemaInfo.IsLoaded"/> false were listed but their contents are not loaded yet (lazy mode).
/// </summary>
public sealed class DbMetadata
{
    public const int CurrentFormatVersion = 1;

    public static DbMetadata Empty(string database = "") => new() { Database = database };

    /// <summary>On-disk format version; caches with another version are ignored.</summary>
    public int FormatVersion { get; init; } = CurrentFormatVersion;

    public string Database { get; init; } = "";

    public string? ServerVersion { get; init; }

    /// <summary>When the newest part of this snapshot was read from the server (UTC). Default for an empty snapshot.</summary>
    public DateTimeOffset LoadedAt { get; init; }

    /// <summary><c>current_schemas(false)</c>: the effective search_path (existing schemas only). Empty means "public".</summary>
    public IReadOnlyList<string> SearchPath { get; init; } = [];

    /// <summary>Sorted by name (user schemas first is up to the UI).</summary>
    public IReadOnlyList<SchemaInfo> Schemas { get; init; } = [];

    [JsonIgnore]
    public bool IsEmpty => Schemas.Count == 0;

    /// <summary>The search path used for unqualified lookups: <see cref="SearchPath"/>, or ["public"] when empty.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> EffectiveSearchPath => SearchPath.Count > 0 ? SearchPath : DefaultSearchPath;

    private static readonly string[] DefaultSearchPath = ["public"];

    private Dictionary<string, SchemaInfo>? _schemaIndex;

    /// <summary>Exact (case-sensitive) name first, then a unique case-insensitive match.</summary>
    public SchemaInfo? FindSchema(string name)
    {
        var index = _schemaIndex ??= Schemas.GroupBy(s => s.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        if (index.TryGetValue(name, out var s)) return s;
        return UniqueOrNull(Schemas.Where(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Finds a table/view/matview/foreign/partitioned table. With <paramref name="schema"/> null, searches the
    /// <see cref="EffectiveSearchPath"/> in order, then falls back to a unique match in any loaded schema. Names match
    /// exactly first, then case-insensitively (unquoted identifiers fold to lower case in PostgreSQL).
    /// </summary>
    public TableInfo? FindTable(string? schema, string name)
    {
        if (schema is not null) return FindSchema(schema)?.FindTable(name);
        foreach (var s in EffectiveSearchPath)
            if (FindSchema(s)?.FindTable(name) is { } t) return t;
        var exact = UniqueOrNull(Schemas.Select(s => s.FindTableExact(name)).OfType<TableInfo>());
        return exact ?? UniqueOrNull(Schemas.Select(s => s.FindTable(name)).OfType<TableInfo>());
    }

    /// <summary>Functions/procedures named <paramref name="name"/> (all overloads), search-path aware like <see cref="FindTable"/>.</summary>
    public IReadOnlyList<FunctionInfo> FindFunctions(string? schema, string name)
    {
        if (schema is not null) return FindSchema(schema)?.FindFunctions(name) ?? [];
        foreach (var s in EffectiveSearchPath)
            if (FindSchema(s)?.FindFunctions(name) is { Count: > 0 } f) return f;
        return Schemas.SelectMany(s => s.FindFunctions(name)).ToList();
    }

    /// <summary>All loaded tables (every kind) across schemas.</summary>
    public IEnumerable<TableInfo> AllTables() => Schemas.SelectMany(s => s.Tables);

    public IEnumerable<FunctionInfo> AllFunctions() => Schemas.SelectMany(s => s.Functions);

    /// <summary>A copy with <paramref name="schema"/> replacing (or adding) the schema of the same name.</summary>
    public DbMetadata WithSchema(SchemaInfo schema, DateTimeOffset? loadedAt = null)
    {
        var list = Schemas.Where(s => s.Name != schema.Name).Append(schema).OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
        return new DbMetadata
        {
            Database = Database,
            ServerVersion = ServerVersion,
            LoadedAt = loadedAt ?? LoadedAt,
            SearchPath = SearchPath,
            Schemas = list,
        };
    }

    internal static T? UniqueOrNull<T>(IEnumerable<T> items) where T : class
    {
        T? found = null;
        foreach (var item in items)
        {
            if (found is not null) return null;
            found = item;
        }
        return found;
    }
}

public sealed class SchemaInfo
{
    public string Name { get; init; } = "";
    public string? Owner { get; init; }
    public string? Comment { get; init; }
    /// <summary>pg_catalog, information_schema (only listed when system schemas were requested).</summary>
    public bool IsSystem { get; init; }
    /// <summary>False when only the name is known (lazy mode, contents not loaded yet).</summary>
    public bool IsLoaded { get; init; }

    /// <summary>Tables, views, matviews, foreign and partitioned tables, sorted by name. Use <see cref="TableInfo.Kind"/>.</summary>
    public IReadOnlyList<TableInfo> Tables { get; init; } = [];
    /// <summary>Functions and procedures (all overloads), sorted by name then signature.</summary>
    public IReadOnlyList<FunctionInfo> Functions { get; init; } = [];
    public IReadOnlyList<SequenceInfo> Sequences { get; init; } = [];
    public IReadOnlyList<UserTypeInfo> Types { get; init; } = [];

    private Dictionary<string, TableInfo>? _tableIndex;
    private Dictionary<string, List<FunctionInfo>>? _functionIndex;

    internal TableInfo? FindTableExact(string name)
    {
        var index = _tableIndex ??= Tables.GroupBy(t => t.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        return index.GetValueOrDefault(name);
    }

    /// <summary>Exact name, then a unique case-insensitive match.</summary>
    public TableInfo? FindTable(string name) =>
        FindTableExact(name) ?? DbMetadata.UniqueOrNull(Tables.Where(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)));

    public IReadOnlyList<FunctionInfo> FindFunctions(string name)
    {
        var index = _functionIndex ??= Functions.GroupBy(f => f.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        if (index.TryGetValue(name, out var list)) return list;
        return Functions.Where(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public IEnumerable<TableInfo> TablesOfKind(RelationKind kind) => Tables.Where(t => t.Kind == kind);
}

public sealed class TableInfo
{
    public string Schema { get; init; } = "";
    public string Name { get; init; } = "";
    public RelationKind Kind { get; init; }
    public uint Oid { get; init; }
    public string? Owner { get; init; }
    public string? Comment { get; init; }
    /// <summary>A partition of a partitioned table (pg_class.relispartition).</summary>
    public bool IsPartition { get; init; }
    /// <summary>pg_class.reltuples; null when never analyzed/vacuumed (or for views).</summary>
    public long? EstimatedRows { get; init; }
    /// <summary>Catalog estimate of heap + indexes + toast size (relpages × block_size, as of the last VACUUM/ANALYZE; never stats files); null for views.</summary>
    public long? TotalBytes { get; init; }

    /// <summary>In attnum order.</summary>
    public IReadOnlyList<ColumnInfo> Columns { get; init; } = [];
    /// <summary>Primary key column names in key order; empty when there is no PK.</summary>
    public IReadOnlyList<string> PrimaryKey { get; init; } = [];
    public string? PrimaryKeyName { get; init; }
    public IReadOnlyList<ForeignKeyInfo> ForeignKeys { get; init; } = [];
    public IReadOnlyList<IndexInfo> Indexes { get; init; } = [];

    [JsonIgnore]
    public bool HasPrimaryKey => PrimaryKey.Count > 0;

    [JsonIgnore]
    public bool IsView => Kind is RelationKind.View or RelationKind.MaterializedView;

    /// <summary>"schema.name" unquoted, for display.</summary>
    [JsonIgnore]
    public string QualifiedName => Schema + "." + Name;

    public ColumnInfo? FindColumn(string name) =>
        Columns.FirstOrDefault(c => c.Name == name)
        ?? DbMetadata.UniqueOrNull(Columns.Where(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The foreign key whose (single or first) column is <paramref name="column"/>, if any.</summary>
    public ForeignKeyInfo? ForeignKeyFor(string column) => ForeignKeys.FirstOrDefault(fk => fk.Columns.Contains(column));
}

public sealed class ColumnInfo
{
    public string Name { get; init; } = "";
    /// <summary>pg_attribute.attnum (1-based; gaps where columns were dropped).</summary>
    public int Ordinal { get; init; }
    /// <summary><c>format_type(atttypid, atttypmod)</c>, e.g. "character varying(20)", "numeric(12,2)", "integer[]".</summary>
    public string DataType { get; init; } = "";
    public bool IsNullable { get; init; } = true;
    /// <summary>Default expression (<c>pg_get_expr</c>), e.g. "nextval('customers_id_seq'::regclass)"; null when none.</summary>
    public string? Default { get; init; }
    public string? Comment { get; init; }
    /// <summary>"a" (ALWAYS) or "d" (BY DEFAULT) for identity columns, else null.</summary>
    public string? Identity { get; init; }
    /// <summary>A generated (STORED) column; its expression is in <see cref="Default"/>.</summary>
    public bool IsGenerated { get; init; }
}

public sealed class ForeignKeyInfo
{
    public string Name { get; init; } = "";
    public IReadOnlyList<string> Columns { get; init; } = [];
    public string RefSchema { get; init; } = "";
    public string RefTable { get; init; } = "";
    public IReadOnlyList<string> RefColumns { get; init; } = [];
    /// <summary>"NO ACTION", "RESTRICT", "CASCADE", "SET NULL", "SET DEFAULT".</summary>
    public string OnUpdate { get; init; } = "NO ACTION";
    public string OnDelete { get; init; } = "NO ACTION";
    /// <summary><c>pg_get_constraintdef</c>, e.g. "FOREIGN KEY (customer_id) REFERENCES customers(id)".</summary>
    public string? Definition { get; init; }
}

public sealed class IndexInfo
{
    public string Name { get; init; } = "";
    public bool IsUnique { get; init; }
    public bool IsPrimary { get; init; }
    /// <summary>Access method: btree, hash, gin, gist, brin…</summary>
    public string Method { get; init; } = "";
    /// <summary>Key columns or expressions (<c>pg_get_indexdef(oid, n, true)</c>), in key order. INCLUDE columns are not listed.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];
    /// <summary>Partial index WHERE clause, or null.</summary>
    public string? Predicate { get; init; }
    /// <summary>Full <c>pg_get_indexdef</c> (CREATE [UNIQUE] INDEX …).</summary>
    public string Definition { get; init; } = "";
}

public sealed class FunctionInfo
{
    public string Schema { get; init; } = "";
    public string Name { get; init; } = "";
    public uint Oid { get; init; }
    /// <summary><c>pg_get_function_identity_arguments</c>, e.g. "x integer, y text" (empty for no arguments).</summary>
    public string Arguments { get; init; } = "";
    /// <summary><c>pg_get_function_result</c>, e.g. "integer", "SETOF record", "TABLE(a int)"; null for procedures.</summary>
    public string? ReturnType { get; init; }
    public RoutineKind Kind { get; init; }
    public string? Language { get; init; }
    public string? Comment { get; init; }

    /// <summary>"name(args)".</summary>
    [JsonIgnore]
    public string Signature => $"{Name}({Arguments})";
}

public sealed class SequenceInfo
{
    public string Schema { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>"bigint", "integer", "smallint".</summary>
    public string? DataType { get; init; }
    /// <summary>"table.column" that owns the sequence (serial / identity), or null.</summary>
    public string? OwnedBy { get; init; }
}

public sealed class UserTypeInfo
{
    public string Schema { get; init; } = "";
    public string Name { get; init; } = "";
    public UserTypeKind Kind { get; init; }
    /// <summary>Enum labels in sort order (enums only).</summary>
    public IReadOnlyList<string> EnumLabels { get; init; } = [];
    /// <summary>Underlying type of a domain, e.g. "text" or "numeric(10,2)".</summary>
    public string? BaseType { get; init; }
    /// <summary>Attributes of a composite type.</summary>
    public IReadOnlyList<ColumnInfo> Attributes { get; init; } = [];
    public string? Comment { get; init; }
}
