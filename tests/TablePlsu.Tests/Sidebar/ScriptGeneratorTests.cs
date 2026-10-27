using Npgsql;
using TablePlsu.Core.Db;
using TablePlsu.Core.Metadata;
using TablePlsu.Core.Sidebar;
using TablePlsu.Tests.Infrastructure;
using Xunit.Abstractions;

namespace TablePlsu.Tests.Sidebar;

public class ScriptGeneratorTests(ITestOutputHelper output)
{
    private static TableInfo Orders() => new()
    {
        Schema = "sales",
        Name = "Orders",
        Kind = RelationKind.Table,
        PrimaryKey = ["id"],
        PrimaryKeyName = "orders_pkey",
        Comment = "It's orders",
        Columns =
        [
            new ColumnInfo { Name = "id", Ordinal = 1, DataType = "bigint", IsNullable = false, Identity = "a" },
            new ColumnInfo { Name = "total", Ordinal = 2, DataType = "numeric(12,2)", IsNullable = false, Default = "0" },
            new ColumnInfo { Name = "order", Ordinal = 4, DataType = "numeric", IsGenerated = true, Default = "(total * (2)::numeric)" },
            new ColumnInfo { Name = "customer_id", Ordinal = 3, DataType = "integer", Comment = "who" },
        ],
        ForeignKeys =
        [
            new ForeignKeyInfo
            {
                Name = "orders_customer_fk", Columns = ["customer_id"], RefSchema = "public", RefTable = "customers", RefColumns = ["id"],
                OnDelete = "CASCADE",
            },
        ],
        Indexes =
        [
            new IndexInfo { Name = "orders_pkey", IsPrimary = true, IsUnique = true, Definition = "CREATE UNIQUE INDEX orders_pkey ON sales.\"Orders\" USING btree (id)" },
            new IndexInfo { Name = "orders_total_idx", Definition = "CREATE INDEX orders_total_idx ON sales.\"Orders\" USING btree (total)" },
        ],
    };

    [Fact]
    public void Create_table_from_metadata()
    {
        var sql = ScriptGenerator.CreateTable(Orders());
        Assert.Equal(
            """
            CREATE TABLE sales."Orders" (
                id bigint GENERATED ALWAYS AS IDENTITY NOT NULL,
                total numeric(12,2) DEFAULT 0 NOT NULL,
                customer_id integer,
                "order" numeric GENERATED ALWAYS AS ((total * (2)::numeric)) STORED,
                CONSTRAINT orders_pkey PRIMARY KEY (id),
                CONSTRAINT orders_customer_fk FOREIGN KEY (customer_id) REFERENCES public.customers (id) ON DELETE CASCADE
            );
            CREATE INDEX orders_total_idx ON sales."Orders" USING btree (total);
            COMMENT ON TABLE sales."Orders" IS 'It''s orders';
            COMMENT ON COLUMN sales."Orders".customer_id IS 'who';
            """.ReplaceLineEndings("\n"), sql);
    }

    [Fact]
    public void Serial_default_creates_its_sequence_first()
    {
        var t = new TableInfo
        {
            Schema = "public", Name = "t",
            Columns = [new ColumnInfo { Name = "id", Ordinal = 1, DataType = "integer", IsNullable = false, Default = "nextval('\"T_id_seq\"'::regclass)" }],
        };
        var sql = ScriptGenerator.CreateTable(t);
        Assert.StartsWith("CREATE SEQUENCE IF NOT EXISTS \"T_id_seq\";\nCREATE TABLE public.t (\n    id integer DEFAULT nextval(", sql);
        Assert.Null(ScriptGenerator.SequenceOfDefault("now()"));
    }

    [Fact]
    public void Foreign_key_prefers_server_definition()
    {
        var fk = new ForeignKeyInfo { Name = "fk", Definition = "FOREIGN KEY (a) REFERENCES t(b)" };
        Assert.Equal("CONSTRAINT fk FOREIGN KEY (a) REFERENCES t(b)", ScriptGenerator.ForeignKeyDefinition(fk));
    }

    [Fact]
    public void Select_limit()
    {
        Assert.Equal("SELECT * FROM public.customers LIMIT 100;", ScriptGenerator.Select("public", "customers"));
        Assert.Equal("SELECT * FROM \"My Schema\".\"order\" LIMIT 5;", ScriptGenerator.Select("My Schema", "order", 5));
    }

    [Fact]
    public void Truncate_options_and_skips_views()
    {
        var a = new ScriptObject(ScriptObjectKind.Table, "public", "a");
        var b = new ScriptObject(ScriptObjectKind.Table, "s", "B");
        var v = new ScriptObject(ScriptObjectKind.View, "public", "v");
        Assert.Equal("TRUNCATE TABLE public.a;", ScriptGenerator.Truncate(a));
        Assert.Equal("TRUNCATE TABLE public.a, s.\"B\" RESTART IDENTITY CASCADE;", ScriptGenerator.Truncate([a, b, v], cascade: true, restartIdentity: true));
        Assert.Equal("", ScriptGenerator.Truncate([v]));
    }

