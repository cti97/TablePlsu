using Npgsql;
using TablePlsu.Core.Data;
using TablePlsu.Core.Sql;
using TablePlsu.Tests.Infrastructure;
using Xunit.Abstractions;

namespace TablePlsu.Tests.Data;

/// <summary>
/// Data-safety integration tests for grid editing: every UPDATE/DELETE generated from a loaded page must hit exactly the
/// row it came from, whatever the key type, and anything else must roll back. Each test uses its own scratch table.
/// The session runs in a non-UTC time zone on purpose (time-zone round-trip bugs hide under UTC).
/// </summary>
public class DataEditSafetyTests(ITestOutputHelper output)
{
    private static async Task<NpgsqlConnection> OpenAsync(string timezone = "Asia/Kolkata")
    {
        var b = new NpgsqlConnectionStringBuilder(TestPg.ConnectionString) { Timezone = timezone };
        var conn = new NpgsqlConnection(b.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private static async Task Exec(NpgsqlConnection conn, string sql)
    {
        await using var c = new NpgsqlCommand(sql, conn);
        await c.ExecuteNonQueryAsync();
    }

    private static async Task<object?> Scalar(NpgsqlConnection conn, string sql)
    {
        await using var c = new NpgsqlCommand(sql, conn);
        return await c.ExecuteScalarAsync();
    }

    private static string Scratch() => "de_" + Guid.NewGuid().ToString("N")[..12];

    private static async Task<TablePage> LoadAsync(NpgsqlConnection conn, string schema, string table, string orderBy)
    {
        var q = new TableQuery { Schema = schema, Table = table, Sorts = [new SortSpec(orderBy, SortDirection.Asc)], Limit = 300, KeyColumns = ["k"] };
        return await TableDataAccess.LoadPageAsync(conn, TableQueryBuilder.BuildSelect(q));
    }

    /// <summary>
    /// Loads rows keyed by <c>k</c> (of <paramref name="type"/>), edits <c>v</c> of the row whose <c>tag</c> is 'target',
    /// commits, and checks that exactly that row changed. The other row holds a near-twin key value.
    /// </summary>
    [Theory]
    [InlineData("numeric", "0.0000000000000000000000000000001", "0")]
    [InlineData("numeric", "12345678901234567890123456789.5", "1")]
    [InlineData("numeric", "1.10", "1.1000001")]
    [InlineData("timestamptz", "'2024-01-01 10:00:00.123456+00'", "'2024-01-01 10:00:00.123457+00'")]
    [InlineData("timestamp", "'2024-01-01 10:00:00.123456'", "'2024-01-01 10:00:00'")]
    [InlineData("date", "'2024-02-29'", "'2024-03-01'")]
    [InlineData("time", "'10:00:00.5'", "'10:00:00'")]
    [InlineData("timetz", "'10:00:00+02'", "'10:00:00+03'")]
    [InlineData("interval", "'1 mon 2 days 03:00:00'", "'1 mon 3 days 03:00:00'")]
    [InlineData("interval", "'1 day 00:00:01.5'", "'1 day'")]
    [InlineData("uuid", "'a0eebc99-9c0b-4ef8-bb6d-6bb9bd380a11'", "'a0eebc99-9c0b-4ef8-bb6d-6bb9bd380a12'")]
    [InlineData("float8", "0.1", "0.1000000000000001")]
    [InlineData("float8", "1e300", "1e299")]
    [InlineData("real", "0.1", "0.1000001")]
    [InlineData("bigint", "9223372036854775807", "-9223372036854775808")]
    [InlineData("boolean", "true", "false")]
    [InlineData("char(5)", "'ab'", "'abc'")]
    [InlineData("varchar(10)", "'x'", "'X'")]
    [InlineData("text", "E'a''b\\\\c\\n\"d'", "'a''b'")]
    [InlineData("text", "'NULL'", "''")]
    [InlineData("jsonb", "'{\"a\": [1, 2.50]}'", "'{\"a\": [1, 2.5001]}'")]
    [InlineData("inet", "'10.0.0.1/8'", "'10.0.0.1'")]
    [InlineData("cidr", "'10.0.0.0/8'", "'10.0.0.0/9'")]
    [InlineData("macaddr", "'08:00:2b:01:02:03'", "'08:00:2b:01:02:04'")]
    [InlineData("int[]", "'{1,2,NULL}'", "'{1,2}'")]
    [InlineData("text[]", "'{\"a b\",\"NULL\",NULL,\"x,y\"}'", "'{\"a b\"}'")]
    [InlineData("timestamptz[]", "'{\"2024-01-01 10:00:00.5+00\"}'", "'{\"2024-01-01 10:00:00+00\"}'")]
    [InlineData("date[]", "'{2024-01-01}'", "'{2024-01-02}'")]
    [InlineData("bytea", "'\\x00ff'", "'\\x00fe'")]
    [InlineData("bytea", "decode(repeat('ab', 300), 'hex')", "decode(repeat('ab', 299), 'hex')")]
    public async Task Update_by_key_hits_exactly_the_loaded_row(string type, string target, string twin)
    {
        if (!TestPg.Available(output)) return;
        var table = Scratch();
        await using var conn = await OpenAsync();
        await Exec(conn, $"CREATE TABLE public.{table}(k {type} PRIMARY KEY, v int NOT NULL DEFAULT 0, tag text NOT NULL); " +
                         $"INSERT INTO public.{table}(k, tag) VALUES ({target}, 'target'), ({twin}, 'twin')");
        try
        {
            var page = await LoadAsync(conn, "public", table, "tag");
            Assert.Equal(2, page.Rows.Count);
            var row = page.Rows.Single(r => (string?)r[2] == "target");
            var pending = new PendingChanges(3, [0]);
            pending.AttachRows(page.Rows, page.KeyTexts);
            Assert.True(pending.SetCell(row, 1, "42"));
            var target2 = new ChangeTarget("public", table, ["k", "v", "tag"], [0]);
            var statements = ChangeSqlGenerator.Generate(target2, pending);
            var result = await TableDataAccess.CommitAsync(conn, statements);
            output.WriteLine($"{type}: key read as {row[0]?.GetType().Name} '{row[0]}' → {statements[0].LiteralSql} → {result.Error}");
            Assert.True(result.Success, result.Error);
            Assert.Equal(42, Convert.ToInt32(await Scalar(conn, $"SELECT v FROM public.{table} WHERE tag = 'target'")));
            Assert.Equal(0, Convert.ToInt32(await Scalar(conn, $"SELECT v FROM public.{table} WHERE tag = 'twin'")));

            // And the same row can be deleted by its key (the twin survives).
            var page2 = await LoadAsync(conn, "public", table, "tag");
            var p2 = new PendingChanges(3, [0]);
            p2.AttachRows(page2.Rows, page2.KeyTexts);
            p2.DeleteRow(page2.Rows.Single(r => (string?)r[2] == "target"));
            var del = await TableDataAccess.CommitAsync(conn, ChangeSqlGenerator.Generate(target2, p2));
            Assert.True(del.Success, del.Error);
            Assert.Equal("twin", await Scalar(conn, $"SELECT string_agg(tag, ',') FROM public.{table}"));
        }
        finally
        {
            await Exec(conn, $"DROP TABLE IF EXISTS public.{table}");
        }
    }

    [Fact]
    public async Task Composite_quoted_keys_pk_edits_and_edit_then_delete_hit_only_their_rows()
    {
        if (!TestPg.Available(output)) return;
        var schema = "De Schema \"" + Guid.NewGuid().ToString("N")[..8];
        const string table = "Mixed.Table;";
        var qualified = SqlIdent.Qualified(schema, table);
        await using var conn = await OpenAsync();
        await Exec(conn, $"CREATE SCHEMA {SqlIdent.Quote(schema)}; " +
                         $"CREATE TABLE {qualified}(\"Id\" int, \"part key\" text, \"select\" int, PRIMARY KEY (\"Id\", \"part key\")); " +
                         $"INSERT INTO {qualified} VALUES (1, 'a', 0), (1, 'A', 0), (2, 'a', 0), (2, 'b''c', 0), (3, 'x', 0)");
        try
        {
            var q = new TableQuery { Schema = schema, Table = table, Sorts = [new SortSpec("Id", SortDirection.Asc), new SortSpec("part key", SortDirection.Asc)], KeyColumns = ["Id", "part key"] };
            var page = await TableDataAccess.LoadPageAsync(conn, TableQueryBuilder.BuildSelect(q));
            Assert.Equal(5, page.Rows.Count);
            object?[] Row(int id, string part) => page.Rows.Single(r => Convert.ToInt32(r[0]) == id && (string?)r[1] == part);
            var p = new PendingChanges(3, [0, 1]);
            p.AttachRows(page.Rows, page.KeyTexts);
            var (a1, bc2, x3) = (Row(1, "a"), Row(2, "b'c"), Row(3, "x"));
            p.SetCell(a1, 2, "10");          // plain edit
            p.SetCell(bc2, 1, "renamed");    // PK column edited
            p.SetCell(bc2, 2, "20");
            p.SetCell(x3, 2, "30");          // edited (PK too), then deleted: only the DELETE by the original key is sent
            p.SetCell(x3, 0, "99");
            p.DeleteRow(x3);
            var ins = p.InsertRow();
            p.SetCell(ins, 0, "4");
            p.SetCell(ins, 1, "new");
            p.DeleteRow(p.InsertRow());               // inserted then removed: nothing sent
            var statements = ChangeSqlGenerator.Generate(new ChangeTarget(schema, table, ["Id", "part key", "select"], [0, 1]), p);
            foreach (var s in statements) output.WriteLine(s.LiteralSql);
            Assert.Equal(4, statements.Count);
            var result = await TableDataAccess.CommitAsync(conn, statements);
            Assert.True(result.Success, result.Error);
            Assert.Equal("1:A:0,1:a:10,2:a:0,2:renamed:20,4:new:", await Scalar(conn,
                $"SELECT string_agg(\"Id\" || ':' || \"part key\" || ':' || coalesce(\"select\"::text, ''), ',' ORDER BY \"Id\", \"part key\" COLLATE \"C\") FROM {qualified}"));
        }
        finally
        {
            await Exec(conn, $"DROP SCHEMA IF EXISTS {SqlIdent.Quote(schema)} CASCADE");
        }
    }

    [Fact]
    public async Task Update_matching_no_row_or_several_rows_rolls_back_everything()
    {
        if (!TestPg.Available(output)) return;
        var table = Scratch();
        await using var conn = await OpenAsync();
        // A stale "primary key" (the metadata said k, the real table allows duplicates of k): the guard must catch it.
        await Exec(conn, $"CREATE TABLE public.{table}(k int, v int); INSERT INTO public.{table} VALUES (1, 0), (2, 0), (2, 0)");
        try
        {
            var target = new ChangeTarget("public", table, ["k", "v"], [0]);
            foreach (var (key, expectedError) in new[] { ("2", "affected 2 row(s)"), ("3", "affected 0 row(s)") })
            {
                var p = new PendingChanges(2, [0]);
                var rowA = new object?[] { 1, 0 };
                var rowB = new object?[] { key, 0 };
                p.AttachRows([rowA, rowB], [["1"], [key]]);
                p.SetCell(rowA, 1, "5");   // succeeds first…
                p.SetCell(rowB, 1, "7");   // …then this one matches 2 rows / no row
                var result = await TableDataAccess.CommitAsync(conn, ChangeSqlGenerator.Generate(target, p));
                Assert.False(result.Success);
                Assert.Equal(1, result.FailedIndex);
                Assert.Contains(expectedError, result.Error);
                Assert.Equal(0L, await Scalar(conn, $"SELECT count(*) FROM public.{table} WHERE v <> 0"));
                p.DeleteRow(rowB);
                Assert.False((await TableDataAccess.CommitAsync(conn, ChangeSqlGenerator.Generate(target, p))).Success);
                Assert.Equal(3L, await Scalar(conn, $"SELECT count(*) FROM public.{table}"));
            }
        }
        finally
        {
            await Exec(conn, $"DROP TABLE IF EXISTS public.{table}");
        }
    }

    /// <summary>Duplicate Row inserts the loaded values: they must write back as the very same values.</summary>
    [Fact]
    public async Task Loaded_values_insert_back_unchanged()
    {
        if (!TestPg.Available(output)) return;
        var table = Scratch();
        await using var conn = await OpenAsync();
        const string cols = "a, b, c, d, e, f, g, h, i, j";
        await Exec(conn, $"CREATE TABLE public.{table}(id int PRIMARY KEY, a inet, b timestamptz[], c text[], d numeric, e bytea, " +
                         "f interval, g jsonb, h timestamptz, i date[], j cidr); " +
                         $"INSERT INTO public.{table} VALUES (1, '10.0.0.1/8', '{{\"2024-01-01 10:00:00.5+00\",NULL}}', '{{\"a b\",\"NULL\",NULL,\"x,y\",\"q\\\"\"}}', " +
                         "1.2300, '\\x00ff', '1 day 02:00:00.25', '{\"k\": [1, 2.50]}', '2024-07-01 23:59:59.999999+00', '{2024-02-29}', '10.1.0.0/16')");
        try
        {
            var q = new TableQuery { Schema = "public", Table = table, KeyColumns = ["id"] };
            var page = await TableDataAccess.LoadPageAsync(conn, TableQueryBuilder.BuildSelect(q));
            var p = new PendingChanges(page.Columns.Count, [0]);
            p.AttachRows(page.Rows, page.KeyTexts);
            var copy = (object?[])page.Rows[0].Clone();
            copy[0] = "2";
            p.InsertRow(copy, Enumerable.Range(0, page.Columns.Count));
            var statements = ChangeSqlGenerator.Generate(new ChangeTarget("public", table, [.. page.Columns.Select(c => c.Name)], [0]), p);
            output.WriteLine(statements[0].LiteralSql);
            var result = await TableDataAccess.CommitAsync(conn, statements);
            Assert.True(result.Success, result.Error);
            foreach (var col in cols.Split(", "))
            {
                var same = await Scalar(conn, $"SELECT (SELECT {col} FROM public.{table} WHERE id = 1) IS NOT DISTINCT FROM (SELECT {col} FROM public.{table} WHERE id = 2)");
                Assert.True((bool)same!, $"column {col} changed: {await Scalar(conn, $"SELECT string_agg({col}::text, ' vs ' ORDER BY id) FROM public.{table}")}");
            }
        }
        finally
        {
            await Exec(conn, $"DROP TABLE IF EXISTS public.{table}");
        }
    }

    [Fact]
    public async Task Typed_timestamptz_is_read_as_the_utc_wall_time_the_grid_shows()
    {
        if (!TestPg.Available(output)) return;
        var table = Scratch();
        await using var conn = await OpenAsync("Asia/Kolkata");
        await Exec(conn, $"CREATE TABLE public.{table}(id int PRIMARY KEY, at timestamptz); INSERT INTO public.{table} VALUES (1, '2024-01-01 10:00:00+00'), (2, NULL)");
        try
        {
            var q = new TableQuery { Schema = "public", Table = table, Sorts = [new SortSpec("id", SortDirection.Asc)], KeyColumns = ["id"] };
            var page = await TableDataAccess.LoadPageAsync(conn, TableQueryBuilder.BuildSelect(q));
            var shown = (DateTime)page.Rows[0][1]!;
            Assert.Equal(DateTimeKind.Utc, shown.Kind);
            Assert.Equal("2024-01-01 10:00:00", shown.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));

            // The user changes the minutes of what the grid shows (UTC wall time) and types into an empty cell.
            var type = page.Columns[1].DataTypeName;
            Assert.Equal("2024-01-01 10:30:00+00", PgInput.ForColumn(type, "2024-01-01 10:30:00"));
            Assert.Equal("2024-01-01 10:30:00+02", PgInput.ForColumn(type, "2024-01-01 10:30:00+02"));
            Assert.Equal("now", PgInput.ForColumn(type, "now"));
            Assert.Equal("2024-01-01 10:30:00", PgInput.ForColumn("timestamp without time zone", "2024-01-01 10:30:00"));
            var p = new PendingChanges(2, [0]);
            p.AttachRows(page.Rows, page.KeyTexts);
            p.SetCell(page.Rows[0], 1, PgInput.ForColumn(type, "2024-01-01 10:30:00"));
            p.SetCell(page.Rows[1], 1, PgInput.ForColumn(type, "2024-06-01T08:00"));
            var result = await TableDataAccess.CommitAsync(conn, ChangeSqlGenerator.Generate(new ChangeTarget("public", table, ["id", "at"], [0]), p));
            Assert.True(result.Success, result.Error);
            Assert.Equal("2024-01-01 10:30:00", await Scalar(conn, $"SELECT to_char(at AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS') FROM public.{table} WHERE id = 1"));
            Assert.Equal("2024-06-01 08:00:00", await Scalar(conn, $"SELECT to_char(at AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS') FROM public.{table} WHERE id = 2"));

            // Without the suffix the session time zone (+05:30) shifts the instant: what the editor used to send.
            await Exec(conn, $"UPDATE public.{table} SET at = '2024-01-01 10:30:00' WHERE id = 1");
            Assert.Equal("2024-01-01 05:00:00", await Scalar(conn, $"SELECT to_char(at AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS') FROM public.{table} WHERE id = 1"));
        }
        finally
        {
            await Exec(conn, $"DROP TABLE IF EXISTS public.{table}");
        }
    }

    [Fact]
    public async Task Raw_filter_cannot_smuggle_a_second_statement_past_safe_mode()
    {
        if (!TestPg.Available(output)) return;
        var table = Scratch();
        await using var conn = await OpenAsync();
        await Exec(conn, $"CREATE TABLE public.{table}(id int PRIMARY KEY); INSERT INTO public.{table} SELECT generate_series(1, 5)");
        try
        {
            foreach (var raw in new[] { $"false; DELETE FROM public.{table}; SELECT 1", $"true); DELETE FROM public.{table}; SELECT (1" })
            {
                var q = new TableQuery { Schema = "public", Table = table, Filters = [FilterCondition.Raw(raw)], Limit = 300 };
                try
                {
                    await TableDataAccess.LoadPageAsync(conn, TableQueryBuilder.BuildSelect(q));
                }
                catch (Exception ex) when (ex is ArgumentException or PostgresException)
                {
                    output.WriteLine(ex.Message);
                }
                Assert.Equal(5L, await Scalar(conn, $"SELECT count(*) FROM public.{table}"));
            }
        }
        finally
        {
            await Exec(conn, $"DROP TABLE IF EXISTS public.{table}");
        }
    }

    [Fact]
    public async Task Raw_filter_line_comment_does_not_swallow_the_page_limit()
    {
        if (!TestPg.Available(output)) return;
        await using var conn = await OpenAsync();
        var q = new TableQuery
        {
            Schema = "sales", Table = "orders", Filters = [FilterCondition.Raw("id > 0 -- big ones")],
            Sorts = [new SortSpec("id", SortDirection.Asc)], Limit = 300, Offset = 10,
        };
        var page = await TableDataAccess.LoadPageAsync(conn, TableQueryBuilder.BuildSelect(q));
        Assert.Equal(300, page.Rows.Count);
        Assert.Equal(11L, Convert.ToInt64(page.Rows[0][0]));
        Assert.Equal(199_999L, await TableDataAccess.CountAsync(conn, TableQueryBuilder.BuildCount(q with { Filters = [FilterCondition.Raw("id > 1 -- c")] })));
    }
}
