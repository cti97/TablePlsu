using Npgsql;
using TablePlsu.Core.Db;
using TablePlsu.Core.Ddl;
using TablePlsu.Core.Metadata;
using TablePlsu.Tests.Infrastructure;
using Xunit.Abstractions;

namespace TablePlsu.Tests.Ddl;

public class DdlIntegrationTests(ITestOutputHelper output)
{
    private static async Task<DbSession> OpenAsync()
    {
        var (profile, secrets) = TestPg.Profile();
        var session = new DbSession(profile, secrets);
        await session.ConnectAsync();
        return session;
    }

    private static Task<DbMetadata> LoadAsync(DbSession s, params string[] schemas) =>
        s.WithMetadataConnectionAsync((c, ct) => MetadataLoader.LoadAsync(c, new MetadataLoadOptions { Schemas = schemas }, ct));

    [Fact]
    public async Task Customers_ddl_from_seed()
    {
        if (!TestPg.Available(output)) return;
        await using var s = await OpenAsync();
        var md = await LoadAsync(s, "public", "sales");
        var customers = md.FindTable("public", "customers")!;
        var extras = await s.WithMetadataConnectionAsync((c, ct) => DdlLoader.LoadTableExtrasAsync(c, "public", "customers", ct));
        Assert.Contains(extras.Constraints, c => c is { Name: "customers_pkey", Type: 'p', Definition: "PRIMARY KEY (id)" });
        Assert.Contains(extras.Constraints, c => c is { Name: "customers_email_key", Type: 'u', Definition: "UNIQUE (email)" });

        var ddl = DdlGenerator.CreateTable(customers, extras);
        output.WriteLine(ddl);
        Assert.StartsWith("CREATE TABLE public.customers (\n    id integer DEFAULT nextval('customers_id_seq'::regclass) NOT NULL,\n    name text NOT NULL,", ddl);
        Assert.Contains("    created_at timestamp with time zone DEFAULT now(),\n", ddl);
        Assert.Contains("    active boolean DEFAULT true,\n", ddl);
        Assert.Contains("    CONSTRAINT customers_pkey PRIMARY KEY (id),\n    CONSTRAINT customers_email_key UNIQUE (email)\n);", ddl);
        Assert.DoesNotContain("CREATE UNIQUE INDEX", ddl);

        var orders = md.FindTable("sales", "orders")!;
        var oExtras = await s.WithMetadataConnectionAsync((c, ct) => DdlLoader.LoadTableExtrasAsync(c, "sales", "orders", ct));
        var oDdl = DdlGenerator.CreateTable(orders, oExtras);
        output.WriteLine(oDdl);
        Assert.Contains("    total numeric(12,2),\n", oDdl);
        Assert.Contains("CONSTRAINT orders_customer_id_fkey FOREIGN KEY (customer_id) REFERENCES customers(id)", oDdl);
        // Metadata-only fallback names the same FK.
        Assert.Contains("CONSTRAINT orders_customer_id_fkey FOREIGN KEY (customer_id) REFERENCES customers(id)", DdlGenerator.CreateTable(orders));
    }

    [Fact]
    public async Task View_matview_and_function_definitions()
    {
        if (!TestPg.Available(output)) return;
        await using var s = await OpenAsync();
        var v = await s.WithMetadataConnectionAsync((c, ct) => DdlLoader.LoadViewDefinitionAsync(c, "public", "active_customers", ct));
        Assert.NotNull(v);
        Assert.False(v.IsMaterialized);
        Assert.Contains("FROM customers", v.Definition);
        Assert.StartsWith("CREATE OR REPLACE VIEW public.active_customers AS\nSELECT", DdlGenerator.ViewDdl(v.Schema, v.Name, v.Definition, false));

        var m = await s.WithMetadataConnectionAsync((c, ct) => DdlLoader.LoadViewDefinitionAsync(c, "sales", "order_totals", ct));
        Assert.True(m!.IsMaterialized);
        Assert.Null(await s.WithMetadataConnectionAsync((c, ct) => DdlLoader.LoadViewDefinitionAsync(c, "public", "customers", ct)));

        var oid = await s.WithMetadataConnectionAsync((c, ct) => DdlLoader.FindFunctionOidAsync(c, "public", "add_one", null, ct));
        Assert.NotNull(oid);
        Assert.Equal(oid, await s.WithMetadataConnectionAsync((c, ct) => DdlLoader.FindFunctionOidAsync(c, "public", "add_one", "x integer", ct)));
        var def = await s.WithMetadataConnectionAsync((c, ct) => DdlLoader.LoadFunctionDefinitionAsync(c, oid.Value, ct));
        Assert.StartsWith("CREATE OR REPLACE FUNCTION public.add_one(x integer)", def);
    }

