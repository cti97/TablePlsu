using System.Diagnostics;
using TablePlsu.App.Features.Palette;
using TablePlsu.App.Features.Sidebar;
using TablePlsu.App.Features.Workspace;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Commands;
using TablePlsu.Core.Connections;
using Xunit.Abstractions;

namespace TablePlsu.App.Tests;

public class PaletteIndexTests(ITestOutputHelper output)
{
    private static PaletteItem Item(string title, string kind = PaletteKinds.Table, string? search = null, int priority = 0) =>
        new(title, kind, _ => { }) { SearchText = search, Priority = priority };

    [Fact]
    public void Anchors_and_fuzzy()
    {
        var index = new PaletteIndex(
        [
            Item("orders", search: "sales.orders"),
            Item("order_items", search: "sales.order_items"),
            Item("customers", search: "public.customers"),
            Item("old_orders", search: "archive.old_orders"),
        ]);
        Assert.Equal(["orders", "order_items"], index.Search("^ord").Select(h => h.Item.Title));
        Assert.Equal(["order_items"], index.Search("_items$").Select(h => h.Item.Title));
        Assert.Equal(["orders"], index.Search("^orders$").Select(h => h.Item.Title));
        Assert.Equal("orders", index.Search("ordrs").First().Item.Title);
        // Qualified search uses SearchText.
        var hit = index.Search("arch.old").First();
        Assert.Equal("old_orders", hit.Item.Title);
        Assert.False(hit.MatchedTitle);
        Assert.Empty(index.Search("zzz"));
    }

    [Fact]
    public void Allocation_free_scorer_equals_fuzzy_matcher()
    {
        var rnd = new Random(7);
        const string alphabet = "abcdeORD_.x-1 ";
        string Rand(int min, int max) => new([.. Enumerable.Range(0, rnd.Next(min, max)).Select(_ => alphabet[rnd.Next(alphabet.Length)])]);
        var index = new PaletteIndex([]);
        string[] fixedPatterns = ["o", "or", "ORD", "^ab", "cd$", "^abc$", "a.b", "x x"];
        for (var i = 0; i < 4000; i++)
        {
            var candidate = Rand(1, 90);
            var pattern = i < fixedPatterns.Length * 100 ? fixedPatterns[i % fixedPatterns.Length] : Rand(1, 6);
            var expected = TablePlsu.Core.Search.FuzzyMatcher.Match(pattern, candidate)?.Score;
            Assert.True(expected == index.Score(pattern, candidate), $"'{pattern}' vs '{candidate}': {expected} != {index.Score(pattern, candidate)}");
        }
    }

    [Fact]
    public void Incremental_narrowing_gives_same_results_as_fresh_search()
    {
        var items = Enumerable.Range(0, 500).Select(i => Item($"t_{i}_order{i % 7}", search: $"s{i % 3}.t_{i}_order{i % 7}")).ToList();
        var incremental = new PaletteIndex(items);
        foreach (var q in new[] { "o", "or", "ord", "orde3" }) incremental.Search(q);
        var fresh = new PaletteIndex(items);
        Assert.Equal(fresh.Search("orde3", 500).Select(h => h.Item.Title), incremental.Search("orde3", 500).Select(h => h.Item.Title));
        incremental.Search("s1");
        Assert.Equal(new PaletteIndex(items).Search("s1.or", 500).Select(h => h.Item.Title), incremental.Search("s1.or", 500).Select(h => h.Item.Title));
    }

    [Fact]
    public void Empty_query_lists_commands_sorted()
    {
        var index = new PaletteIndex([Item("orders"), Item("Reload", PaletteKinds.Command), Item("New SQL Editor", PaletteKinds.Command)]);
        Assert.Equal(["New SQL Editor", "Reload"], index.Search("").Select(h => h.Item.Title));
        Assert.Equal(3, index.Search("", emptyListsCommands: false).Count);
    }

    [Fact]
    public void Ties_prefer_priority()
    {
        var index = new PaletteIndex([Item("orders", PaletteKinds.Database, priority: 1), Item("orders", PaletteKinds.Table, priority: 3)]);
        Assert.Equal(PaletteKinds.Table, index.Search("orders")[0].Item.Kind);
    }

