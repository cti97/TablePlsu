using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Npgsql;
using TablePlsu.App.Features.Palette;
using TablePlsu.App.Features.Sidebar;
using TablePlsu.App.Features.Workspace;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Db;
using TablePlsu.Core.History;
using Xunit.Abstractions;

namespace TablePlsu.App.Tests.Perf;

/// <summary>
/// Sidebar search hot path on a 2000-table scratch schema (created by the test, dropped after; set
/// <c>TABLEPLSU_PERF_SCHEMA</c> to reuse a kept schema across runs, <c>TABLEPLSU_PERF_DROP=1</c> drops it at the end).
/// Types <c>customers</c> char by char into the real sidebar search box (and the History search box over 10,000
/// entries, and the Ctrl+P palette), then reports load-independent proxies: UI-thread CPU, list rebuilds (collection
/// Resets / change events), dispatcher operations, layout passes, UI-thread allocations, realized rows — plus the
/// longest single UI-thread operation (wall, noisy under load). Lines starting with <c>PERF</c> are parsed by
/// <c>scripts/perf-sidebarsearch.ps1</c>; <c>TABLEPLSU_PERF_OUT</c> appends them to a file.
/// </summary>
[Collection(AppServicesCollection.Name)]
[Trait("Category", "Perf")]
public class SidebarSearchPerfTests(ITestOutputHelper output)
{
    private const int TableCount = 2000;
    private const string Query = "customers";

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

    private static double UiCpuMs()
    {
        GetThreadTimes(GetCurrentThread(), out _, out _, out var k, out var u);
        return (k + u) / 10_000.0;
    }

