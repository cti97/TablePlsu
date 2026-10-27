using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Threading;
using TablePlsu.App.Features.SqlEditor;
using TablePlsu.App.Features.Workspace;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Db;
using Xunit.Abstractions;

namespace TablePlsu.App.Tests.Perf;

/// <summary>
/// Streaming hot path: <c>select * from sales.orders</c> (seed: 200k rows), No limit, in the real SQL editor view.
/// Interleaves (same process, alternating) fetch-only runs (DbSession, no UI), the old per-batch UI path
/// (<see cref="ResultBatchPump.Coalesce"/> = false) and the coalesced path, and reports medians of wall time plus
/// load-independent proxies: collection notifications, dispatcher operations, UI-thread CPU and allocations.
/// Run: <c>scripts/perf-streaming.ps1</c> (or <c>dotnet test --filter Category=Perf</c> with TABLEPLSU_TEST_PG).
/// </summary>
[Collection(AppServicesCollection.Name)]
[Trait("Category", "Perf")]
public class StreamingPerfTests(ITestOutputHelper output)
{
    private const string Sql = "select * from sales.orders";

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

    private static double ThreadCpuMs()
    {
        GetThreadTimes(GetCurrentThread(), out _, out _, out var k, out var u);
        return (k + u) / 10_000.0;
    }

    private sealed record Sample(string Mode, double WallMs, double UiCpuMs, double UiAllocMb, int Notifications, int Resets, int DispatcherOps,
        int RowCountChanges, double MaxStallMs, int Rows, string SlowestOps);

