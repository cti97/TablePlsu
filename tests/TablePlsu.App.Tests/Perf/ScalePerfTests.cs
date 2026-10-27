using System.Diagnostics;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using ICSharpCode.AvalonEdit;
using Npgsql;
using TablePlsu.App.Features.Completion;
using TablePlsu.App.Features.Palette;
using TablePlsu.App.Features.Sidebar;
using TablePlsu.App.Features.Workspace;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Db;
using Xunit.Abstractions;

namespace TablePlsu.App.Tests.Perf;

/// <summary>
/// A database with 2000 tables in one schema (a scratch schema created and dropped by the test): catalog load, sidebar
/// population and search, palette keystrokes, autocomplete keystrokes. Prints the numbers; asserts the model-side
/// budgets (the UI-side ones only when no UI Automation client is listening, which inflates WPF layout many times).
/// </summary>
[Collection(AppServicesCollection.Name)]
public class ScalePerfTests(ITestOutputHelper output)
{
    private const int TableCount = 2000;

    private static async Task ExecAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(TestPg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 300 };
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task CreateSchemaAsync(string schema)
    {
        await ExecAsync($"create schema {schema}");
        // Batches keep each transaction's lock count well under max_locks_per_transaction (shared test server).
        for (var start = 0; start < TableCount; start += 250)
            await ExecAsync($"do $$ begin for i in {start}..{start + 249} loop execute format('create table {schema}.%I(id int primary key, qty int, created_at timestamptz)', " +
                            $"(array['orders','customer','invoice','payment','product','stock','audit','event'])[i % 8 + 1] || '_' || i); end loop; end $$");
    }

    private static async Task DropSchemaAsync(string schema)
    {
        for (var i = 0; i < 10; i++)
            await ExecAsync($"do $$ declare r record; begin for r in select tablename from pg_tables where schemaname = '{schema}' limit 250 loop " +
                            $"execute format('drop table {schema}.%I', r.tablename); end loop; end $$");
        await ExecAsync($"drop schema if exists {schema} cascade");
    }

    private static bool UiaListening =>
        AutomationPeer.ListenerExists(AutomationEvents.StructureChanged) || AutomationPeer.ListenerExists(AutomationEvents.PropertyChanged);

    private static double Median(List<double> xs) { xs.Sort(); return xs[xs.Count / 2]; }