    [Fact]
    public async Task Create_table_round_trip_and_function_replace()
    {
        if (!TestPg.Available(output)) return;
        var schema = "ddl_test_" + Guid.NewGuid().ToString("N")[..10];
        await using var s = await OpenAsync();
        await using var conn = await s.OpenConnectionAsync();
        await new NpgsqlCommand($"create schema {schema}", conn).ExecuteNonQueryAsync();
        try
        {
            var model = new NewTableModel
            {
                Schema = schema,
                Name = "Widgets",
                Columns =
                [
                    new NewColumn { Name = "id", DataType = "bigint", IsNullable = false, Default = DefaultChoices.NextVal, IsPrimaryKey = true },
                    new NewColumn { Name = "label", DataType = "varchar(40)", IsNullable = false, Default = DefaultChoices.Empty, Comment = "shown" },
                    new NewColumn { Name = "at", DataType = "timestamptz", Default = DefaultChoices.CurrentTimestamp },
                ],
            };
            Assert.Empty(model.Validate());
            await DdlExecutor.ExecuteAsync(conn, DdlGenerator.CreateTable(model));

            var md = await LoadAsync(s, schema);
            var t = md.FindTable(schema, "Widgets");
            Assert.NotNull(t);
            Assert.Equal(["id"], t.PrimaryKey);
            Assert.Equal("character varying(40)", t.FindColumn("label")!.DataType);
            Assert.Equal("shown", t.FindColumn("label")!.Comment);
            Assert.StartsWith("nextval(", t.FindColumn("id")!.Default);
            Assert.Contains(md.FindSchema(schema)!.Sequences, q => q.Name == "Widgets_id_seq" && q.OwnedBy == "Widgets.id");
            await new NpgsqlCommand($"insert into {schema}.\"Widgets\" default values", conn).ExecuteNonQueryAsync();

            // Regenerated DDL recreates an equivalent table under another name.
            var ddl = DdlGenerator.CreateTable(t).Replace("\"Widgets\" (", "widgets2 (").Replace("CONSTRAINT \"Widgets_pkey\" ", "");
            await new NpgsqlCommand(ddl.Split("\n\n")[0], conn).ExecuteNonQueryAsync();

            // A failing batch rolls back entirely.
            var ex = await Assert.ThrowsAsync<DdlExecutionException>(() => DdlExecutor.ExecuteAsync(conn,
                [$"create table {schema}.ok_one(a int)", $"create table {schema}.bad(a no_such_type)"]));
            Assert.Equal(1, ex.StatementIndex);
            var exists = await new NpgsqlCommand($"select to_regclass('{schema}.ok_one') is not null", conn).ExecuteScalarAsync();
            Assert.Equal(false, exists);

            // CREATE OR REPLACE FUNCTION from an edited pg_get_functiondef.
            await new NpgsqlCommand($"create function {schema}.f(x int) returns int language sql as $$ select x $$", conn).ExecuteNonQueryAsync();
            var oid = (await DdlLoader.FindFunctionOidAsync(conn, schema, "f", null))!.Value;
            var def = await DdlLoader.LoadFunctionDefinitionAsync(conn, oid);
            await DdlExecutor.ExecuteAsync(conn, [DdlGenerator.FunctionDdl(def!.Replace("select x", "select x * 10"))]);
            Assert.Equal(30, await new NpgsqlCommand($"select {schema}.f(3)", conn).ExecuteScalarAsync());
        }
        finally
        {
            await new NpgsqlCommand($"drop schema {schema} cascade", conn).ExecuteNonQueryAsync();
        }
    }
}