    [Fact]
    public void Keystroke_search_over_20k_candidates_is_fast()
    {
        var rnd = new Random(42);
        string[] words = ["order", "customer", "item", "invoice", "payment", "product", "stock", "audit", "log", "user", "account", "event"];
        var items = Enumerable.Range(0, 20_000).Select(i =>
        {
            var name = $"{words[rnd.Next(words.Length)]}_{words[rnd.Next(words.Length)]}_{i}";
            return Item(name, search: $"schema_{i % 40}.{name}");
        }).ToList();
        var index = new PaletteIndex(items);
        index.Search("ord"); // warm-up (JIT)
        var times = new List<double>();
        foreach (var q in new[] { "o", "or", "ord", "ord_", "ord_it", "ord_ite", "^cust", "_99$", "s", "sc", "sch.", "sch.pay" })
        {
            // Best of 3 fresh indexes (no incremental narrowing): the box is shared with other builds.
            var best = double.MaxValue;
            for (var rep = 0; rep < 3; rep++)
            {
                var fresh = new PaletteIndex(items);
                var sw = Stopwatch.StartNew();
                var hits = fresh.Search(q);
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
                Assert.NotEmpty(hits);
            }
            times.Add(best);
        }
        output.WriteLine("per-keystroke ms: " + string.Join(", ", times.Select(t => t.ToString("0.0"))));
        times.Sort();
        Assert.True(times[times.Count / 2] < 60, $"median {times[times.Count / 2]:0.0} ms"); // target < 16 ms; loose bound for loaded CI boxes
    }
}

[Collection(AppServicesCollection.Name)]
public class PaletteViewModelTests(ITestOutputHelper output)
{
    [Fact]
    public void Navigation_wraps_tab_completes_escape_clears_then_closes()
    {
        var invoked = new List<PaletteInvocation>();
        PaletteItem[] items =
        [
            new("orders", PaletteKinds.Table, inv => invoked.Add(inv)),
            new("order_items", PaletteKinds.Table, inv => invoked.Add(inv)),
            new("Reload", PaletteKinds.Command, inv => invoked.Add(inv)) { Shortcut = "Ctrl+R" },
        ];
        var vm = new PaletteViewModel(PaletteMode.Anything, null, null, _ => items);
        Assert.Equal(["Reload"], vm.Rows.Select(r => r.Title)); // empty input → commands
        Assert.Equal("Ctrl+R", vm.Rows[0].Shortcut);

        vm.Text = "ord";
        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal(0, vm.SelectedIndex);
        vm.MoveSelection(1);
        Assert.Equal(1, vm.SelectedIndex);
        vm.MoveSelection(1);
        Assert.Equal(0, vm.SelectedIndex); // wraps
        vm.MoveSelection(-1);
        Assert.Equal(1, vm.SelectedIndex);
        Assert.True(vm.LastSearchTime < TimeSpan.FromSeconds(1));

        vm.Complete();
        Assert.Equal("order_items", vm.Text);

        var closed = 0;
        vm.CloseRequested += () => closed++;
        vm.Invoke(newTab: true);
        Assert.Equal(1, closed);
        Assert.True(Assert.Single(invoked).NewTab);

        Assert.False(vm.Escape());
        Assert.Equal("", vm.Text);
        Assert.True(vm.Escape());

        vm.Text = "nothing-matches";
        Assert.Equal("No matches", vm.StatusText);
        Assert.Null(vm.Invoke());
    }

