using Npgsql;
using TablePlsu.Core.Data;
using TablePlsu.Core.Db;
using TablePlsu.Core.Metadata;
using TablePlsu.Tests.Infrastructure;
using Xunit.Abstractions;

namespace TablePlsu.Tests.Data;

/// <summary>Spec §10 "Edit results inline": which editor results can be edited, decided by column provenance.</summary>
public class ResultEditabilityTests(ITestOutputHelper output)
{
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TempDir _tmp = new();

        public required DbSession Session { get; init; }
        public required MetadataCache Cache { get; init; }

        public static async Task<Fixture> OpenAsync()
        {
            var (profile, secrets) = TestPg.Profile();
            var session = new DbSession(profile, secrets);
            await session.ConnectAsync();
            var tmp = new TempDir();
            // Lazy: nothing loaded up front, so the analyzer has to load the source table's schema itself.
            var cache = new MetadataCache(profile.Id, session.Database, session, tmp.Paths, new MetadataCacheOptions { LazySchemas = true });
            return new Fixture { Session = session, Cache = cache };
        }

        public async Task<(StatementResult Result, ResultEditInfo Info)> AnalyzeAsync(string sql)
        {
            var result = await Session.ExecuteAsync(sql, new ExecuteOptions { RowLimit = 50 });
            var st = Assert.Single(result.Statements);
            Assert.Null(st.Error);
            // The app's whole pipeline, including re-reading the rows by key.
            var info = await ResultEditability.AnalyzeResultAsync(st.CommandTag, st.Columns, st.Rows, Cache, Session);
            return (st, info);
        }