    [Fact]
    public void Drop_groups_by_kind()
    {
        var stmts = ScriptGenerator.Drop(
        [
            new ScriptObject(ScriptObjectKind.Table, "public", "a"),
            new ScriptObject(ScriptObjectKind.View, "public", "v"),
            new ScriptObject(ScriptObjectKind.Table, "public", "b"),
            new ScriptObject(ScriptObjectKind.Function, "public", "f", "x integer"),
            new ScriptObject(ScriptObjectKind.MaterializedView, "sales", "mv"),
        ], cascade: true);
        Assert.Equal(
        [
            "DROP TABLE public.a, public.b CASCADE;",
            "DROP VIEW public.v CASCADE;",
            "DROP FUNCTION public.f(x integer) CASCADE;",
            "DROP MATERIALIZED VIEW sales.mv CASCADE;",
        ], stmts);
        Assert.Equal("DROP SEQUENCE public.s;", ScriptGenerator.Drop(new ScriptObject(ScriptObjectKind.Sequence, "public", "s")));
        Assert.Equal("DROP TYPE public.mood;", ScriptGenerator.Drop(new ScriptObject(ScriptObjectKind.Type, "public", "mood")));
    }

    [Fact]
    public void Sequence_and_types()
    {
        Assert.Equal("CREATE SEQUENCE public.s AS bigint OWNED BY public.t.id;",
            ScriptGenerator.CreateSequence(new SequenceInfo { Schema = "public", Name = "s", DataType = "bigint", OwnedBy = "t.id" }));
        Assert.Equal("CREATE TYPE public.mood AS ENUM ('sad', 'it''s ok');",
            ScriptGenerator.CreateType(new UserTypeInfo { Schema = "public", Name = "mood", Kind = UserTypeKind.Enum, EnumLabels = ["sad", "it's ok"] }));
        Assert.Equal("CREATE DOMAIN public.pos AS integer;",
            ScriptGenerator.CreateType(new UserTypeInfo { Schema = "public", Name = "pos", Kind = UserTypeKind.Domain, BaseType = "integer" }));
        Assert.Equal("CREATE TYPE public.pt AS (\n    x integer,\n    y integer\n);",
            ScriptGenerator.CreateType(new UserTypeInfo
            {
                Schema = "public", Name = "pt", Kind = UserTypeKind.Composite,
                Attributes = [new ColumnInfo { Name = "y", Ordinal = 2, DataType = "integer" }, new ColumnInfo { Name = "x", Ordinal = 1, DataType = "integer" }],
            }));
    }

    [Fact]
    public void Definition_query_only_for_server_side_kinds()
    {
        Assert.Null(ScriptGenerator.DefinitionQuery(new ScriptObject(ScriptObjectKind.Table, "public", "t")));
        Assert.Contains("pg_get_viewdef('\"public\".\"v\"'::regclass, true)", ScriptGenerator.DefinitionQuery(new ScriptObject(ScriptObjectKind.View, "public", "v")));
        Assert.Equal("SELECT pg_get_functiondef(42::oid)", ScriptGenerator.DefinitionQuery(new ScriptObject(ScriptObjectKind.Function, "public", "f", "x integer"), 42));
        Assert.Null(ScriptGenerator.DefinitionQuery(new ScriptObject(ScriptObjectKind.Aggregate, "public", "agg", "integer")));
        Assert.Equal("x;", ScriptGenerator.FinishDefinition("x\n"));
        Assert.Equal("x;", ScriptGenerator.FinishDefinition("x;"));
    }

    // ---- integration: scripts round-trip on a real server --------------------------------------------------------

