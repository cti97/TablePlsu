using System.Diagnostics;
using TablePlsu.Core.Db;
using TablePlsu.Tests.Infrastructure;
using Xunit.Abstractions;

namespace TablePlsu.Tests.Db;

/// <summary>Synchronous, ordered IProgress (Progress&lt;T&gt; posts to the thread pool and can reorder).</summary>
internal sealed class ListProgress<T> : IProgress<T>
{
    public List<T> Items { get; } = [];
    public void Report(T value) { lock (Items) Items.Add(value); }
}

public class DbSessionIntegrationTests(ITestOutputHelper output)
{
    private static async Task<DbSession> OpenAsync(string? bootstrap = null)
    {
        var (profile, secrets) = TestPg.Profile();
        if (bootstrap is not null) profile = profile with { Bootstrap = bootstrap };
        var session = new DbSession(profile, secrets);
        await session.ConnectAsync();
        return session;
    }

    [Fact]
    public async Task Connects_and_reports_server_version()
    {
        if (!TestPg.Available(output)) return;
        await using var s = await OpenAsync();
        Assert.Equal(DbSessionState.Connected, s.State);
        Assert.False(string.IsNullOrEmpty(s.ServerVersion));
        Assert.True(await s.KeepAliveAsync());
    }

    [Fact]
    public async Task Streams_200k_orders_with_row_limit_fast()
    {
        if (!TestPg.Available(output)) return;
        await using var s = await OpenAsync();
        await s.ExecuteAsync("select 1", new ExecuteOptions()); // warm-up
        var progress = new ListProgress<ResultBatch>();
        var sw = Stopwatch.StartNew();
        var result = await s.ExecuteAsync("select * from sales.orders", new ExecuteOptions { RowLimit = 1000 }, progress);
        sw.Stop();
        output.WriteLine($"elapsed {sw.ElapsedMilliseconds} ms");

        var st = Assert.Single(result.Statements);
        Assert.Null(st.Error);
        Assert.True(st.HasResultSet);
        Assert.Equal(1000, st.Rows.Count);
        Assert.True(st.Truncated);
        Assert.Equal(5, st.Columns.Count);
        Assert.Equal("id", st.Columns[0].Name);
        Assert.Equal("bigint", st.Columns[0].DataTypeName);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"took {sw.Elapsed}");

        var rowBatches = progress.Items.Where(b => b.Rows.Count > 0).ToList();
        Assert.All(rowBatches, b => Assert.True(b.Rows.Count <= 200));
        Assert.Equal(1000, rowBatches.Sum(b => b.Rows.Count));
        Assert.Equal(5, progress.Items[0].Columns.Count);
        Assert.True(progress.Items[^1].IsFinal);
        Assert.Same(st, progress.Items[^1].Result);