        public async ValueTask DisposeAsync()
        {
            Cache.Dispose();
            await Session.DisposeAsync();
            _tmp.Dispose();
        }
    }

    [Fact]
    public async Task Select_star_and_pk_subsets_of_one_table_are_editable()
    {
        if (!TestPg.Available(output)) return;
        await using var f = await Fixture.OpenAsync();

        var (st, info) = await f.AnalyzeAsync("select * from customers order by id");
        Assert.True(info.IsEditable, info.Reason);
        Assert.Equal("public", info.Target!.Schema);
        Assert.Equal("customers", info.Target.Table);
        Assert.Equal(["id", "name", "email", "created_at", "meta", "active"], info.Target.Columns);
        Assert.Equal([0], info.Target.KeyColumns);
        Assert.True(st.Truncated); // a row-limited result is still editable

        (_, info) = await f.AnalyzeAsync("select id, name from customers");
        Assert.True(info.IsEditable, info.Reason);
        Assert.Equal(["id", "name"], info.Target!.Columns);

        // Aliases: the change SQL uses the base column names.
        (_, info) = await f.AnalyzeAsync("select name as who, id as key from customers");
        Assert.True(info.IsEditable, info.Reason);
        Assert.Equal(["name", "id"], info.Target!.Columns);
        Assert.Equal([1], info.Target.KeyColumns);

        (_, info) = await f.AnalyzeAsync("select * from sales.orders o where o.status = 'paid'");
        Assert.True(info.IsEditable, info.Reason);
        Assert.Equal("sales", info.Target!.Schema);
        Assert.Equal("orders", info.Target.Table);

        (_, info) = await f.AnalyzeAsync("TABLE sales.orders");
        Assert.True(info.IsEditable, info.Reason);
    }

    [Fact]
    public async Task A_cte_over_one_table_is_decided_by_provenance()
    {
        if (!TestPg.Available(output)) return;
        await using var f = await Fixture.OpenAsync();
        var (_, info) = await f.AnalyzeAsync("with x as (select * from customers) select * from x");
        Assert.True(info.IsEditable, info.Reason);
        Assert.Equal("customers", info.Target!.Table);

        (_, info) = await f.AnalyzeAsync("with x as (select * from customers) select name from x");
        Assert.False(info.IsEditable);
        Assert.Equal("primary key column id is not in the result", info.Reason);
    }

    [Theory]
    [InlineData("select name from customers", "primary key column id is not in the result")]
    [InlineData("select o.id, o.total, c.name from sales.orders o join customers c on c.id = o.customer_id", "result combines columns from several tables")]
    [InlineData("select count(*) from customers", "column count is computed, not read from a table")]
    [InlineData("select id, name || ' ' from customers", "column ?column? is computed, not read from a table")]
    [InlineData("select * from active_customers", "result comes from a view")]
    [InlineData("select * from sales.order_totals", "result comes from a materialized view")]
    [InlineData("select * from no_pk", "table public.no_pk has no primary key")]
    [InlineData("select id, name, name from customers", "column name appears more than once")]
    [InlineData("select id from customers union all select id from customers", "column id is computed, not read from a table")]
    [InlineData("select relname, oid from pg_class", "result comes from a system catalog")]
    [InlineData("values (1, 'a')", "only SELECT results can be edited")]
    public async Task Other_results_are_read_only_with_a_specific_reason(string sql, string reason)
    {
        if (!TestPg.Available(output)) return;
        await using var f = await Fixture.OpenAsync();
        var (_, info) = await f.AnalyzeAsync(sql);
        Assert.False(info.IsEditable);
        Assert.Equal(reason, info.Reason);
        Assert.Null(info.Target);
    }

    [Fact]
    public async Task Inexact_key_types_and_repeated_keys_are_read_only()
    {
        if (!TestPg.Available(output)) return;
        var table = "re_" + Guid.NewGuid().ToString("N")[..10];
        await using var admin = new NpgsqlConnection(TestPg.ConnectionString);
        await admin.OpenAsync();
        await using (var cmd = new NpgsqlCommand(
            $"CREATE TABLE public.{table}_n(k numeric PRIMARY KEY, v text); INSERT INTO public.{table}_n VALUES (1.5, 'a');" +
            $"CREATE TABLE public.{table}_v(k varchar(20), c char(3), v text, PRIMARY KEY (k, c)); INSERT INTO public.{table}_v VALUES ('a', 'x', '1');", admin))
            await cmd.ExecuteNonQueryAsync();
        await using var f = await Fixture.OpenAsync();
        try
        {
            var (_, info) = await f.AnalyzeAsync($"select * from public.{table}_n");
            Assert.False(info.IsEditable);
            Assert.Equal("primary key type numeric can't be matched exactly", info.Reason);

            (_, info) = await f.AnalyzeAsync($"select * from public.{table}_v");
            Assert.True(info.IsEditable, info.Reason);
            Assert.Equal([0, 1], info.Target!.KeyColumns);

            // One table's columns through a join that repeats rows: the same key twice can't be edited row by row.
            (_, info) = await f.AnalyzeAsync($"select t.* from public.{table}_v t cross join generate_series(1, 2)");
            Assert.False(info.IsEditable);
            Assert.Equal("rows repeat the same primary key (a join?)", info.Reason);
        }
        finally
        {
            await using var cmd = new NpgsqlCommand($"DROP TABLE IF EXISTS public.{table}_n; DROP TABLE IF EXISTS public.{table}_v", admin);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public void Analysis_without_a_database_follows_the_rules()
    {
        static ResultColumn Col(int i, string name, uint oid, short att, string type = "integer") =>
            new(i, name, type, 23) { TableOid = oid, AttributeNumber = att };
        var table = new TableInfo
        {
            Schema = "public", Name = "t", Oid = 42, Kind = RelationKind.Table, PrimaryKey = ["id"],
            Columns = [new ColumnInfo { Name = "id", Ordinal = 1, DataType = "integer" }, new ColumnInfo { Name = "v", Ordinal = 3, DataType = "text" }],
        };
        var ok = ResultEditability.Analyze("SELECT", [Col(0, "v", 42, 3, "text"), Col(1, "id", 42, 1)], table);
        Assert.True(ok.IsEditable, ok.Reason);
        Assert.Equal(["v", "id"], ok.Target!.Columns);
        Assert.Equal([1], ok.Target.KeyColumns);
        Assert.Same(table.Columns[1], ok.ColumnInfos[0]);

        Assert.Equal("table metadata for public.t is out of date (reload with Ctrl+R)",
            ResultEditability.Analyze("SELECT", [Col(0, "id", 42, 1), Col(1, "gone", 42, 9)], table).Reason);
        Assert.Equal("only SELECT results can be edited", ResultEditability.Analyze("INSERT", [Col(0, "id", 42, 1)], table).Reason);
        Assert.Equal("table metadata isn't available", ResultEditability.Analyze("SELECT", [Col(0, "id", 42, 1)], null).Reason);
        var partitioned = new TableInfo { Schema = "public", Name = "t", Oid = 42, Kind = RelationKind.PartitionedTable, PrimaryKey = ["id"], Columns = table.Columns };
        Assert.True(ResultEditability.Analyze("WITH", [Col(0, "id", 42, 1)], partitioned).IsEditable);
        var foreign = new TableInfo { Schema = "public", Name = "t", Oid = 42, Kind = RelationKind.ForeignTable, Columns = table.Columns };
        Assert.Equal("result comes from a foreign table", ResultEditability.Analyze("SELECT", [Col(0, "id", 42, 1)], foreign).Reason);

        Assert.Null(ResultEditability.DuplicateKeyReason([[1, "a"], [2, "b"]], [0]));
        Assert.NotNull(ResultEditability.DuplicateKeyReason([[1, "a"], [1, "b"]], [0]));
    }
    /// <summary>The row re-read (wrong-row review) sends every accepted key type back as text: none may break matching.</summary>
    [Fact]
    public async Task Row_verification_matches_every_exact_key_type()
    {
        if (!TestPg.Available(output)) return;
        var table = "re_keys_" + Guid.NewGuid().ToString("N")[..8];
        await using var admin = new NpgsqlConnection(TestPg.ConnectionString);
        await admin.OpenAsync();
        await using (var c = new NpgsqlCommand($$"""
            CREATE TABLE public.{{table}}(a smallint, b bigint, c uuid, d date, e boolean, f char(3), g varchar(5), h name, i text, v int[],
                PRIMARY KEY (a, b, c, d, e, f, g, h, i));
            INSERT INTO public.{{table}} VALUES
                (1, 9000000000, 'a0eebc99-9c0b-4ef8-bb6d-6bb9bd380a11', '2024-02-29', true, 'x', 'it''s', 'nm', 'tab	x', '{1,2}'),
                (-2, -1, '00000000-0000-0000-0000-000000000000', '0001-01-01', false, 'abc', '', 'n2', 'ü€', NULL)
            """, admin)) await c.ExecuteNonQueryAsync();
        await using var f = await Fixture.OpenAsync();
        try
        {
            var (st, info) = await f.AnalyzeAsync($"select * from public.{table}");
            Assert.Equal(2, st.Rows.Count);
            Assert.True(info.IsEditable, info.Reason);
        }
        finally
        {
            await using var d = new NpgsqlCommand($"DROP TABLE IF EXISTS public.{table}", admin);
            await d.ExecuteNonQueryAsync();
        }
    }

    /// <summary>A row changed (or deleted) by someone else between the query and the check: what the grid shows is not
    /// what a commit keyed by that row would change, so the result is read-only until re-run.</summary>
    [Fact]
    public async Task Rows_changed_since_the_query_make_the_result_read_only()
    {
        if (!TestPg.Available(output)) return;
        var table = "re_chg_" + Guid.NewGuid().ToString("N")[..8];
        await using var admin = new NpgsqlConnection(TestPg.ConnectionString);
        await admin.OpenAsync();
        async Task Exec(string sql)
        {
            await using var c = new NpgsqlCommand(sql, admin);
            await c.ExecuteNonQueryAsync();
        }
        await Exec($"CREATE TABLE public.{table}(id int PRIMARY KEY, name text); INSERT INTO public.{table} VALUES (1, 'a'), (2, 'b')");
        await using var f = await Fixture.OpenAsync();
        try
        {
            var result = await f.Session.ExecuteAsync($"select * from public.{table} order by id", new ExecuteOptions { RowLimit = 50 });
            var st = Assert.Single(result.Statements);
            var info = await ResultEditability.AnalyzeAsync(st.CommandTag, st.Columns, (oid, ct) => ResultEditability.FindTableByOidAsync(f.Cache, f.Session, oid, ct));
            Assert.True(info.IsEditable, info.Reason);
            Assert.True((await ResultEditability.VerifyRowsAsync(info, st.Rows, f.Session)).IsEditable);

            await Exec($"UPDATE public.{table} SET name = 'changed' WHERE id = 2");
            var changed = await ResultEditability.VerifyRowsAsync(info, st.Rows, f.Session);
            Assert.False(changed.IsEditable);
            Assert.Contains("don't match", changed.Reason);

            await Exec($"UPDATE public.{table} SET name = 'b' WHERE id = 2; DELETE FROM public.{table} WHERE id = 1");
            Assert.False((await ResultEditability.VerifyRowsAsync(info, st.Rows, f.Session)).IsEditable);
        }
        finally
        {
            await Exec($"DROP TABLE IF EXISTS public.{table}");
        }
    }

    /// <summary>A renamed non-key column (wrong-row review, finding 2): the stale snapshot is re-loaded, so the change
    /// target uses the new name.</summary>
    [Fact]
    public async Task Renamed_column_reloads_the_stale_metadata()
    {
        if (!TestPg.Available(output)) return;
        var table = "re_ren_" + Guid.NewGuid().ToString("N")[..8];
        await using var admin = new NpgsqlConnection(TestPg.ConnectionString);
        await admin.OpenAsync();
        async Task Exec(string sql)
        {
            await using var c = new NpgsqlCommand(sql, admin);
            await c.ExecuteNonQueryAsync();
        }
        await Exec($"CREATE TABLE public.{table}(id int PRIMARY KEY, name text); INSERT INTO public.{table} VALUES (1, 'a')");
        await using var f = await Fixture.OpenAsync();
        try
        {
            var (_, info) = await f.AnalyzeAsync($"select * from public.{table}");
            Assert.Equal(["id", "name"], info.Target!.Columns);
            await Exec($"ALTER TABLE public.{table} RENAME name TO title");
            (_, info) = await f.AnalyzeAsync($"select * from public.{table}");
            Assert.True(info.IsEditable, info.Reason);
            Assert.Equal(["id", "title"], info.Target!.Columns);
        }
        finally
        {
            await Exec($"DROP TABLE IF EXISTS public.{table}");
        }
    }
}