    [Fact]
    public void Streaming_200k_rows_coalesced_vs_per_batch_vs_fetch_only()
    {
        if (!TestPg.Available(output)) return;
        var runs = int.TryParse(Environment.GetEnvironmentVariable("TABLEPLSU_PERF_RUNS"), out var n) ? n : 5;
        var intervalBefore = ResultBatchPump.Interval;
        if (int.TryParse(Environment.GetEnvironmentVariable("TABLEPLSU_PERF_INTERVAL_MS"), out var interval)) ResultBatchPump.Interval = TimeSpan.FromMilliseconds(interval);
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
            var dispatcherOps = 0;
            void CountOp(object? s, DispatcherHookEventArgs e) => dispatcherOps++;
            Dispatcher.CurrentDispatcher.Hooks.OperationCompleted += CountOp;
            var coalesceBefore = ResultBatchPump.Coalesce;
            try
            {
                await PerfHarness.Yield();
                var grid = view.ResultGrid.DataGrid;

                async Task<double> FetchOnly()
                {
                    var sw = Stopwatch.StartNew();
                    var r = await Task.Run(() => ws.Session.ExecuteAsync(Sql, new ExecuteOptions { RowLimit = null }));
                    var ms = sw.Elapsed.TotalMilliseconds;
                    Assert.Equal(200_000, r.Statements[0].Rows.Count);
                    return ms;
                }

                async Task<Sample> Display(bool coalesce)
                {
                    ResultBatchPump.Coalesce = coalesce;
                    tab.Results.Clear();
                    tab.SelectedResult = null;
                    await PerfHarness.Yield();
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    tab.RowLimit = 0;
                    tab.Document.Text = Sql;
                    tab.SetSelection(caret: 3);

                    int notifications = 0, resets = 0, rowCountChanges = 0;
                    void OnRows(object? s, NotifyCollectionChangedEventArgs e) { notifications++; if (e.Action == NotifyCollectionChangedAction.Reset) resets++; }
                    void OnProp(object? s, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(ResultTabViewModel.RowCount)) rowCountChanges++; }
                    void OnResults(object? s, NotifyCollectionChangedEventArgs e)
                    {
                        foreach (ResultTabViewModel r in e.NewItems ?? Array.Empty<ResultTabViewModel>())
                        {
                            r.Rows.CollectionChanged += OnRows;
                            r.PropertyChanged += OnProp;
                        }
                    }
                    tab.Results.CollectionChanged += OnResults;

                    probe.Reset();
                    prof.Reset();
                    dispatcherOps = 0;
                    var cpu0 = ThreadCpuMs();
                    var alloc0 = GC.GetAllocatedBytesForCurrentThread();
                    var clock = Stopwatch.StartNew();
                    await tab.RunCurrentAsync();
                    // All rows displayed = the first render after the last row is in the grid's items.
                    await PerfHarness.WhenRendered(() => grid.Items.Count == 200_000, clock, TimeSpan.FromSeconds(120));
                    var wall = clock.Elapsed.TotalMilliseconds;
                    var cpu = ThreadCpuMs() - cpu0;
                    var alloc = (GC.GetAllocatedBytesForCurrentThread() - alloc0) / 1048576.0;
                    var ops = dispatcherOps;
                    tab.Results.CollectionChanged -= OnResults;
                    var rows = tab.SelectedResult!.RowCount;
                    Assert.True(grid.ItemContainerGenerator.ContainerFromIndex(0) is DataGridRow, "first row realized");
                    return new Sample(coalesce ? "coalesced" : "per-batch", wall, cpu, alloc, notifications, resets, ops, rowCountChanges, probe.MaxStallMs, rows, prof.Top(3));
                }

                // Warm-up (JIT, type handlers, templates) for every path.
                await FetchOnly();
                await Display(false);
                await Display(true);

                // Optional CPU profile of the measured runs: TABLEPLSU_PERF_TRACE=<file.nettrace> (needs the dotnet-trace global tool).
                System.Diagnostics.Process? trace = null;
                if (Environment.GetEnvironmentVariable("TABLEPLSU_PERF_TRACE") is { Length: > 0 } traceFile)
                {
                    trace = System.Diagnostics.Process.Start(new ProcessStartInfo("dotnet-trace",
                        $"collect -p {Environment.ProcessId} --profile dotnet-sampled-thread-time --format Speedscope --duration 00:00:00:20 -o \"{traceFile}\"")
                        { UseShellExecute = false, CreateNoWindow = true });
                    await Task.Delay(3000);
                }

                var fetch = new List<double>();
                var samples = new List<Sample>();
                for (var i = 0; i < runs; i++)
                {
                    fetch.Add(await FetchOnly());
                    // Alternate the order so drift on a shared machine hits both modes equally.
                    foreach (var mode in i % 2 == 0 ? new[] { false, true } : [true, false])
                    {
                        var s = await Display(mode);
                        samples.Add(s);
                        output.WriteLine($"run {i} {s}");
                    }
                }

                if (trace is not null) await trace.WaitForExitAsync();

                static double Median(IEnumerable<double> values)
                {
                    var v = values.Order().ToList();
                    return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
                }

                var fetchMs = Median(fetch);
                output.WriteLine($"UIA client listening: {QueryPerfTests.UiaListening}; runs: {runs}; flush interval {ResultBatchPump.Interval.TotalMilliseconds} ms");
                output.WriteLine($"fetch only (DbSession, no UI): median {fetchMs:F0} ms  [{string.Join(", ", fetch.Select(f => f.ToString("F0")))}]");
                foreach (var g in samples.GroupBy(s => s.Mode))
                {
                    var wall = Median(g.Select(s => s.WallMs));
                    output.WriteLine($"{g.Key,-10}: all rows displayed median {wall:F0} ms ({wall / fetchMs:F2}x fetch), UI CPU {Median(g.Select(s => s.UiCpuMs)):F0} ms, " +
                                     $"UI alloc {Median(g.Select(s => s.UiAllocMb)):F0} MB, collection notifications {Median(g.Select(s => (double)s.Notifications)):F0} " +
                                     $"(resets {g.Max(s => s.Resets)}), dispatcher ops {Median(g.Select(s => (double)s.DispatcherOps)):F0}, " +
                                     $"RowCount changes {Median(g.Select(s => (double)s.RowCountChanges)):F0}, max UI stall median {Median(g.Select(s => s.MaxStallMs)):F0} ms " +
                                     $"(worst {g.Max(s => s.MaxStallMs):F0})");
                }
                Assert.All(samples, s => Assert.Equal(200_000, s.Rows));
            }
            finally
            {
                ResultBatchPump.Coalesce = coalesceBefore;
                ResultBatchPump.Interval = intervalBefore;
                Dispatcher.CurrentDispatcher.Hooks.OperationCompleted -= CountOp;
                window.Close();
                await ws.CloseAsync();
            }
        }, timeoutSeconds: 900);
    }
}