        // The connection is still usable after the early stop.
        var after = await s.ExecuteAsync("select count(*) from public.customers", new ExecuteOptions());
        Assert.Equal(5000L, after.Statements[0].Rows[0][0]);
    }

    [Fact]
    public async Task No_limit_reads_everything()
    {
        if (!TestPg.Available(output)) return;
        await using var s = await OpenAsync();
        var result = await s.ExecuteAsync("select id from public.customers", new ExecuteOptions { RowLimit = null });
        Assert.Equal(5000, result.Statements[0].Rows.Count);
        Assert.False(result.Statements[0].Truncated);
    }

    [Fact]
    public async Task Cancel_stops_pg_sleep_quickly()
    {
        if (!TestPg.Available(output)) return;
        await using var s = await OpenAsync();
        var sw = Stopwatch.StartNew();
        var run = s.ExecuteAsync("select pg_sleep(10); select 1", new ExecuteOptions());
        await Task.Delay(300);
        Assert.True(s.IsBusy);
        s.Cancel();
        var result = await run;
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1.3), $"took {sw.Elapsed}");
        Assert.True(result.Cancelled);
        Assert.True(result.Statements[0].Cancelled);
        Assert.Single(result.Statements); // second statement not run after cancel
        Assert.False(s.IsBusy);

        var after = await s.ExecuteAsync("select 2", new ExecuteOptions());
        Assert.Equal(2, after.Statements[0].Rows[0][0]);
    }

    [Fact]
    public async Task CancellationToken_cancels()
    {
        if (!TestPg.Available(output)) return;
        await using var s = await OpenAsync();
        using var cts = new CancellationTokenSource(300);
        var sw = Stopwatch.StartNew();
        var result = await s.ExecuteAsync("select pg_sleep(10)", new ExecuteOptions(), null, cts.Token);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1.3), $"took {sw.Elapsed}");
        Assert.True(result.Cancelled);
    }

    [Fact]
    public async Task Multi_statement_with_error_position_and_continue()
    {
        if (!TestPg.Available(output)) return;
        await using var s = await OpenAsync();
        const string sql = "select 1 as a;\n\nselect * from nope_table;\nselect 3 as c";
        var result = await s.ExecuteAsync(sql, new ExecuteOptions { ReturnOnError = false });
        Assert.Equal(3, result.Statements.Count);
        Assert.Null(result.Statements[0].Error);
        var err = result.Statements[1].Error;
        Assert.NotNull(err);
        Assert.Equal("42P01", err!.SqlState);
        Assert.Equal(sql.IndexOf("nope_table", StringComparison.Ordinal), err.Position);
        Assert.Equal(3, err.Line);
        Assert.Equal(3, result.Statements[2].Rows[0][0]);
        Assert.True(result.HasErrors);
        Assert.Equal(sql.IndexOf("select * from nope", StringComparison.Ordinal), result.Statements[1].Statement.Start);
    }

    [Fact]
    public async Task Return_on_error_stops_after_failure()
    {
        if (!TestPg.Available(output)) return;
        await using var s = await OpenAsync();
        var result = await s.ExecuteAsync("select 1; select 1/0; select 3", new ExecuteOptions { ReturnOnError = true });
        Assert.Equal(2, result.Statements.Count);
        Assert.Equal("22012", result.Statements[1].Error!.SqlState);
    }

    [Fact]
    public async Task Affected_rows_command_tag_and_elapsed()
    {
        if (!TestPg.Available(output)) return;
        await using var s = await OpenAsync();
        var result = await s.ExecuteAsync(
            "create temp table t_aff(x int); insert into t_aff select generate_series(1,7); update t_aff set x = x + 1 where x > 4; select * from t_aff",
            new ExecuteOptions());
        Assert.All(result.Statements, st => Assert.Null(st.Error));
        Assert.Equal(7, result.Statements[1].AffectedRows);
        Assert.Equal("INSERT", result.Statements[1].CommandTag);
        Assert.Equal(3, result.Statements[2].AffectedRows);
        Assert.False(result.Statements[2].HasResultSet);
        Assert.True(result.Statements[3].HasResultSet);
        Assert.Equal(7, result.Statements[3].Rows.Count);
        Assert.All(result.Statements, st => Assert.True(st.Elapsed > TimeSpan.Zero));
    }

    [Fact]
    public async Task Notices_are_captured_per_statement()
    {
        if (!TestPg.Available(output)) return;
        await using var s = await OpenAsync();
        var result = await s.ExecuteAsync(
            "do $$ begin raise notice 'hello %', 42; raise notice 'second'; end $$; select 1",
            new ExecuteOptions());
        Assert.Equal(new[] { "hello 42", "second" }, result.Statements[0].Notices.Select(n => n.Message));
        Assert.Equal("NOTICE", result.Statements[0].Notices[0].Severity);
        Assert.Empty(result.Statements[1].Notices);
    }

    [Fact]
    public async Task Bootstrap_runs_on_each_physical_connection()
    {
        if (!TestPg.Available(output)) return;
        await using var s = await OpenAsync("SET search_path TO sales, public; SET application_name = 'boot-test'");
        var r = await s.ExecuteAsync("select count(*) from orders; show application_name", new ExecuteOptions());
        Assert.Equal(200000L, r.Statements[0].Rows[0][0]);
        Assert.Equal("boot-test", r.Statements[1].Rows[0][0]);

        var meta = await s.WithMetadataConnectionAsync(async (conn, ct) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "show search_path";
            return (string)(await cmd.ExecuteScalarAsync(ct))!;
        });
        Assert.StartsWith("sales", meta);
    }

    [Fact]
    public async Task Metadata_connection_works_while_query_runs()
    {
        if (!TestPg.Available(output)) return;
        await using var s = await OpenAsync();
        var run = s.ExecuteAsync("select pg_sleep(2)", new ExecuteOptions());
        await Task.Delay(200);
        var sw = Stopwatch.StartNew();
        var n = await s.WithMetadataConnectionAsync(async (conn, ct) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "select count(*) from pg_namespace";
            return (long)(await cmd.ExecuteScalarAsync(ct))!;
        });
        Assert.True(n > 0);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1.5));
        s.Cancel();
        await run;
    }

    [Fact]
    public async Task Values_are_display_friendly()
    {
        if (!TestPg.Available(output)) return;
        await using var s = await OpenAsync();
        var r = await s.ExecuteAsync(
            "select '\\x0102'::bytea, array[1,2,null]::int[], '{\"a\":1}'::jsonb, null::text, meta from public.customers where id = 1",
            new ExecuteOptions());
        var row = r.Statements[0].Rows[0];
        Assert.Equal("\\x0102", row[0]);
        Assert.Equal("{1,2,NULL}", row[1]);
        Assert.Equal("{\"a\": 1}", row[2]);
        Assert.Null(row[3]);
        Assert.IsType<string>(row[4]);
    }

    [Fact]
    public async Task Reconnect_restores_session()
    {
        if (!TestPg.Available(output)) return;
        await using var s = await OpenAsync();
        var states = new List<DbSessionState>();
        s.StateChanged += (_, st) => states.Add(st);
        await s.ReconnectAsync();
        Assert.Equal(DbSessionState.Connected, s.State);
        Assert.Contains(DbSessionState.Connecting, states);
        var r = await s.ExecuteAsync("select 5", new ExecuteOptions());
        Assert.Equal(5, r.Statements[0].Rows[0][0]);
    }

    [Fact]
    public async Task Connect_failure_reports_error_state()
    {
        if (!TestPg.Available(output)) return;
        var (profile, secrets) = TestPg.Profile();
        await using var s = new DbSession(profile with { Database = "no_such_db_xyz" }, secrets);
        await Assert.ThrowsAnyAsync<Exception>(() => s.ConnectAsync());
        Assert.Equal(DbSessionState.Broken, s.State);
    }

    [Fact]
    public async Task Empty_sql_returns_no_statements()
    {
        if (!TestPg.Available(output)) return;
        await using var s = await OpenAsync();
        var r = await s.ExecuteAsync("  -- nothing\n", new ExecuteOptions());
        Assert.Empty(r.Statements);
    }
}