    [Fact]
    public void Two_thousand_tables_sidebar_palette_and_autocomplete()
    {
        if (!TestPg.Available(output)) return;
        var schema = "perf_" + Guid.NewGuid().ToString("N")[..10];
        var create = Stopwatch.StartNew();
        CreateSchemaAsync(schema).GetAwaiter().GetResult();
        output.WriteLine($"created {TableCount} tables in {create.ElapsedMilliseconds} ms");
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
                var uia = UiaListening;
                try
                {
                    // ---- catalog: one schema of 2000 tables -----------------------------------------------------
                    var sw = Stopwatch.StartNew();
                    var info = await ws.Metadata.EnsureSchemaLoadedAsync(schema);
                    var loadMs = sw.Elapsed.TotalMilliseconds;
                    Assert.Equal(TableCount, info!.Tables.Count);

                    // ---- sidebar: build the tree with the schema expanded, render, search -------------------------
                    var vm = SidebarFeature.For(ws);
                    sw.Restart();
                    vm.RevealSchema(schema);
                    var buildMs = sw.Elapsed.TotalMilliseconds;
                    Assert.True(vm.Nodes.Count > TableCount, $"nodes {vm.Nodes.Count}");
                    sw.Restart();
                    var view = (SidebarView)AppServices.Features.Panels.Create(WorkspacePanel.Sidebar, ws)!;
                    var window = PerfHarness.Host(view, 320, 800);
                    window.UpdateLayout();
                    var list = (ListBox)view.FindName("ItemsList");
                    var renderMs = sw.Elapsed.TotalMilliseconds;
                    Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(0));
                    output.WriteLine($"sidebar: schema catalog load {loadMs:F0} ms, tree build {buildMs:F1} ms ({vm.Nodes.Count:N0} rows), first view + layout {renderMs:F0} ms, realized rows {QueryPerfTests.Count<ListBoxItem>(list)}");

                    var searchTimes = new List<double>();
                    var searchModel = new List<double>();
                    foreach (var text in new[] { "p", "pa", "pay", "paym", "payme", "payment_1", "payment_19", "" })
                    {
                        sw.Restart();
                        vm.SearchText = text;
                        searchModel.Add(sw.Elapsed.TotalMilliseconds);
                        window.UpdateLayout();
                        searchTimes.Add(sw.Elapsed.TotalMilliseconds);
                    }
                    var treeTimes = new List<double>();
                    foreach (var text in new[] { "p", "pa", "pay", "paym", "payme", "payment_1", "payment_19" })
                    {
                        sw.Restart();
                        SidebarTree.Build(ws.Metadata.Current, new SidebarTreeOptions { Filter = text, Expanded = vm.ExpandedKeys });
                        treeTimes.Add(sw.Elapsed.TotalMilliseconds);
                    }
                    output.WriteLine($"sidebar search: tree filter median {Median(treeTimes):F1} ms");
                    output.WriteLine($"sidebar search keystroke: model median {Median(searchModel):F1} ms, with layout median {Median(searchTimes):F1} ms, max {searchTimes.Max():F1} ms");
                    window.Close();

                    // ---- palette ------------------------------------------------------------------------------------
                    sw.Restart();
                    using var palette = new PaletteViewModel(PaletteMode.Anything, ws, null);
                    var openMs = sw.Elapsed.TotalMilliseconds;
                    var paletteTimes = new List<double>();
                    var typed = "";
                    foreach (var c in "payment_19")
                    {
                        typed += c;
                        sw.Restart();
                        palette.Text = typed;
                        paletteTimes.Add(sw.Elapsed.TotalMilliseconds);
                    }
                    Assert.Contains(palette.Rows, r => r.Title == "payment_19");
                    output.WriteLine($"palette: open (gather + index of {palette.CandidateCount:N0}) {openMs:F1} ms; keystroke median {Median(paletteTimes):F2} ms, max {paletteTimes.Max():F2} ms");

                    // ---- autocomplete -------------------------------------------------------------------------------
                    var editor = new TextEditor { FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 13 };
                    var ewin = PerfHarness.Host(editor, 800, 500);
                    InputMethod.SetIsInputMethodEnabled(editor.TextArea, false);
                    using var completion = SqlCompletion.Attach(editor, () => ws.Metadata.Current);
                    editor.Text = "select * from ";
                    editor.TextArea.Caret.Offset = editor.Text.Length;
                    foreach (var c in schema + ".pa") { editor.TextArea.PerformTextInput(c.ToString()); completion.FlushPending(); }
                    completion.Close();
                    editor.Text = "select * from ";
                    editor.TextArea.Caret.Offset = editor.Text.Length;
                    await PerfHarness.Yield();

                    var keyTimes = new List<double>();
                    var refreshTimes = new List<double>();
                    double popupOpenMs = 0;
                    foreach (var c in schema + ".payment_1")
                    {
                        var wasOpen = completion.IsOpen;
                        sw.Restart();
                        editor.TextArea.PerformTextInput(c.ToString());
                        completion.FlushPending();
                        editor.UpdateLayout();
                        var t = sw.Elapsed.TotalMilliseconds;
                        if (!wasOpen && completion.IsOpen) popupOpenMs = t;
                        keyTimes.Add(t);
                        refreshTimes.Add(completion.LastRefreshMs);
                        await PerfHarness.Yield();
                    }
                    Assert.True(completion.IsOpen);
                    output.WriteLine($"autocomplete: popup open {popupOpenMs:F1} ms; keystroke (input + completion + layout) median {Median(keyTimes):F2} ms, max {keyTimes.Max():F2} ms; " +
                                     $"completion refresh median {Median(refreshTimes):F2} ms, max {refreshTimes.Max():F2} ms; UIA listening: {uia}");
                    ewin.Close();

                    Assert.True(loadMs < 2000, $"loading a schema of {TableCount} tables took {loadMs:F0} ms");
                    Assert.True(Median(treeTimes) < 16, $"sidebar tree filter {Median(treeTimes):F1} ms");
                    Assert.True(Median(paletteTimes) < 16, $"palette keystroke {Median(paletteTimes):F1} ms");
                    Assert.True(Median(refreshTimes) < 16, $"completion refresh {Median(refreshTimes):F1} ms");
                    if (!uia) Assert.True(Median(keyTimes) < 16, $"autocomplete keystroke {Median(keyTimes):F1} ms");
                }
                finally
                {
                    await ws.CloseAsync();
                }
            });
        }
        finally
        {
            DropSchemaAsync(schema).GetAwaiter().GetResult();
        }
    }
}
