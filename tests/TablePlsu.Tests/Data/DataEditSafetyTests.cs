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