    private static async Task ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync();
    }

    [Fact]
    public async Task Scripts_round_trip_on_server()
    {
        if (!TestPg.Available(output)) return;
        var schema = "sg_test_" + Guid.NewGuid().ToString("N")[..10];
        var (profile, secrets) = TestPg.Profile();
        await using var session = new DbSession(profile, secrets);
        await session.ConnectAsync();
        await using var conn = await session.OpenConnectionAsync();
        try
        {
            await ExecAsync(conn, $"""
                create schema {schema};
                create table {schema}.parent(id int generated by default as identity primary key, code text unique);
                create table {schema}."Child"(id bigserial primary key, parent_id int not null references {schema}.parent(id) on delete cascade,
                    qty int default 1, dbl int generated always as (qty * 2) stored);
                create index child_qty_idx on {schema}."Child"(qty) where qty > 0;
                comment on table {schema}."Child" is 'kids';
                create view {schema}.v as select * from {schema}.parent;
                create materialized view {schema}.mv as select count(*) n from {schema}.parent;
                create function {schema}.f(x int) returns int language sql as $$ select x $$;
                create type {schema}.mood as enum ('a','b');
                insert into {schema}.parent(code) values ('x'),('y');
                insert into {schema}."Child"(parent_id) values (1),(2);
                """);

            var md = DbMetadata.Empty("x").WithSchema((await MetadataLoader.LoadSchemasAsync(conn, [schema], default)).Single());
            var child = md.FindTable(schema, "Child")!;
            var createChild = ScriptGenerator.CreateTable(child);
            var createParent = ScriptGenerator.CreateTable(md.FindTable(schema, "parent")!);
            var createType = ScriptGenerator.CreateFromMetadata(md, ScriptObject.For(md.FindSchema(schema)!.Types.Single()))!;
            output.WriteLine(createParent + "\n" + createChild);

            // Server-side definitions.
            var viewDef = (string)(await ScalarAsync(conn, ScriptGenerator.DefinitionQuery(new ScriptObject(ScriptObjectKind.View, schema, "v"))!))!;
            Assert.StartsWith($"CREATE OR REPLACE VIEW {schema}.v AS\n", viewDef);
            var mvDef = (string)(await ScalarAsync(conn, ScriptGenerator.DefinitionQuery(new ScriptObject(ScriptObjectKind.MaterializedView, schema, "mv"))!))!;
            Assert.StartsWith($"CREATE MATERIALIZED VIEW {schema}.mv AS\n", mvDef);
            var f = md.FindFunctions(schema, "f").Single();
            var fnDef = (string)(await ScalarAsync(conn, ScriptGenerator.DefinitionQuery(ScriptObject.For(f), f.Oid)!))!;
            Assert.Contains("CREATE OR REPLACE FUNCTION", fnDef);
            var fnDef2 = (string)(await ScalarAsync(conn, ScriptGenerator.DefinitionQuery(ScriptObject.For(f))!))!;
            Assert.Equal(fnDef, fnDef2);

            // TRUNCATE … RESTART IDENTITY CASCADE empties both tables.
            await ExecAsync(conn, ScriptGenerator.Truncate(ScriptObject.For(md.FindTable(schema, "parent")!), cascade: true, restartIdentity: true));
            Assert.Equal(0L, await ScalarAsync(conn, $"select count(*) from {schema}.\"Child\""));

            // DROP everything (one statement per kind), then recreate tables + type from the generated DDL.
            var drops = ScriptGenerator.Drop(
            [
                ScriptObject.For(md.FindTable(schema, "v")!), ScriptObject.For(md.FindTable(schema, "mv")!), ScriptObject.For(f),
                ScriptObject.For(child), ScriptObject.For(md.FindTable(schema, "parent")!), ScriptObject.For(md.FindSchema(schema)!.Types.Single()),
            ], cascade: true);
            foreach (var d in drops) await ExecAsync(conn, d);
            Assert.Equal(0L, await ScalarAsync(conn, $"select count(*) from pg_class c join pg_namespace n on n.oid = c.relnamespace where n.nspname = '{schema}' and c.relkind in ('r','v','m')"));

            await ExecAsync(conn, createType);
            await ExecAsync(conn, createParent);
            await ExecAsync(conn, createChild);
            await ExecAsync(conn, FixupForReplay(viewDef));
            var md2 = DbMetadata.Empty("x").WithSchema((await MetadataLoader.LoadSchemasAsync(conn, [schema], default)).Single());
            var child2 = md2.FindTable(schema, "Child")!;
            Assert.Equal(child.Columns.Select(c => (c.Name, c.DataType, c.IsNullable, c.IsGenerated)), child2.Columns.Select(c => (c.Name, c.DataType, c.IsNullable, c.IsGenerated)));
            Assert.Equal(child.PrimaryKey, child2.PrimaryKey);
            Assert.Equal("CASCADE", child2.ForeignKeys.Single().OnDelete);
            Assert.Contains(child2.Indexes, i => i.Name == "child_qty_idx" && i.Predicate is not null);
            Assert.Equal("kids", child2.Comment);
            Assert.Equal("d", md2.FindTable(schema, "parent")!.FindColumn("id")!.Identity);
            Assert.Contains(md2.FindTable(schema, "parent")!.Indexes, i => i.IsUnique && i.Columns.SequenceEqual(["code"]));
            Assert.NotNull(md2.FindTable(schema, "v"));
        }
        finally
        {
            await ExecAsync(conn, $"drop schema if exists {schema} cascade");
        }
    }

    private static string FixupForReplay(string def) => ScriptGenerator.FinishDefinition(def);
}
