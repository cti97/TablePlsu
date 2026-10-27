using System.Collections.Specialized;
using TablePlsu.App.Features.Sidebar;
using TablePlsu.App.Features.Workspace;
using TablePlsu.App.Tests.Perf;
using TablePlsu.Core.Connections;
using TablePlsu.Core.History;
using TablePlsu.Core.Metadata;

namespace TablePlsu.App.Tests;

/// <summary>Debounced, cancellable, off-UI-thread sidebar and History search over a large index (no database needed).</summary>
[Collection(AppServicesCollection.Name)]
public class SidebarSearchTests
{
    private static readonly string[] Prefixes = ["orders", "customers", "invoices", "payments", "products", "stock", "audit", "events"];

    private static DbMetadata Big(int tables = 2000) => new()
    {
        Database = "db",
        SearchPath = ["public"],
        Schemas =
        [
            new SchemaInfo
            {
                Name = "public",
                IsLoaded = true,
                Tables = [.. Enumerable.Range(0, tables).Select(i => new TableInfo { Schema = "public", Name = Prefixes[i % 8] + "_" + i, Kind = RelationKind.Table })],
            },
        ],
    };

    private static async Task WaitIdle(SidebarViewModel vm)
    {
        for (var i = 0; i < 500 && vm.IsSearchPending; i++) await Task.Delay(10);
        Assert.False(vm.IsSearchPending);
        await PerfHarness.Yield();
    }

    [Fact]
    public void Typing_burst_on_a_large_index_filters_once_off_the_keystroke_and_clearing_restores_selection()
    {
        PerfHarness.OnUiThread(async () =>
        {
            TestApp.Init(new WorkspaceFeature(), new FakeTabsFeature(true), new SidebarFeature());
            var ws = new WorkspaceViewModel(TestApp.Profile(), ConnectionSecrets.None, null, shell: null);
            ws.Metadata.Save(Big());
            await ws.Metadata.LoadCachedAsync();
            var vm = SidebarFeature.For(ws);
            vm.Rebuild();
            // Stand-in for the view: re-select the handed-back keys after every rebuild.
            vm.NodesRebuilt += keys => vm.SelectedNodes = [.. vm.Nodes.Where(n => keys.Contains(n.Key))];
            var full = vm.Nodes.Count;
            Assert.True(full > 2000);
            vm.SelectedNodes = [vm.Nodes.Single(n => n.Key == "public/Tables/stock_5")];
            var resets = 0;
            vm.Nodes.CollectionChanged += (_, e) => { if (e.Action == NotifyCollectionChangedAction.Reset) resets++; };

            var typed = "";
            foreach (var c in "customers")
            {
                typed += c;
                vm.SearchText = typed;
                Assert.Equal(full, vm.Nodes.Count); // the keystroke itself does not touch the list
                Assert.True(vm.IsSearchPending);
            }
            await WaitIdle(vm);
            Assert.Equal(1, resets);
            var expected = SidebarTree.Build(ws.Metadata.Current, new SidebarTreeOptions { Filter = "customers", Expanded = vm.ExpandedKeys });
            Assert.Equal(expected.Select(n => n.Key), vm.Nodes.Select(n => n.Key));
            Assert.Equal(250, vm.Nodes.Count(n => n.IsItem));
            Assert.Empty(vm.SelectedNodes); // stock_5 is not a result

            // A stale search is cancelled: only the newest query lands.
            vm.SearchText = "^orders_1";
            vm.SearchText = "^invoices_2";
            await WaitIdle(vm);
            Assert.All(vm.Nodes.Where(n => n.IsItem), n => Assert.StartsWith("invoices_2", n.Text));

            // Same result for a longer query: no list update at all.
            resets = 0;
            vm.SearchText = "^invoices_2 ";
            await WaitIdle(vm);
            Assert.Equal(0, resets);

            // Clearing is immediate, restores the full tree with its expansion and the selection from before the search.
            vm.SearchText = "";
            Assert.False(vm.IsSearchPending);
            Assert.Equal(full, vm.Nodes.Count);
            Assert.Equal("public/Tables/stock_5", Assert.Single(vm.SelectedNodes).Key);
            await ws.CloseAsync();
        });
    }

    [Fact]
    public void Small_index_still_filters_synchronously()
    {
        PerfHarness.OnUiThread(async () =>
        {
            TestApp.Init(new WorkspaceFeature(), new FakeTabsFeature(true), new SidebarFeature());
            var ws = new WorkspaceViewModel(TestApp.Profile(), ConnectionSecrets.None, null, shell: null);
            ws.Metadata.Save(Big(50));
            await ws.Metadata.LoadCachedAsync();
            var vm = SidebarFeature.For(ws);
            vm.Rebuild();
            vm.SearchText = "^stock_";
            Assert.False(vm.IsSearchPending);
            Assert.All(vm.Nodes.Where(n => n.IsItem), n => Assert.StartsWith("stock_", n.Text));
            await ws.CloseAsync();
        });
    }

    [Fact]
    public void History_search_on_a_large_history_is_debounced_into_one_update()
    {
        PerfHarness.OnUiThread(async () =>
        {
            TestApp.Init(new WorkspaceFeature(), new FakeTabsFeature(true), new SidebarFeature());
            var ws = new WorkspaceViewModel(TestApp.Profile(), ConnectionSecrets.None, null, shell: null);
            var vm = SidebarFeature.For(ws);
            ws.History.Clear(ws.Profile.Id);
            for (var i = 0; i < 2000; i++) ws.History.Append(ws.Profile.Id, new HistoryEntry { Sql = $"select * from {Prefixes[i % 8]}_{i}", Database = "db" });
            await vm.LoadHistoryAsync();
            Assert.Equal(2000, vm.HistoryItems.Count);
            var resets = 0;
            vm.HistoryItems.CollectionChanged += (_, e) => { if (e.Action == NotifyCollectionChangedAction.Reset) resets++; };
            var typed = "";
            foreach (var c in "customers")
            {
                typed += c;
                vm.HistorySearchText = typed;
                Assert.Equal(2000, vm.HistoryItems.Count);
            }
            await WaitIdle(vm);
            Assert.Equal(1, resets);
            Assert.Equal(250, vm.HistoryItems.Count);
            vm.HistorySearchText = "";
            Assert.Equal(2000, vm.HistoryItems.Count);
            await ws.CloseAsync();
        });
    }
}