    [Fact]
    public void Databases_and_connections_modes_list_everything_on_empty_input()
    {
        TestApp.Init(new WorkspaceFeature(), new FakeTabsFeature(true), new PaletteFeature());
        var ws = new WorkspaceViewModel(TestApp.Profile(), ConnectionSecrets.None, null, shell: null);
        var opened = new List<(string, bool)>();
        var items = PaletteItems.Databases(ws, ["db", "other", "postgres"]).ToList();
        var vm = new PaletteViewModel(PaletteMode.Databases, ws, null, _ => items);
        Assert.Equal(["db", "other", "postgres"], vm.Rows.Select(r => r.Title));
        Assert.Equal("Database · current", vm.Rows[0].Detail);
        vm.Text = "oth";
        Assert.Equal(["other"], vm.Rows.Select(r => r.Title));
        ws.CloseAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public void Anything_mode_gathers_objects_tabs_and_registered_commands()
    {
        TestApp.Init(new WorkspaceFeature(), new FakeTabsFeature(true), new SidebarFeature(), new PaletteFeature());
        var ws = new WorkspaceViewModel(TestApp.Profile(), ConnectionSecrets.None, null, shell: null);
        ws.Metadata.Save(SampleMetadata.Create());
        ws.Metadata.LoadCachedAsync().GetAwaiter().GetResult();
        ws.NewSqlEditor();

        var vm = new PaletteViewModel(PaletteMode.Anything, ws, null);
        output.WriteLine($"{vm.CandidateCount} candidates");
        Assert.Contains(vm.Rows, r => r.Title == "New SQL Editor" && r.Shortcut == "Ctrl+E");
        Assert.DoesNotContain(vm.Rows, r => r.Item.Kind != PaletteKinds.Command);
        Assert.DoesNotContain(vm.Rows, r => r.Title == "Open Anything");

        vm.Text = "^order_tot";
        var row = Assert.Single(vm.Rows);
        Assert.Equal(ObjectKinds.MaterializedView, row.Item.Kind);
        Assert.Equal("Materialized view · sales", row.Detail);
        vm.Invoke();
        Assert.Equal(TabKinds.MaterializedView, ws.ActiveTab!.Kind);
        Assert.Equal("order_totals", ws.ActiveTab.Name);

        // Ctrl+Enter → new tab.
        var vm2 = new PaletteViewModel(PaletteMode.Anything, ws, null) { Text = "^customers$" };
        var tabsBefore = ws.Tabs.Count;
        vm2.Invoke(newTab: true);
        Assert.Equal(tabsBefore + 1, ws.Tabs.Count);

        // Functions (overloads), schemas and SQL tabs are candidates too.
        var vm3 = new PaletteViewModel(PaletteMode.Anything, ws, null) { Text = "add_one" };
        Assert.Equal(2, vm3.Rows.Count(r => r.Item.Kind == ObjectKinds.Function));
        vm3.Text = "^sales$";
        Assert.Equal(PaletteKinds.Schema, vm3.Rows[0].Item.Kind);
        vm3.Invoke();
        Assert.Contains("sales", SidebarFeature.For(ws).ExpandedKeys);
        var vm4 = new PaletteViewModel(PaletteMode.Anything, ws, null) { Text = "Query" };
        Assert.Contains(vm4.Rows, r => r.Item.Kind == PaletteKinds.SqlTab);

        // A command runs through the workspace.
        var vm5 = new PaletteViewModel(PaletteMode.Anything, ws, null) { Text = "^Toggle Console$" };
        Assert.False(ws.IsConsoleVisible);
        vm5.Invoke();
        Assert.True(ws.IsConsoleVisible);
        ws.CloseAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public void Palette_commands_are_registered_with_default_shortcuts()
    {
        TestApp.Init(new WorkspaceFeature(), new PaletteFeature());
        Assert.True(AppServices.Commands.IsRegistered(CommandIds.PaletteOpenAnything));
        Assert.True(AppServices.Commands.IsRegistered(CommandIds.PaletteOpenDatabase));
        Assert.True(AppServices.Commands.IsRegistered(CommandIds.PaletteOpenConnection));
        Assert.Equal("Ctrl+P", ShortcutText.Gesture(CommandIds.PaletteOpenAnything));
        Assert.Equal("Ctrl+K", ShortcutText.Gesture(CommandIds.PaletteOpenDatabase));
        Assert.Equal("Ctrl+Shift+K", ShortcutText.Gesture(CommandIds.PaletteOpenConnection));
        AppServices.Connections.Save(new ConnectionProfile { Name = "prod db", Host = "h", Database = "d" });
        var items = PaletteItems.Connections(null).ToList();
        Assert.Equal("prod db", Assert.Single(items).Title);
    }

    [Fact]
    public async Task Database_list_comes_from_pg_database()
    {
        if (!TestPg.Available(output)) return;
        TestApp.Init(new WorkspaceFeature(), new PaletteFeature());
        var (profile, secrets) = TestPg.Profile();
        var ws = new WorkspaceViewModel(profile, secrets, null, null);
        try
        {
            await ws.ConnectAsync();
            var names = await DatabaseList.For(ws).RefreshAsync(ws.Session);
            Assert.Contains("tableplsu_test", names);
            Assert.DoesNotContain("template0", names);
            var vm = new PaletteViewModel(PaletteMode.Databases, ws, null) { Text = "^tableplsu_test$" };
            Assert.Equal("Database · current", Assert.Single(vm.Rows).Detail);
        }
        finally
        {
            await ws.CloseAsync();
        }
    }
}