    private static async Task ExecAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(TestPg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 300 };
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<bool> SchemaExistsAsync(string schema)
    {
        await using var conn = new NpgsqlConnection(TestPg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select count(*) from pg_tables where schemaname = @s", conn);
        cmd.Parameters.AddWithValue("s", schema);
        return (long)(await cmd.ExecuteScalarAsync())! >= TableCount;
    }

    private static async Task CreateSchemaAsync(string schema)
    {
        await ExecAsync($"create schema if not exists {schema}");
        for (var start = 0; start < TableCount; start += 250)
            await ExecAsync($"do $$ begin for i in {start}..{start + 249} loop execute format('create table if not exists {schema}.%I(id int primary key, qty int)', " +
                            $"(array['orders','customers','invoices','payments','products','stock','audit','events'])[i % 8 + 1] || '_' || i); end loop; end $$");
    }

    private static async Task DropSchemaAsync(string schema)
    {
        for (var i = 0; i < 10; i++)
            await ExecAsync($"do $$ declare r record; begin for r in select tablename from pg_tables where schemaname = '{schema}' limit 250 loop " +
                            $"execute format('drop table {schema}.%I', r.tablename); end loop; end $$");
        await ExecAsync($"drop schema if exists {schema} cascade");
    }

    /// <summary>Counts what happens on the UI thread while active.</summary>
    private sealed class Meter : IDisposable
    {
        private readonly Dispatcher _d = Dispatcher.CurrentDispatcher;
        private readonly Dictionary<DispatcherOperation, long> _started = [];
        private double _cpu0;
        private long _alloc0;
        public int Ops, Resets, Changes, Layouts;
        public double MaxOpMs, CpuMs;
        public long AllocBytes;
        public readonly Dictionary<string, (double Ms, int N)> Totals = [];

        /// <summary>The dispatcher operation kinds with the most UI-thread time (diagnostics).</summary>
        public string Top(int n = 5) => string.Join("; ", Totals.OrderByDescending(t => t.Value.Ms).Take(n).Select(t => $"{t.Key} x{t.Value.N} {t.Value.Ms:F0}ms"));

        public Meter()
        {
            _d.Hooks.OperationStarted += OnStarted;
            _d.Hooks.OperationCompleted += OnCompleted;
        }

        private void OnStarted(object? s, DispatcherHookEventArgs e) => _started[e.Operation] = Stopwatch.GetTimestamp();

        private void OnCompleted(object? s, DispatcherHookEventArgs e)
        {
            if (!_started.Remove(e.Operation, out var t0)) return;
            Ops++;
            var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            MaxOpMs = Math.Max(MaxOpMs, ms);
            var d = e.Operation.GetType().GetField("_method", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(e.Operation) as Delegate;
            var what = $"{e.Operation.Priority} {d?.Method.DeclaringType?.Name}.{d?.Method.Name}";
            Totals[what] = (Totals.GetValueOrDefault(what).Ms + ms, Totals.GetValueOrDefault(what).N + 1);
        }

        public void OnCollection(object? s, NotifyCollectionChangedEventArgs e)
        {
            Changes++;
            if (e.Action == NotifyCollectionChangedAction.Reset) Resets++;
        }

        public void OnLayout(object? s, EventArgs e) => Layouts++;

        public void Start()
        {
            Ops = Resets = Changes = Layouts = 0;
            Totals.Clear();
            MaxOpMs = 0;
            _cpu0 = UiCpuMs();
            _alloc0 = GC.GetAllocatedBytesForCurrentThread();
        }

        public void Stop()
        {
            CpuMs = UiCpuMs() - _cpu0;
            AllocBytes = GC.GetAllocatedBytesForCurrentThread() - _alloc0;
        }

        public void Dispose()
        {
            _d.Hooks.OperationStarted -= OnStarted;
            _d.Hooks.OperationCompleted -= OnCompleted;
        }
    }

    private sealed record Sample(string Name, double CpuMs, int Resets, int Changes, int Ops, int Layouts, double AllocKb, double MaxOpMs, double WallMs, int Realized)
    {
        public string Top { get; init; } = "";
    }

    private static double Median(IEnumerable<double> xs)
    {
        var l = xs.OrderBy(x => x).ToList();
        return l[l.Count / 2];
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 20_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("condition never held");
            await Task.Delay(10);
        }
    }

    /// <summary>Types <see cref="Query"/> into <paramref name="box"/> one char per <paramref name="intervalMs"/>, waits for <paramref name="settled"/>.</summary>
    private static async Task<Sample> TypeBurst(string name, Meter m, TextBox box, ItemsControl list, Func<bool> settled, int intervalMs)
    {
        box.Clear();
        await Task.Delay(400);
        await PerfHarness.Yield();
        var wall = Stopwatch.StartNew();
        m.Start();
        foreach (var c in Query)
        {
            box.AppendText(c.ToString());
            box.CaretIndex = box.Text.Length;
            await Task.Delay(intervalMs);
        }
        await WaitUntil(settled);
        await PerfHarness.Yield();
        var wallMs = wall.Elapsed.TotalMilliseconds;
        m.Stop();
        return new Sample(name, m.CpuMs, m.Resets, m.Changes, m.Ops, m.Layouts, m.AllocBytes / 1024.0, m.MaxOpMs, wallMs, QueryPerfTests.Count<ListBoxItem>(list)) { Top = m.Top() };
    }

    [Fact]
    public void Typing_customers_into_sidebar_history_and_palette_search_on_2000_tables()
    {
        if (!TestPg.Available(output)) return;
        var kept = Environment.GetEnvironmentVariable("TABLEPLSU_PERF_SCHEMA");
        var schema = string.IsNullOrWhiteSpace(kept) ? "perf_" + Guid.NewGuid().ToString("N")[..10] : kept;
        var rounds = int.TryParse(Environment.GetEnvironmentVariable("TABLEPLSU_PERF_ROUNDS"), out var r) ? r : 3;
        if (string.IsNullOrWhiteSpace(kept) || !SchemaExistsAsync(schema).GetAwaiter().GetResult()) CreateSchemaAsync(schema).GetAwaiter().GetResult();
        var samples = new List<Sample>();
        var selectionKept = 0;
        try
        {
            PerfHarness.OnUiThread(async () =>
            {
                TestApp.Init(new WorkspaceFeature(), new FakeTabsFeature(true), new SidebarFeature(), new PaletteFeature());
                var (profile, secrets) = TestPg.Profile();
                var ws = new WorkspaceViewModel(profile, secrets, null, shell: null);
                await ws.ConnectAsync();
                for (var i = 0; i < 100 && ws.State != DbSessionState.Connected; i++) await Task.Delay(50);
                await ws.Metadata.RefreshAsync();
                foreach (var s in ws.Metadata.Current.Schemas.Select(s => s.Name).ToList()) await ws.Metadata.EnsureSchemaLoadedAsync(s);
                try
                {
                    var vm = SidebarFeature.For(ws);
                    vm.RevealSchema(schema);
                    var view = (SidebarView)AppServices.Features.Panels.Create(WorkspacePanel.Sidebar, ws)!;
                    var window = PerfHarness.Host(view, 320, 800);
                    window.UpdateLayout();
                    var list = (ListBox)view.FindName("ItemsList");
                    var search = (TextBox)view.FindName("ItemsSearch");
                    using var m = new Meter();
                    vm.Nodes.CollectionChanged += m.OnCollection;
                    list.LayoutUpdated += m.OnLayout;

                    // Selection made before searching survives the search (cleared query restores it).
                    var picked = vm.Nodes.First(n => n.Key == schema + "/Tables/stock_5");
                    list.SelectedItem = picked;
                    await PerfHarness.Yield();

                    var expected = SidebarTree.Build(ws.Metadata.Current, new SidebarTreeOptions
                    {
                        Filter = Query, Matching = vm.Matching, VisibleSchemas = vm.VisibleSchemas, Expanded = vm.ExpandedKeys,
                    }).Select(n => n.Key).ToList();
                    bool Filtered() => vm.Nodes.Count == expected.Count && vm.Nodes.Select(n => n.Key).SequenceEqual(expected);
                    var fullCount = vm.Nodes.Count;
                    bool Cleared() => vm.Nodes.Count == fullCount;

                    async Task<Sample> Clear(string name)
                    {
                        var wall = Stopwatch.StartNew();
                        m.Start();
                        search.Clear();
                        await WaitUntil(Cleared);
                        await PerfHarness.Yield();
                        var wallMs = wall.Elapsed.TotalMilliseconds;
                        m.Stop();
                        return new Sample(name, m.CpuMs, m.Resets, m.Changes, m.Ops, m.Layouts, m.AllocBytes / 1024.0, m.MaxOpMs, wallMs, QueryPerfTests.Count<ListBoxItem>(list)) { Top = m.Top() };
                    }

                    // Warm-up (JIT, lazy loads) — not recorded.
                    await TypeBurst("warmup", m, search, list, Filtered, 60);
                    await Clear("warmup");
                    list.SelectedItem = vm.Nodes.First(n => n.Key == picked.Key);
                    await PerfHarness.Yield();

                    for (var round = 0; round < rounds; round++)
                    {
                        samples.Add(await TypeBurst("sidebar.fast60", m, search, list, Filtered, 60));
                        samples.Add(await Clear("sidebar.clear"));
                        if ((list.SelectedItem as SidebarNode)?.Key == picked.Key) selectionKept++;
                        samples.Add(await TypeBurst("sidebar.slow150", m, search, list, Filtered, 150));
                        samples.Add(await Clear("sidebar.clear"));
                    }
                    vm.Nodes.CollectionChanged -= m.OnCollection;
                    list.LayoutUpdated -= m.OnLayout;

                    // ---- History: 10,000 entries ----------------------------------------------------------------
                    ws.History.Clear(ws.Profile.Id);
                    var tables = new[] { "orders", "customers", "invoices", "payments" };
                    for (var i = 0; i < 10_000; i++)
                        ws.History.Append(ws.Profile.Id, new HistoryEntry { Sql = $"select * from {tables[i % 4]}_{i} where id = {i}\norder by 1", Database = "db" });
                    vm.SelectedTabIndex = SidebarViewModel.HistoryTab;
                    await WaitUntil(() => vm.HistoryItems.Count == 10_000);
                    await PerfHarness.Yield();
                    var hlist = (ListBox)view.FindName("HistoryList");
                    var hsearch = (TextBox)view.FindName("HistorySearch");
                    vm.HistoryItems.CollectionChanged += m.OnCollection;
                    hlist.LayoutUpdated += m.OnLayout;
                    bool HFiltered() => vm.HistoryItems.Count == 2_500;
                    await TypeBurst("warmup", m, hsearch, hlist, HFiltered, 60);
                    for (var round = 0; round < rounds; round++)
                    {
                        samples.Add(await TypeBurst("history.fast60", m, hsearch, hlist, HFiltered, 60));
                        samples.Add(await TypeBurst("history.slow150", m, hsearch, hlist, HFiltered, 150));
                    }
                    vm.HistoryItems.CollectionChanged -= m.OnCollection;
                    hlist.LayoutUpdated -= m.OnLayout;
                    window.Close();

                    // ---- Palette (Ctrl+P) -------------------------------------------------------------------------
                    var owner = PerfHarness.Host(new Grid(), 600, 400);
                    var palette = PaletteWindow.Open(PaletteMode.Anything, ws, owner);
                    await PerfHarness.Yield();
                    var plist = (ListBox)palette.FindName("List");
                    var pbox = (TextBox)palette.FindName("Input");
                    palette.ViewModel.Rows.CollectionChanged += m.OnCollection;
                    plist.LayoutUpdated += m.OnLayout;
                    bool PSettled() => palette.ViewModel.Text == Query && palette.ViewModel.Rows.Count > 0;
                    await TypeBurst("warmup", m, pbox, plist, PSettled, 60);
                    for (var round = 0; round < rounds; round++)
                        samples.Add(await TypeBurst("palette.fast60", m, pbox, plist, PSettled, 60));
                    palette.Close();
                    owner.Close();
                }
                finally
                {
                    await ws.CloseAsync();
                }
            }, timeoutSeconds: 600);
        }
        finally
        {
            if (string.IsNullOrWhiteSpace(kept) || Environment.GetEnvironmentVariable("TABLEPLSU_PERF_DROP") == "1") DropSchemaAsync(schema).GetAwaiter().GetResult();
        }

        var lines = new List<string> { $"PERF sidebarsearch selectionKeptAfterClear={selectionKept}/{rounds}" };
        output.WriteLine(lines[0]);
        foreach (var g in samples.GroupBy(s => s.Name))
        {
            var line = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"PERF sidebarsearch {g.Key} cpuMs={Median(g.Select(s => s.CpuMs)):F1} resets={Median(g.Select(s => (double)s.Resets)):F0} changes={Median(g.Select(s => (double)s.Changes)):F0} " +
                $"ops={Median(g.Select(s => (double)s.Ops)):F0} layouts={Median(g.Select(s => (double)s.Layouts)):F0} allocKB={Median(g.Select(s => s.AllocKb)):F0} " +
                $"maxOpMs={Median(g.Select(s => s.MaxOpMs)):F1} wallMs={Median(g.Select(s => s.WallMs)):F0} realized={Median(g.Select(s => (double)s.Realized)):F0} n={g.Count()} uia={QueryPerfTests.UiaListening}");
            lines.Add(line);
            output.WriteLine(line);
            output.WriteLine("  top ops (last run): " + g.Last().Top);
        }
        if (Environment.GetEnvironmentVariable("TABLEPLSU_PERF_OUT") is { Length: > 0 } outFile) File.AppendAllLines(outFile, lines);
    }
}
