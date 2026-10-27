using Npgsql;
using NpgsqlTypes;

namespace TablePlsu.Core.Metadata;

/// <summary>What <see cref="MetadataLoader.LoadAsync"/> reads.</summary>
public sealed record MetadataLoadOptions
{
    /// <summary>Also list (and load) pg_catalog and information_schema. pg_toast and temp schemas are never listed.</summary>
    public bool IncludeSystemSchemas { get; init; }

    /// <summary>
    /// Schemas whose contents to load. Null = every listed schema (whole-DB load). Empty = names only (lazy mode).
    /// Names that do not exist are ignored.
    /// </summary>
    public IReadOnlyCollection<string>? Schemas { get; init; }

    public static MetadataLoadOptions WholeDatabase { get; } = new();
}

/// <summary>
/// Reads <see cref="DbMetadata"/> straight from the system catalogs (pg_namespace, pg_class, pg_attribute, pg_index,
/// pg_constraint, pg_proc, pg_type…), never information_schema, in two round trips (one NpgsqlBatch for the contents).
/// Requires PostgreSQL 12+. The caller owns the connection (normally <c>DbSession.WithMetadataConnectionAsync</c>).
/// </summary>
public static class MetadataLoader
{
    private const string SystemSchemaFilter =
        "n.nspname not like 'pg\\_toast%' and n.nspname not like 'pg\\_temp\\_%' ";

    private const string UserSchemaFilter =
        "and n.nspname not like 'pg\\_%' and n.nspname <> 'information_schema' ";

    /// <summary>Lists schemas and loads the contents of the schemas selected by <paramref name="options"/>.</summary>
    public static async Task<DbMetadata> LoadAsync(NpgsqlConnection conn, MetadataLoadOptions? options = null, CancellationToken ct = default)
    {
        options ??= MetadataLoadOptions.WholeDatabase;
        var header = await LoadHeaderAsync(conn, options.IncludeSystemSchemas, ct).ConfigureAwait(false);

        var wanted = options.Schemas is null
            ? header.Schemas
            : header.Schemas.Where(s => options.Schemas.Contains(s.Name)).ToList();

        var loaded = wanted.Count == 0
            ? new Dictionary<string, SchemaInfo>()
            : (await LoadContentsAsync(conn, wanted, ct).ConfigureAwait(false)).ToDictionary(s => s.Name, StringComparer.Ordinal);

        var schemas = header.Schemas
            .Select(s => loaded.TryGetValue(s.Name, out var full) ? full : s.Info)
            .ToList();

        return new DbMetadata
        {
            Database = header.Database,
            ServerVersion = conn.PostgreSqlVersion.ToString(),
            LoadedAt = DateTimeOffset.UtcNow,
            SearchPath = header.SearchPath,
            Schemas = schemas,
        };
    }

    /// <summary>Loads the full contents of the named schemas (lazy expansion). Unknown names are skipped.</summary>
    public static async Task<IReadOnlyList<SchemaInfo>> LoadSchemasAsync(NpgsqlConnection conn, IReadOnlyCollection<string> schemaNames, CancellationToken ct = default)
    {
        if (schemaNames.Count == 0) return [];
        var header = await LoadHeaderAsync(conn, includeSystem: true, ct).ConfigureAwait(false);
        var wanted = header.Schemas.Where(s => schemaNames.Contains(s.Name)).ToList();
        if (wanted.Count == 0) return [];
        return await LoadContentsAsync(conn, wanted, ct).ConfigureAwait(false);
    }

    // ---- header: schemas + search_path ------------------------------------------------------------------------

    private sealed record SchemaRow(uint Oid, string Name, SchemaInfo Info);

    private sealed record Header(string Database, IReadOnlyList<string> SearchPath, List<SchemaRow> Schemas);

