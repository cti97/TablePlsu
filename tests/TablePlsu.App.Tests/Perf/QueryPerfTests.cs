using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Threading;
using TablePlsu.App.Features.SqlEditor;
using TablePlsu.App.Features.Workspace;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Db;
using Xunit.Abstractions;

namespace TablePlsu.App.Tests.Perf;

/// <summary>
/// End to end: the real SQL editor view in a window, Run Current on <c>select * from sales.orders</c> (seed: 200k rows),
/// measuring time to the first painted grid row, total time, the longest UI-thread stall while streaming, and memory.
/// </summary>
[Collection(AppServicesCollection.Name)]
public class QueryPerfTests(ITestOutputHelper output)
{
    private const double MaxStallMs = 50;

    internal static bool UiaListening =>
        System.Windows.Automation.Peers.AutomationPeer.ListenerExists(System.Windows.Automation.Peers.AutomationEvents.StructureChanged)
        || System.Windows.Automation.Peers.AutomationPeer.ListenerExists(System.Windows.Automation.Peers.AutomationEvents.PropertyChanged);

    internal static int Count<T>(System.Windows.DependencyObject d) where T : System.Windows.DependencyObject
    {
        var n = d is T ? 1 : 0;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++) n += Count<T>(System.Windows.Media.VisualTreeHelper.GetChild(d, i));
        return n;
    }

    [Fact]
    public void Run_select_orders_paints_fast_and_never_blocks_the_ui_thread()
    {
        if (!TestPg.Available(output)) return;
        PerfHarness.OnUiThread(async () =>
        {
            TestApp.Init(new WorkspaceFeature(), new SqlEditorFeature());
            var (profile, secrets) = TestPg.Profile();
            var ws = new WorkspaceViewModel(profile, secrets, null, shell: null);
            AppServices.Features.RaiseWorkspaceOpened(ws);
            await ws.ConnectAsync();
            for (var i = 0; i < 100 && ws.State != DbSessionState.Connected; i++) await Task.Delay(50);
            Assert.Equal(DbSessionState.Connected, ws.State);
            var tab = (SqlEditorTabViewModel)ws.NewTab();
            var view = (SqlEditorView)tab.CreateView();
            var window = PerfHarness.Host(view);
            using var probe = new StallProbe(Dispatcher.CurrentDispatcher);
            using var prof = new OpProfiler(Dispatcher.CurrentDispatcher);
            try
            {
                await PerfHarness.Yield();
                var grid = view.ResultGrid.DataGrid;

                async Task<(double FirstPaint, double Total, double Stall, long Bytes, int Rows)> RunOnce(int limit)
                {
                    tab.Results.Clear();
                    tab.SelectedResult = null;
                    await PerfHarness.Yield();
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    var before = GC.GetTotalMemory(true);
                    tab.RowLimit = limit;
                    tab.Document.Text = "select * from sales.orders";
                    tab.SetSelection(caret: 3);
                    probe.Reset();
                    prof.Reset();
                    var clock = Stopwatch.StartNew();
                    var painted = PerfHarness.WhenRendered(
                        () => grid.ItemContainerGenerator.ContainerFromIndex(0) is DataGridRow { IsLoaded: true },
                        clock, TimeSpan.FromSeconds(60));
                    await tab.RunCurrentAsync();
                    var total = clock.Elapsed.TotalMilliseconds;
                    var first = (await painted).TotalMilliseconds;
                    await PerfHarness.Yield();
                    var stall = probe.MaxStallMs;
                    output.WriteLine($"limit {limit}: slowest UI ops: {prof.Top()}; totals: {prof.Totals()}; realized rows {Count<DataGridRow>(grid)}, cells {Count<DataGridCell>(grid)}, grid h {grid.ActualHeight:F0}");
                    var rows = tab.SelectedResult!.RowCount;
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    var after = GC.GetTotalMemory(true);
                    GC.KeepAlive(tab.Results);
                    return (first, total, stall, after - before, rows);
                }

                await RunOnce(1000); // warm-up (JIT, type handlers, templates)
                var limited = await RunOnce(1000);
                var all = await RunOnce(0);
                output.WriteLine($"limit 1000: first rows painted {limited.FirstPaint:F0} ms, done {limited.Total:F0} ms, max UI stall {limited.Stall:F0} ms, {limited.Rows:N0} rows");
                output.WriteLine($"no limit:   first rows painted {all.FirstPaint:F0} ms, done {all.Total:F0} ms, max UI stall {all.Stall:F0} ms, " +
                                 $"{all.Rows:N0} rows, managed memory +{all.Bytes / 1024.0 / 1024:F1} MB ({all.Bytes / Math.Max(1, all.Rows)} B/row), stalls>50ms: {probe.Over(MaxStallMs)}");

                Assert.Equal(1000, limited.Rows);
                Assert.Equal(200_000, all.Rows);
                Assert.True(all.Bytes / all.Rows < 600, $"{all.Bytes / all.Rows} bytes per 5-column row");

                // UI timings are asserted only without a UI Automation client (screen reader, Power Automate, remote
                // tooling…): with one, WPF raises cross-process automation events for every visual and the first
                // layout of a grid alone takes hundreds of ms (measured on the shared dev box), whatever the app does.
                if (UiaListening)
                {
                    output.WriteLine("A UI Automation client is listening: UI timings reported, not asserted.");
                    return;
                }
                Assert.True(limited.FirstPaint < 250, $"first rows (limit 1000) painted after {limited.FirstPaint:F0} ms");
                Assert.True(all.FirstPaint < 250, $"first rows (no limit) painted after {all.FirstPaint:F0} ms");
                Assert.True(limited.Stall < MaxStallMs, $"UI thread blocked {limited.Stall:F0} ms (limit 1000)");
                Assert.True(all.Stall < MaxStallMs, $"UI thread blocked {all.Stall:F0} ms while streaming 200k rows");
            }
            finally
            {
                window.Close();
                await ws.CloseAsync();
            }
        });
    }
}
