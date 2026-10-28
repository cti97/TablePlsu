using Npgsql;
using TablePlsu.Core.Data;
using TablePlsu.Tests.Infrastructure;
using Xunit.Abstractions;

namespace TablePlsu.Tests.Data;

public class TableDataIntegrationTests(ITestOutputHelper output)
{
    private static async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(TestPg.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private static readonly QueryColumn[] OrderCols =
    [
        new("id", "bigint"), new("customer_id", "integer"), new("total", "numeric(12,2)"), new("status", "text"), new("placed_at", "date"),
    ];

    [Fact]
    public async Task Fetches_a_filtered_sorted_page_from_sales_orders()
    {
        if (!TestPg.Available(output)) return;
        await using var conn = await OpenAsync();
        var q = new TableQuery
        {
            Schema = "sales", Table = "orders", Columns = OrderCols,
            Filters = [FilterCondition.For("status", FilterOperator.Equal, "paid"), FilterCondition.For("total", FilterOperator.Between, "10", "20")],
            Sorts = [new("id", SortDirection.Desc)],
            Limit = 50, Offset = 10,
        };
        var page = await TableDataAccess.LoadPageAsync(conn, TableQueryBuilder.BuildSelect(q));
        Assert.Equal(["id", "customer_id", "total", "status", "placed_at"], page.Columns.Select(c => c.Name));
        Assert.Equal(50, page.Rows.Count);
        Assert.All(page.Rows, r =>
        {
            Assert.Equal("paid", r[3]);
            Assert.InRange((decimal)r[2]!, 10m, 20m);
        });
        var ids = page.Rows.Select(r => (long)r[0]!).ToList();
        Assert.Equal(ids.OrderByDescending(x => x), ids);

        var count = await TableDataAccess.CountAsync(conn, TableQueryBuilder.BuildCount(q));
        Assert.True(count > 60);
        var estimate = await TableDataAccess.EstimateRowsAsync(conn, "sales", "orders");
        output.WriteLine($"count {count}, estimate {estimate}, page {page.Elapsed.TotalMilliseconds} ms");
    }

    [Theory]
    [InlineData(FilterOperator.In, "1,2,3", null, 3)]
    [InlineData(FilterOperator.IsNull, null, null, 0)]
    [InlineData(FilterOperator.LessOrEqual, "5", null, 5)]
    [InlineData(FilterOperator.HasPrefix, "1000", null, 1)]
    public async Task Typed_comparisons_work_with_unknown_typed_parameters(FilterOperator op, string? value, string? value2, int expected)
    {
        if (!TestPg.Available(output)) return;
        await using var conn = await OpenAsync();
        var q = new TableQuery
        {
            Schema = "sales", Table = "orders", Columns = OrderCols,
            Filters = [FilterCondition.For("id", op, value, value2), FilterCondition.For("id", FilterOperator.LessOrEqual, "1000")],
        };
        Assert.Equal(expected, await TableDataAccess.CountAsync(conn, TableQueryBuilder.BuildCount(q)));
    }

    [Fact]
    public async Task Any_column_and_raw_filters()
    {
        if (!TestPg.Available(output)) return;
        await using var conn = await OpenAsync();
        var q = new TableQuery
        {
            Schema = "public", Table = "customers",
            Columns = [new("id", "integer"), new("name", "text"), new("email", "text"), new("meta", "jsonb")],
            Filters = [FilterCondition.Any(FilterOperator.IContains, "c4999@"), FilterCondition.Raw("id < 10000")],
        };
        var page = await TableDataAccess.LoadPageAsync(conn, TableQueryBuilder.BuildSelect(q));
        var row = Assert.Single(page.Rows);
        Assert.Equal("c4999@x.com", row[2]);
    }

    [Fact]
    public async Task Commit_runs_in_one_transaction_and_rolls_back_on_error()
    {
        if (!TestPg.Available(output)) return;
        var table = "td_commit_" + Guid.NewGuid().ToString("N")[..10];
        await using var conn = await OpenAsync();
        await using (var c = new NpgsqlCommand($"CREATE TABLE public.{table}(id serial primary key, name text not null, qty int default 7, at timestamptz)", conn))
            await c.ExecuteNonQueryAsync();
        try
        {
            await using (var c = new NpgsqlCommand($"INSERT INTO public.{table}(name, qty, at) VALUES ('a', 1, '2024-01-02 03:04:05.123456+00'), ('b', 2, now()), ('c', 3, now())", conn))
                await c.ExecuteNonQueryAsync();

            var cols = new QueryColumn[] { new("id", "integer"), new("name", "text"), new("qty", "integer"), new("at", "timestamp with time zone") };
            var q = new TableQuery { Schema = "public", Table = table, Columns = cols, Sorts = [new("id", SortDirection.Asc)] };
            var page = await TableDataAccess.LoadPageAsync(conn, TableQueryBuilder.BuildSelect(q));
            var target = new ChangeTarget("public", table, cols.Select(c => c.Name).ToList(), [0]);

            // 1) An error in the last statement rolls back everything and keeps the changes staged.
            var p = new PendingChanges(4, [0]);
            p.AttachRows(page.Rows);
            p.SetCell(page.Rows[0], 2, "100");
            p.DeleteRow(page.Rows[2]);
            var bad = p.InsertRow([null, null, null, null], [1]); // name NULL violates NOT NULL
            var statements = ChangeSqlGenerator.Generate(target, p);
            var result = await TableDataAccess.CommitAsync(conn, statements);
            Assert.False(result.Success);
            Assert.Equal(2, result.FailedIndex);
            output.WriteLine(result.Error);
            Assert.Contains("null", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(3L, await ScalarAsync(conn, $"SELECT count(*) FROM public.{table}"));
            Assert.Equal(1, Convert.ToInt32(await ScalarAsync(conn, $"SELECT qty FROM public.{table} WHERE id = 1")));
            Assert.True(p.IsDirty);

            // 2) Fix and commit: all statements land together.
            p.SetCell(bad, 1, "d");
            p.SetCell(page.Rows[1], 3, SqlExpression.Now);
            p.SetCell(page.Rows[1], 1, "it's");
            result = await TableDataAccess.CommitAsync(conn, ChangeSqlGenerator.Generate(target, p));
            Assert.True(result.Success, result.Error);
            Assert.Equal(3L, await ScalarAsync(conn, $"SELECT count(*) FROM public.{table}"));
            Assert.Equal(100, Convert.ToInt32(await ScalarAsync(conn, $"SELECT qty FROM public.{table} WHERE id = 1")));
            Assert.Equal("it's", await ScalarAsync(conn, $"SELECT name FROM public.{table} WHERE id = 2"));
            Assert.Equal(7, Convert.ToInt32(await ScalarAsync(conn, $"SELECT qty FROM public.{table} WHERE name = 'd'")));
            Assert.Equal(0L, await ScalarAsync(conn, $"SELECT count(*) FROM public.{table} WHERE id = 3"));

            // 2b) RETURNING: the committed rows come back as the server stored them (DEFAULT filled, text cast).
            var pr = new PendingChanges(4, [0]);
            var fresh = (await TableDataAccess.LoadPageAsync(conn, TableQueryBuilder.BuildSelect(q))).Rows;
            pr.AttachRows(fresh);
            pr.SetCell(fresh[0], 2, "41");
            pr.InsertRow([null, "e", null, null], [1]);
            var withReturning = ChangeSqlGenerator.Generate(target, pr, returnRows: true);
            Assert.Equal($"UPDATE public.{table} SET qty = '41' WHERE id = 1 RETURNING id, name, qty, at", withReturning[0].LiteralSql);
            Assert.All(withReturning, s => Assert.True(s.ReturnsRows));
            result = await TableDataAccess.CommitAsync(conn, withReturning);
            Assert.True(result.Success, result.Error);
            Assert.Equal([1L, 1L], result.Affected);
            var updatedRow = Assert.Single(result.Returned[0]!);
            Assert.Equal(41, updatedRow[2]);
            var insertedRow = Assert.Single(result.Returned[1]!);
            Assert.Equal("e", insertedRow[1]);
            Assert.Equal(7, insertedRow[2]);
            Assert.IsType<int>(insertedRow[0]);
            await using (var c = new NpgsqlCommand($"UPDATE public.{table} SET qty = 100 WHERE id = 1; DELETE FROM public.{table} WHERE name = 'e'", conn))
                await c.ExecuteNonQueryAsync();

            // 3) A row that vanished makes the UPDATE affect 0 rows: rolled back.
            var p2 = new PendingChanges(4, [0]);
            var ghost = new object?[] { 999, "x", 1, null };
            p2.AttachRows([ghost, page.Rows[0]]);
            p2.SetCell(page.Rows[0], 2, "5");
            p2.SetCell(ghost, 1, "y");
            result = await TableDataAccess.CommitAsync(conn, ChangeSqlGenerator.Generate(target, p2));
            Assert.False(result.Success);
            Assert.Equal(1, result.FailedIndex);
            Assert.Equal(100, Convert.ToInt32(await ScalarAsync(conn, $"SELECT qty FROM public.{table} WHERE id = 1")));

            // 4) Timestamp primary-key style values round-trip through the text parameters (WHERE on a timestamptz).
            var reread = await TableDataAccess.LoadPageAsync(conn, TableQueryBuilder.BuildSelect(q with
            {
                Filters = [FilterCondition.For("at", FilterOperator.Equal, "2024-01-02 03:04:05.123456+00")],
            }));
            var r = Assert.Single(reread.Rows);
            var tsTarget = new ChangeTarget("public", table, target.Columns, [3]);
            var p3 = new PendingChanges(4, [3]);
            p3.AttachRows(reread.Rows);
            p3.SetCell(r, 1, "by-ts");
            result = await TableDataAccess.CommitAsync(conn, ChangeSqlGenerator.Generate(tsTarget, p3));
            Assert.True(result.Success, result.Error);
        }
        finally
        {
            await using var c = new NpgsqlCommand($"DROP TABLE IF EXISTS public.{table}", conn);
            await c.ExecuteNonQueryAsync();
        }
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection conn, string sql)
    {
        await using var c = new NpgsqlCommand(sql, conn);
        return await c.ExecuteScalarAsync();
    }
}
