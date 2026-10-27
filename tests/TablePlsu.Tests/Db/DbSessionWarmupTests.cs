using System.Diagnostics;
using Npgsql;
using TablePlsu.Core.Db;
using TablePlsu.Tests.Infrastructure;
using Xunit.Abstractions;

namespace TablePlsu.Tests.Db;

/// <summary>Connection warm-up: what the first query and the first table page pay after <see cref="DbSession.ConnectAsync"/>.</summary>
public class DbSessionWarmupTests(ITestOutputHelper output)
{
    /// <summary>Server backends opened by this session (its unique application_name), counted on an outside connection.</summary>
    private static async Task<long> BackendsAsync(string app)
    {
        await using var conn = new NpgsqlConnection(TestPg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select count(*) from pg_stat_activity where application_name = $1", conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = app });
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task After_connect_the_first_query_and_first_extra_connection_pay_no_handshake()
    {
        if (!TestPg.Available(output)) return;
        var (profile, secrets) = TestPg.Profile();
        var app = "warmup_" + Guid.NewGuid().ToString("N")[..8];
        await using var s = new DbSession(profile, secrets, null, new DbSessionOptions { ApplicationName = app, KeepAlive = false });
        var sw = Stopwatch.StartNew();
        await s.ConnectAsync();
        var connectMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var r1 = await s.ExecuteAsync("select 1");
        var first = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        await s.ExecuteAsync("select 1");
        var second = sw.Elapsed.TotalMilliseconds;
        Assert.Null(r1.Statements[0].Error);

        // The table tab, row counts and commits use extra pooled connections: the first one must not open a new
        // physical connection (TCP + TLS + auth + bootstrap: hundreds of ms over SSH) when the session warmed the pool.
        for (var i = 0; i < 50 && await BackendsAsync(app) < 2; i++) await Task.Delay(50); // background warm-up
        var before = await BackendsAsync(app);
        sw.Restart();
        await using (var c = await s.OpenConnectionAsync())
        {
            await using var cmd = new NpgsqlCommand("select 1", c);
            await cmd.ExecuteScalarAsync();
        }
        var extraMs = sw.Elapsed.TotalMilliseconds;
        var opened = await BackendsAsync(app) - before;
        output.WriteLine($"connect {connectMs:F0} ms; first select 1 {first:F1} ms, second {second:F1} ms; first extra pooled connection + select 1 {extraMs:F1} ms (new backends: {opened})");
        Assert.Equal(0, opened);
    }
}