    private static async Task<Header> LoadHeaderAsync(NpgsqlConnection conn, bool includeSystem, CancellationToken ct)
    {
        var sql =
            "select current_database(), current_schemas(false);\n" +
            "select n.oid, n.nspname, pg_get_userbyid(n.nspowner), obj_description(n.oid, 'pg_namespace'), " +
            "(n.nspname like 'pg\\_%' or n.nspname = 'information_schema') " +
            "from pg_namespace n where " + SystemSchemaFilter + (includeSystem ? "" : UserSchemaFilter) +
            "order by n.nspname";
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await r.ReadAsync(ct).ConfigureAwait(false);
        var db = r.GetString(0);
        var path = r.IsDBNull(1) ? [] : r.GetFieldValue<string[]>(1);
        await r.NextResultAsync(ct).ConfigureAwait(false);
        var schemas = new List<SchemaRow>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var name = r.GetString(1);
            schemas.Add(new SchemaRow(r.GetFieldValue<uint>(0), name, new SchemaInfo
            {
                Name = name,
                Owner = Str(r, 2),
                Comment = Str(r, 3),
                IsSystem = r.GetBoolean(4),
                IsLoaded = false,
            }));
        }
        return new Header(db, path, schemas);
    }

    // ---- contents ---------------------------------------------------------------------------------------------

    private const string RelationsSql = """
        select c.oid, c.relnamespace, c.relname, c.relkind::text, pg_get_userbyid(c.relowner), obj_description(c.oid, 'pg_class'),
               c.relispartition, c.reltuples::float8,
               case when c.relkind in ('r','m','p','f') then pg_total_relation_size(c.oid) end
        from pg_class c
        where c.relnamespace = any($1) and c.relkind in ('r','v','m','f','p')
        """;

    private const string ColumnsSql = """
        select a.attrelid, a.attname, a.attnum, format_type(a.atttypid, a.atttypmod), not a.attnotnull,
               pg_get_expr(d.adbin, d.adrelid), col_description(a.attrelid, a.attnum), a.attidentity::text, a.attgenerated::text
        from pg_attribute a
        join pg_class c on c.oid = a.attrelid
        left join pg_attrdef d on d.adrelid = a.attrelid and d.adnum = a.attnum
        where c.relnamespace = any($1) and c.relkind in ('r','v','m','f','p','c') and a.attnum > 0 and not a.attisdropped
        order by a.attrelid, a.attnum
        """;

    private const string ConstraintsSql = """
        select con.conrelid, con.conname, con.contype::text,
               array(select a.attname from unnest(con.conkey) with ordinality k(n, o)
                     join pg_attribute a on a.attrelid = con.conrelid and a.attnum = k.n order by k.o)::text[],
               rn.nspname, rc.relname,
               array(select a.attname from unnest(con.confkey) with ordinality k(n, o)
                     join pg_attribute a on a.attrelid = con.confrelid and a.attnum = k.n order by k.o)::text[],
               con.confupdtype::text, con.confdeltype::text, pg_get_constraintdef(con.oid, true)
        from pg_constraint con
        join pg_class c on c.oid = con.conrelid
        left join pg_class rc on rc.oid = con.confrelid
        left join pg_namespace rn on rn.oid = rc.relnamespace
        where c.relnamespace = any($1) and con.contype in ('p', 'f')
        order by con.conrelid, con.conname
        """;

    private const string IndexesSql = """
        select i.indrelid, ic.relname, i.indisunique, i.indisprimary, am.amname,
               array(select pg_get_indexdef(i.indexrelid, k, true) from generate_series(1, i.indnkeyatts::int) k order by k)::text[],
               pg_get_expr(i.indpred, i.indrelid, true), pg_get_indexdef(i.indexrelid)
        from pg_index i
        join pg_class ic on ic.oid = i.indexrelid
        join pg_class t on t.oid = i.indrelid
        join pg_am am on am.oid = ic.relam
        where t.relnamespace = any($1)
        order by i.indrelid, i.indisprimary desc, ic.relname
        """;

    private const string FunctionsSql = """
        select p.oid, p.pronamespace, p.proname, pg_get_function_identity_arguments(p.oid),
               case when p.prokind = 'p' then null else pg_get_function_result(p.oid) end,
               p.prokind::text, l.lanname, obj_description(p.oid, 'pg_proc')
        from pg_proc p
        left join pg_language l on l.oid = p.prolang
        where p.pronamespace = any($1)
        order by p.proname, 4
        """;

    private const string SequencesSql = """
        select c.relnamespace, c.relname, format_type(s.seqtypid, null),
               (select t.relname || '.' || a.attname
                from pg_depend d
                join pg_class t on t.oid = d.refobjid
                join pg_attribute a on a.attrelid = d.refobjid and a.attnum = d.refobjsubid
                where d.classid = 'pg_class'::regclass and d.objid = c.oid and d.refclassid = 'pg_class'::regclass
                  and d.deptype in ('a', 'i') and d.refobjsubid > 0
                limit 1)
        from pg_class c
        join pg_sequence s on s.seqrelid = c.oid
        where c.relnamespace = any($1) and c.relkind = 'S'
        order by c.relname
        """;

    private const string TypesSql = """
        select t.oid, t.typnamespace, t.typname, t.typtype::text,
               case when t.typtype = 'd' then format_type(t.typbasetype, t.typtypmod) end,
               t.typrelid,
               case when t.typtype = 'e' then array(select e.enumlabel::text from pg_enum e where e.enumtypid = t.oid order by e.enumsortorder) end,
               obj_description(t.oid, 'pg_type')
        from pg_type t
        left join pg_class rc on rc.oid = t.typrelid
        where t.typnamespace = any($1)
          and (t.typtype in ('e', 'd') or (t.typtype = 'c' and rc.relkind = 'c'))
        order by t.typname
        """;

    private sealed class TableBuilder(TableInfo seed)
    {
        public TableInfo Seed { get; } = seed;
        public List<ColumnInfo> Columns { get; } = [];
        public List<ForeignKeyInfo> ForeignKeys { get; } = [];
        public List<IndexInfo> Indexes { get; } = [];
        public string[] PrimaryKey { get; set; } = [];
        public string? PrimaryKeyName { get; set; }

        public TableInfo Build() => new()
        {
            Schema = Seed.Schema,
            Name = Seed.Name,
            Kind = Seed.Kind,
            Oid = Seed.Oid,
            Owner = Seed.Owner,
            Comment = Seed.Comment,
            IsPartition = Seed.IsPartition,
            EstimatedRows = Seed.EstimatedRows,
            TotalBytes = Seed.TotalBytes,
            Columns = Columns,
            PrimaryKey = PrimaryKey,
            PrimaryKeyName = PrimaryKeyName,
            ForeignKeys = ForeignKeys,
            Indexes = Indexes,
        };
    }

    private static async Task<IReadOnlyList<SchemaInfo>> LoadContentsAsync(NpgsqlConnection conn, IReadOnlyList<SchemaRow> schemas, CancellationToken ct)
    {
        var oids = schemas.Select(s => s.Oid).ToArray();
        var nsName = schemas.ToDictionary(s => s.Oid, s => s.Name);

        await using var batch = new NpgsqlBatch(conn);
        foreach (var sql in new[] { RelationsSql, ColumnsSql, ConstraintsSql, IndexesSql, FunctionsSql, SequencesSql, TypesSql })
        {
            var cmd = new NpgsqlBatchCommand(sql);
            cmd.Parameters.Add(new NpgsqlParameter { Value = oids, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Oid });
            batch.BatchCommands.Add(cmd);
        }

        var tables = new Dictionary<uint, TableBuilder>();
        var compositeAttrs = new Dictionary<uint, List<ColumnInfo>>();
        var functions = new Dictionary<string, List<FunctionInfo>>();
        var sequences = new Dictionary<string, List<SequenceInfo>>();
        var types = new Dictionary<string, List<UserTypeInfo>>();

        await using (var r = await batch.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            // relations
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var oid = r.GetFieldValue<uint>(0);
                var kind = r.GetString(3) switch
                {
                    "v" => RelationKind.View,
                    "m" => RelationKind.MaterializedView,
                    "f" => RelationKind.ForeignTable,
                    "p" => RelationKind.PartitionedTable,
                    _ => RelationKind.Table,
                };
                var tuples = r.GetDouble(7);
                tables[oid] = new TableBuilder(new TableInfo
                {
                    Oid = oid,
                    Schema = nsName[r.GetFieldValue<uint>(1)],
                    Name = r.GetString(2),
                    Kind = kind,
                    Owner = Str(r, 4),
                    Comment = Str(r, 5),
                    IsPartition = r.GetBoolean(6),
                    EstimatedRows = kind is RelationKind.View || tuples < 0 ? null : (long)tuples,
                    TotalBytes = r.IsDBNull(8) ? null : r.GetInt64(8),
                });
            }

            // columns (tables and standalone composite types)
            await r.NextResultAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var rel = r.GetFieldValue<uint>(0);
                var identity = Str(r, 7);
                var generated = Str(r, 8);
                var col = new ColumnInfo
                {
                    Name = r.GetString(1),
                    Ordinal = r.GetInt16(2),
                    DataType = r.GetString(3),
                    IsNullable = r.GetBoolean(4),
                    Default = Str(r, 5),
                    Comment = Str(r, 6),
                    Identity = string.IsNullOrEmpty(identity) ? null : identity,
                    IsGenerated = !string.IsNullOrEmpty(generated),
                };
                if (tables.TryGetValue(rel, out var tb)) tb.Columns.Add(col);
                else
                {
                    if (!compositeAttrs.TryGetValue(rel, out var list)) compositeAttrs[rel] = list = [];
                    list.Add(col);
                }
            }

            // PK + FK constraints
            await r.NextResultAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!tables.TryGetValue(r.GetFieldValue<uint>(0), out var tb)) continue;
                var name = r.GetString(1);
                var cols = r.GetFieldValue<string[]>(3);
                if (r.GetString(2) == "p")
                {
                    tb.PrimaryKey = cols;
                    tb.PrimaryKeyName = name;
                    continue;
                }
                tb.ForeignKeys.Add(new ForeignKeyInfo
                {
                    Name = name,
                    Columns = cols,
                    RefSchema = Str(r, 4) ?? "",
                    RefTable = Str(r, 5) ?? "",
                    RefColumns = r.IsDBNull(6) ? [] : r.GetFieldValue<string[]>(6),
                    OnUpdate = FkAction(Str(r, 7)),
                    OnDelete = FkAction(Str(r, 8)),
                    Definition = Str(r, 9),
                });
            }

            // indexes
            await r.NextResultAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!tables.TryGetValue(r.GetFieldValue<uint>(0), out var tb)) continue;
                tb.Indexes.Add(new IndexInfo
                {
                    Name = r.GetString(1),
                    IsUnique = r.GetBoolean(2),
                    IsPrimary = r.GetBoolean(3),
                    Method = r.GetString(4),
                    Columns = r.GetFieldValue<string[]>(5),
                    Predicate = Str(r, 6),
                    Definition = r.GetString(7),
                });
            }

            // functions
            await r.NextResultAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var schema = nsName[r.GetFieldValue<uint>(1)];
                Add(functions, schema, new FunctionInfo
                {
                    Oid = r.GetFieldValue<uint>(0),
                    Schema = schema,
                    Name = r.GetString(2),
                    Arguments = Str(r, 3) ?? "",
                    ReturnType = Str(r, 4),
                    Kind = r.GetString(5) switch
                    {
                        "p" => RoutineKind.Procedure,
                        "a" => RoutineKind.Aggregate,
                        "w" => RoutineKind.Window,
                        _ => RoutineKind.Function,
                    },
                    Language = Str(r, 6),
                    Comment = Str(r, 7),
                });
            }

            // sequences
            await r.NextResultAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var schema = nsName[r.GetFieldValue<uint>(0)];
                Add(sequences, schema, new SequenceInfo { Schema = schema, Name = r.GetString(1), DataType = Str(r, 2), OwnedBy = Str(r, 3) });
            }

            // types
            await r.NextResultAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var schema = nsName[r.GetFieldValue<uint>(1)];
                var kind = r.GetString(3) switch { "e" => UserTypeKind.Enum, "d" => UserTypeKind.Domain, _ => UserTypeKind.Composite };
                var relid = r.GetFieldValue<uint>(5);
                Add(types, schema, new UserTypeInfo
                {
                    Schema = schema,
                    Name = r.GetString(2),
                    Kind = kind,
                    BaseType = Str(r, 4),
                    EnumLabels = r.IsDBNull(6) ? [] : r.GetFieldValue<string[]>(6),
                    Attributes = kind == UserTypeKind.Composite && compositeAttrs.TryGetValue(relid, out var attrs) ? attrs : [],
                    Comment = Str(r, 7),
                });
            }
        }

        var tablesBySchema = tables.Values
            .Select(t => t.Build())
            .GroupBy(t => t.Schema)
            .ToDictionary(g => g.Key, g => g.OrderBy(t => t.Name, StringComparer.Ordinal).ToList());

        return schemas.Select(s => new SchemaInfo
        {
            Name = s.Name,
            Owner = s.Info.Owner,
            Comment = s.Info.Comment,
            IsSystem = s.Info.IsSystem,
            IsLoaded = true,
            Tables = tablesBySchema.GetValueOrDefault(s.Name) ?? [],
            Functions = functions.GetValueOrDefault(s.Name) ?? [],
            Sequences = sequences.GetValueOrDefault(s.Name) ?? [],
            Types = types.GetValueOrDefault(s.Name) ?? [],
        }).ToList();
    }

    private static void Add<T>(Dictionary<string, List<T>> map, string key, T item)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        list.Add(item);
    }

    private static string FkAction(string? code) => code switch
    {
        "r" => "RESTRICT",
        "c" => "CASCADE",
        "n" => "SET NULL",
        "d" => "SET DEFAULT",
        _ => "NO ACTION",
    };

    private static string? Str(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
}
