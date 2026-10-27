using System.Diagnostics;
using TablePlsu.Core.Db;
using TablePlsu.Tests.Infrastructure;
using Xunit.Abstractions;

namespace TablePlsu.Tests.Db;

/// <summary>DbSession alone (no UI): time to the first batch and to the end for 200k rows, and the rows' memory.</summary>
public class DbSessionStreamingPerfTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Streams_all_200k_orders_first_batch_fast()
    {
        if (!TestPg.Available(output)) return;
        var (profile, secrets) = TestPg.Profile();
        await using var s = new DbSession(profile, secrets);
        await s.ConnectAsync();
        await s.ExecuteAsync("select * from sales.orders limit 10"); // warm-up
        for (var round = 0; round < 2; round++)
        {
            double first = -1;
            var sw = Stopwatch.StartNew();
            var progress = new ListProgressFirst(() => { if (first < 0) first = sw.Elapsed.TotalMilliseconds; });
            var before = GC.GetTotalMemory(true);
            var r = await s.ExecuteAsync("select * from sales.orders", new ExecuteOptions { RowLimit = null }, progress);
            var total = sw.Elapsed.TotalMilliseconds;
            var bytes = GC.GetTotalMemory(true) - before;
            Assert.Equal(200_000, r.Statements[0].Rows.Count);
            if (round == 0) output.WriteLine($"200k rows: first batch {first:F1} ms, all {total:F0} ms, {bytes / 1024.0 / 1024:F1} MB ({bytes / 200_000} B/row)");
            GC.KeepAlive(r);
            Assert.True(first < 200, $"first batch after {first:F0} ms");
        }
    }

    /// <summary>
    /// Cost of the row-limit cut-off (cancel request on a second connection + draining the cancelled reader) compared
    /// with a query that ends by itself after the same number of rows. Interleaved, median of 7.
    /// </summary>
    [Fact]
    [Trait("Category", "Perf")]
    public async Task Row_limit_cutoff_cost_vs_natural_end()
    {
        if (!TestPg.Available(output)) return;
        var (profile, secrets) = TestPg.Profile();
        await using var s = new DbSession(profile, secrets);
        await s.ConnectAsync();
        var cut = new List<double>();
        var natural = new List<double>();
        for (var i = 0; i < 8; i++)
        {
            var sw = Stopwatch.StartNew();
            var a = await s.ExecuteAsync("select * from sales.orders", new ExecuteOptions { RowLimit = 1000 });
            var cutMs = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            var b = await s.ExecuteAsync("select * from sales.orders limit 1001", new ExecuteOptions { RowLimit = null });
            var naturalMs = sw.Elapsed.TotalMilliseconds;
            Assert.True(a.Statements[0].Truncated);
            Assert.Equal(1000, a.Statements[0].Rows.Count);
            Assert.Equal(1001, b.Statements[0].Rows.Count);
            if (i == 0) continue; // warm-up
            cut.Add(cutMs);
            natural.Add(naturalMs);
        }
        cut.Sort();
        natural.Sort();
        output.WriteLine($"row limit 1000 on 200k rows (cancel): median {cut[cut.Count / 2]:F1} ms; same rows ending naturally: median {natural[natural.Count / 2]:F1} ms");
    }

    private sealed class ListProgressFirst(Action onFirst) : IProgress<ResultBatch>
    {
        public void Report(ResultBatch value) { if (value.Rows.Count > 0) onFirst(); }
    }
}
