using System.Diagnostics;
using System.Windows.Threading;
using TablePlsu.App.Features.SafeMode;
using TablePlsu.App.Features.TableData;
using TablePlsu.App.Features.Workspace;
using TablePlsu.Core.Db;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Data;
using Xunit.Abstractions;

namespace TablePlsu.App.Tests.Perf;

/// <summary>
/// Table tab on sales.orders (200k rows, page 300), view-model level on a pumping UI thread: open → page applied,
/// row count, sort and filter round trips. (Window creation is left out: on boxes with a UI Automation client it
/// takes seconds and says nothing about the app.)
/// </summary>
[Collection(AppServicesCollection.Name)]
public class TablePerfTests(ITestOutputHelper output)
{
    /// <summary>Polls <paramref name="condition"/> from a dispatcher timer and returns the elapsed time when it holds.</summary>
    private static async Task<double> Until(Func<bool> condition, Stopwatch sw, Func<string> describe, int timeoutMs = 20_000)
    {
        if (condition()) return sw.Elapsed.TotalMilliseconds;
        var tcs = new TaskCompletionSource<double?>();
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(1) };
        timer.Tick += (_, _) =>
        {
            if (condition()) { timer.Stop(); tcs.TrySetResult(sw.Elapsed.TotalMilliseconds); }
            else if (sw.ElapsedMilliseconds > timeoutMs) { timer.Stop(); tcs.TrySetResult(null); }
        };
        timer.Start();
        return await tcs.Task ?? throw new TimeoutException("condition not reached: " + describe());
    }

    [Fact]
    public void Sales_orders_open_count_sort_and_filter_round_trips()
    {
        if (!TestPg.Available(output)) return;
        PerfHarness.OnUiThread(async () =>
        {
            TestApp.Init(new WorkspaceFeature(), new TableDataFeature(), new SafeModeFeature());
            var (profile, secrets) = TestPg.Profile();
            var ws = new WorkspaceViewModel(profile, secrets, null, null);
            await ws.ConnectAsync();
            var sw = Stopwatch.StartNew();
            await Until(() => ws.State == DbSessionState.Connected, sw, () => "not connected");
            await ws.Metadata.RefreshAsync();
            await Task.Delay(300); // a user clicks a table a moment after connecting
            try
            {
                foreach (var table in new[] { "customers", "orders" }) // customers: warm-up (JIT)
                {
                    sw.Restart();
                    var tab = Assert.IsType<TableTabViewModel>(ws.OpenTab(TabRequest.ForObject(table == "orders" ? "sales" : "public", table, ObjectKinds.Table, pinned: true)));
                    string Describe() => $"rows {tab.Rows.Count} busy {tab.IsBusy} status {tab.StatusText} count {tab.RowCountText} error {tab.ErrorText}";
                    var page = await Until(() => tab.Rows.Count == 300 && !tab.IsBusy, sw, Describe);
                    var count = await Until(() => tab.RowCountText.EndsWith(" rows", StringComparison.Ordinal), sw, Describe);
                    if (table != "orders") { ws.CloseTab(tab); continue; }
                    Assert.Equal("200,000 rows", tab.RowCountText);

                    sw.Restart();
                    tab.OnSortRequested("id", addToMulti: false);
                    var sort = await Until(() => !tab.IsBusy && tab.Rows.Count == 300 && Convert.ToInt64(tab.Rows[0][0]) == 200_000, sw, Describe);

                    await Until(() => tab.Filter.ColumnOptions.Any(o => o.Column == "status"), sw, Describe);
                    sw.Restart();
                    tab.Filter.Rows[0].Column = tab.Filter.ColumnOptions.First(o => o.Column == "status");
                    tab.Filter.Rows[0].Value = "paid";
                    tab.Filter.ApplyAll();
                    var filter = await Until(() => !tab.IsBusy && tab.Rows.Count > 0 && tab.Rows.All(r => (string?)r[3] == "paid"), sw, Describe);
                    var filterCount = await Until(() => tab.RowCountText == "66,667 rows", sw, Describe);
                    output.WriteLine($"sales.orders: page applied {page:F0} ms, exact row count {count:F0} ms; sort round trip {sort:F0} ms; filter round trip {filter:F0} ms, filtered count {filterCount:F0} ms");
                    Assert.True(page < 500, $"page {page:F0} ms");
                    Assert.True(sort < 500, $"sort {sort:F0} ms");
                    Assert.True(filter < 500, $"filter {filter:F0} ms");
                    tab.Filter.Clear();
                    ws.CloseTab(tab);
                }
            }
            finally
            {
                await ws.CloseAsync();
            }
        });
    }
}
