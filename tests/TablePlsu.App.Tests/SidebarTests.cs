using System.IO;
using TablePlsu.App.Features.Sidebar;
using TablePlsu.App.Features.Workspace;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Commands;
using TablePlsu.Core.Connections;
using TablePlsu.Core.History;
using TablePlsu.Core.Metadata;
using TablePlsu.Core.Paths;
using TablePlsu.Core.Settings;
using TablePlsu.Core.Sidebar;
using Xunit.Abstractions;

namespace TablePlsu.App.Tests;

internal static class SampleMetadata
{
    public static DbMetadata Create(bool salesLoaded = true) => new()
    {
        Database = "db",
        SearchPath = ["public"],
        Schemas =
        [
            new SchemaInfo
            {
                Name = "public",
                IsLoaded = true,
                Tables =
                [
                    new TableInfo { Schema = "public", Name = "customers", Kind = RelationKind.Table, Columns = [new ColumnInfo { Name = "id", Ordinal = 1, DataType = "integer" }], PrimaryKey = ["id"] },
                    new TableInfo { Schema = "public", Name = "active_customers", Kind = RelationKind.View },
                    new TableInfo { Schema = "public", Name = "no_pk", Kind = RelationKind.Table },
                ],
                Functions =
                [
                    new FunctionInfo { Schema = "public", Name = "add_one", Arguments = "x integer", ReturnType = "integer", Kind = RoutineKind.Function, Oid = 1 },
                    new FunctionInfo { Schema = "public", Name = "add_one", Arguments = "x bigint", ReturnType = "bigint", Kind = RoutineKind.Function, Oid = 2 },
                    new FunctionInfo { Schema = "public", Name = "do_it", Arguments = "", Kind = RoutineKind.Procedure, Oid = 3 },
                ],
                Sequences = [new SequenceInfo { Schema = "public", Name = "customers_id_seq", DataType = "integer", OwnedBy = "customers.id" }],
                Types = [new UserTypeInfo { Schema = "public", Name = "mood", Kind = UserTypeKind.Enum, EnumLabels = ["a", "b"] }],
            },
            new SchemaInfo
            {
                Name = "sales",
                IsLoaded = salesLoaded,
                Tables = salesLoaded
                    ? [
                        new TableInfo { Schema = "sales", Name = "orders", Kind = RelationKind.Table },
                        new TableInfo { Schema = "sales", Name = "order_totals", Kind = RelationKind.MaterializedView },
                    ]
                    : [],
            },
        ],
    };
}

public class SidebarTreeTests
{
    private static readonly HashSet<string> AllExpanded =
        ["public", "public/Tables", "public/Functions", "public/Sequences", "public/Types", "sales", "sales/Tables"];

    [Fact]
    public void Collapsed_schemas_show_only_schema_rows()
    {
        var nodes = SidebarTree.Build(SampleMetadata.Create(), new SidebarTreeOptions());
        Assert.Equal(["public", "sales"], nodes.Select(n => n.Key));
        Assert.All(nodes, n => Assert.Equal(SidebarNodeKind.Schema, n.Kind));
        Assert.All(nodes, n => Assert.False(n.IsExpanded));
    }

    [Fact]
    public void Expanded_tree_has_sections_items_icons_and_signatures()
    {
        var nodes = SidebarTree.Build(SampleMetadata.Create(), new SidebarTreeOptions { Expanded = AllExpanded });
        var keys = nodes.Select(n => n.Key).ToList();
        Assert.Equal(
        [
            "public", "public/Tables", "public/Tables/active_customers", "public/Tables/customers", "public/Tables/no_pk",
            "public/Functions", "public/Functions/add_one(x bigint)", "public/Functions/add_one(x integer)", "public/Functions/do_it()",
            "public/Sequences", "public/Sequences/customers_id_seq",
            "public/Types", "public/Types/mood",
            "sales", "sales/Tables", "sales/Tables/orders", "sales/Tables/order_totals",
        ], keys);
        var byKey = nodes.ToDictionary(n => n.Key);
        Assert.Equal(SidebarNode.IconView, byKey["public/Tables/active_customers"].Icon);
        Assert.Equal(SidebarNode.IconTable, byKey["public/Tables/customers"].Icon);
        Assert.Equal(SidebarNode.IconMatView, byKey["sales/Tables/order_totals"].Icon);
        Assert.Equal(ObjectKinds.MaterializedView, byKey["sales/Tables/order_totals"].ObjectKind);
        Assert.Equal("(x integer)", byKey["public/Functions/add_one(x integer)"].Detail);
        Assert.Equal(ObjectKinds.Procedure, byKey["public/Functions/do_it()"].ObjectKind);
        Assert.Equal("3", byKey["public/Tables"].Detail);
        Assert.Equal(2, byKey["public/Tables/customers"].Level);
        Assert.Equal(new SidebarItemRef("public", "add_one", ObjectKinds.Function) { Signature = "x bigint" }, byKey["public/Functions/add_one(x bigint)"].ToRef());
    }

    [Fact]
    public void Filter_expands_matches_and_hides_empty_schemas()
    {
        var nodes = SidebarTree.Build(SampleMetadata.Create(), new SidebarTreeOptions { Filter = "ordr" });
        Assert.Equal(["sales", "sales/Tables", "sales/Tables/orders", "sales/Tables/order_totals"], nodes.Select(n => n.Key));

        var anchored = SidebarTree.Build(SampleMetadata.Create(), new SidebarTreeOptions { Filter = "^cust" });
        Assert.Equal(["public", "public/Tables", "public/Tables/customers", "public/Sequences", "public/Sequences/customers_id_seq"], anchored.Select(n => n.Key));

        // "contains" strategy rejects subsequences.
        var contains = SidebarTree.Build(SampleMetadata.Create(), new SidebarTreeOptions { Filter = "ordr", Matching = SidebarMatching.Contains });
        Assert.Empty(contains);
        var contains2 = SidebarTree.Build(SampleMetadata.Create(), new SidebarTreeOptions { Filter = "TOTAL", Matching = SidebarMatching.Contains });
        Assert.Contains(contains2, n => n.Key == "sales/Tables/order_totals");
    }

    [Fact]
    public void Visible_schemas_and_lazy_placeholder()
    {
        var nodes = SidebarTree.Build(SampleMetadata.Create(salesLoaded: false), new SidebarTreeOptions
        {
            VisibleSchemas = new HashSet<string> { "sales" },
            Expanded = AllExpanded,
            NotLoadedText = "Loading…",
        });
        Assert.Equal(["sales", "sales/…"], nodes.Select(n => n.Key));
        Assert.Equal(SidebarNodeKind.Placeholder, nodes[1].Kind);
        Assert.Equal("Loading…", nodes[1].Text);
    }

    [Fact]
    public void Script_objects_resolve_kinds_from_metadata()
    {
        var md = SampleMetadata.Create();
        Assert.Equal(ScriptObjectKind.View, SidebarTree.ToScriptObject(md, new SidebarItemRef("public", "active_customers", ObjectKinds.Table))!.Kind);
        var fn = SidebarTree.ToScriptObject(md, new SidebarItemRef("public", "add_one", ObjectKinds.Function) { Signature = "x bigint" })!;
        Assert.Equal("public.add_one(x bigint)", fn.Target);
        Assert.Equal(2u, SidebarTree.FindFunction(md, new SidebarItemRef("public", "add_one", ObjectKinds.Function) { Signature = "x bigint" })!.Oid);
    }
}

[Collection(AppServicesCollection.Name)]
public class SidebarViewModelTests(ITestOutputHelper output)
{
    private static async Task<(WorkspaceViewModel Ws, SidebarViewModel Vm, string Root)> CreateAsync(DbMetadata? md = null)
    {
        var root = TestApp.Init(new WorkspaceFeature(), new FakeTabsFeature(withSqlEditor: true), new SidebarFeature());
        var ws = new WorkspaceViewModel(TestApp.Profile(), ConnectionSecrets.None, null, shell: null);
        ws.Metadata.Save(md ?? SampleMetadata.Create());
        await ws.Metadata.LoadCachedAsync();
        var vm = SidebarFeature.For(ws);
        vm.Rebuild();
        return (ws, vm, root);
    }

    [Fact]
    public async Task Initial_expansion_follows_search_path()
    {
        var (ws, vm, _) = await CreateAsync();
        Assert.Contains("public", vm.ExpandedKeys);
        Assert.Contains("public/Tables", vm.ExpandedKeys);
        Assert.Contains(vm.Nodes, n => n.Key == "public/Tables/customers");
        Assert.DoesNotContain(vm.Nodes, n => n.Key.StartsWith("sales/", StringComparison.Ordinal));
        Assert.Equal("All schemas", vm.SchemaSelectorText);
        await ws.CloseAsync();
    }

    [Fact]
    public async Task Rebuild_on_metadata_change_keeps_expansion_and_selection()
    {
        var (ws, vm, _) = await CreateAsync();
        var sales = vm.Nodes.Single(n => n.Key == "sales");
        vm.Toggle(sales);
        vm.Toggle(vm.Nodes.Single(n => n.Key == "sales/Tables"));
        vm.SelectedNodes = [vm.Nodes.Single(n => n.Key == "sales/Tables/orders")];
        IReadOnlyList<string>? restored = null;
        vm.NodesRebuilt += keys => restored = keys;

        // A refresh adds a table: expansion stays and the selection keys are handed back to the view.
        var md = SampleMetadata.Create();
        var salesSchema = md.FindSchema("sales")!;
        var updated = md.WithSchema(new SchemaInfo
        {
            Name = "sales", IsLoaded = true,
            Tables = [.. salesSchema.Tables, new TableInfo { Schema = "sales", Name = "refunds", Kind = RelationKind.Table }],
        });
        ws.Metadata.Save(updated);
        ws.Metadata.Clear();
        await ws.Metadata.LoadCachedAsync();
        ws.Metadata.Save(updated);
        await ws.Metadata.LoadCachedAsync();

        Assert.Contains(vm.Nodes, n => n.Key == "sales/Tables/refunds");
        Assert.Contains(vm.Nodes, n => n.Key == "public/Tables/customers");
        Assert.Equal(["sales/Tables/orders"], restored);
        await ws.CloseAsync();
    }

    [Fact]
    public async Task Schema_selection_is_remembered_per_connection()
    {
        var (ws, vm, root) = await CreateAsync();
        vm.SchemaChoices.Single(c => c.Name == "public").IsChecked = false;
        Assert.Equal("sales", vm.SchemaSelectorText);
        Assert.Equal(["sales"], vm.Nodes.Where(n => n.Kind == SidebarNodeKind.Schema).Select(n => n.Key));

        var store = new SidebarSchemaStore(AppPaths.ForRoot(root));
        Assert.Equal(["sales"], store.Load(ws.Profile.Id, ws.Database));
        var vm2 = new SidebarViewModel(ws, store);
        Assert.Equal(["sales"], vm2.VisibleSchemas!);
        Assert.False(vm2.SchemaChoices.Single(c => c.Name == "public").IsChecked);

        vm.ShowAllSchemas();
        Assert.Null(store.Load(ws.Profile.Id, ws.Database));
        vm2.Dispose();
        await ws.CloseAsync();
    }

    [Fact]
    public async Task Click_opens_preview_double_click_pins_and_function_requests_function_tab()
    {
        var (ws, vm, _) = await CreateAsync();
        vm.Activate(vm.Nodes.Single(n => n.Key == "public/Tables/customers"));
        Assert.Equal("customers", ws.ActiveTab!.Name);
        Assert.True(ws.ActiveTab.IsPreview);

        vm.Activate(vm.Nodes.Single(n => n.Key == "public/Tables/no_pk"));
        Assert.Single(ws.Tabs); // preview replaced
        Assert.Equal("no_pk", ws.ActiveTab!.Name);

        vm.Activate(vm.Nodes.Single(n => n.Key == "public/Tables/active_customers"), pinned: true);
        Assert.Equal(TabKinds.View, ws.ActiveTab!.Kind);
        Assert.False(ws.ActiveTab.IsPreview);

        vm.SetExpanded("public/Functions", true);
        vm.Activate(vm.Nodes.Single(n => n.Key == "public/Functions/add_one(x bigint)"));
        Assert.Equal(TabKinds.Function, ws.ActiveTab!.Kind);

        var request = SidebarActions.RequestFor(new SidebarItemRef("public", "add_one", ObjectKinds.Function) { Signature = "x bigint" }, false, false, ws.Metadata.Current)!;
        Assert.Equal("add_one(x bigint)", request.Title);
        Assert.Equal(2u, Assert.IsType<FunctionInfo>(request.Parameter).Oid);

        // Header click toggles instead of opening.
        var before = vm.Nodes.Count;
        vm.Activate(vm.Nodes.Single(n => n.Key == "public"));
        Assert.True(vm.Nodes.Count < before);
        await ws.CloseAsync();
    }

    [Fact]
    public async Task Sidebar_commands_route_through_command_target()
    {
        var (ws, vm, _) = await CreateAsync();
        var ctx = new CommandContext { CommandId = CommandIds.SidebarOpenItem, Workspace = ws, Target = vm };
        vm.SelectedNodes = [vm.Nodes.Single(n => n.Key == "public/Tables/customers")];
        Assert.True(vm.TryExecuteCommand(CommandIds.SidebarOpenItem, ctx));
        Assert.Equal("customers", ws.ActiveTab?.Name);

        var focus = 0;
        vm.FocusSearchRequested += () => focus++;
        ws.IsSidebarVisible = false;
        Assert.True(AppServices.Commands.TryExecute(CommandIds.SidebarSearch, ctx with { CommandId = CommandIds.SidebarSearch }));
        Assert.Equal(1, focus);
        Assert.True(ws.IsSidebarVisible);

        // Drop with a cancelled confirmation sends nothing.
        var asked = 0;
        var old = SidebarActions.ConfirmDestructive;
        SidebarActions.ConfirmDestructive = (_, _, _) => { asked++; return null; };
        try
        {
            Assert.True(vm.TryExecuteCommand(CommandIds.SidebarDropItem, ctx));
            await Task.Delay(50);
            Assert.Equal(1, asked);
        }
        finally
        {
            SidebarActions.ConfirmDestructive = old;
        }
        Assert.False(vm.TryExecuteCommand(CommandIds.TabNew, ctx));
        await ws.CloseAsync();
    }

    [Fact]
    public async Task Context_menu_lists_object_and_schema_entries()
    {
        var (ws, _, _) = await CreateAsync();
        var items = await RunSta(() =>
        {
            var table = AppServices.Features.SidebarMenu.Build(new SidebarMenuContext(ws, [new SidebarItemRef("public", "customers", ObjectKinds.Table)]));
            var schema = AppServices.Features.SidebarMenu.Build(new SidebarMenuContext(ws, [new SidebarItemRef("public", "public", ObjectKinds.Schema)]));
            return (Headers(table), Headers(schema));
        });
        Assert.Equal(["Open in New Tab", "Open Structure", "Copy Name", "Copy Script As", "-", "Truncate…", "Delete…"], items.Item1);
        Assert.Equal(["New Table…", "Copy Name", "Reload Schema"], items.Item2);
        await ws.CloseAsync();

        static List<string> Headers(IList<object> list) =>
            [.. list.Select(o => o is System.Windows.Controls.MenuItem m ? (string)m.Header : "-")];
    }

    [Fact]
    public async Task Copy_script_as_select_drop_and_create_from_metadata()
    {
        var (ws, _, _) = await CreateAsync();
        var refs = new[] { new SidebarItemRef("public", "customers", ObjectKinds.Table), new SidebarItemRef("public", "active_customers", ObjectKinds.View) };
        Assert.Equal("SELECT * FROM public.customers LIMIT 100;\nSELECT * FROM public.active_customers LIMIT 100;", await SidebarActions.BuildScriptAsync(ws, refs, ScriptAs.Select));
        Assert.Equal("DROP TABLE public.customers;\nDROP VIEW public.active_customers;", await SidebarActions.BuildScriptAsync(ws, refs, ScriptAs.Drop));
        Assert.Equal("TRUNCATE TABLE public.customers;", await SidebarActions.BuildScriptAsync(ws, refs, ScriptAs.Truncate));
        Assert.StartsWith("CREATE TABLE public.customers (", await SidebarActions.BuildScriptAsync(ws, [refs[0]], ScriptAs.Create));

        string? copied = null;
        var old = SidebarActions.SetClipboard;
        SidebarActions.SetClipboard = t => copied = t;
        try
        {
            SidebarActions.CopyNames(refs);
            Assert.Equal("customers" + Environment.NewLine + "active_customers", copied);
        }
        finally
        {
            SidebarActions.SetClipboard = old;
        }
        await ws.CloseAsync();
    }

    [Fact]
    public async Task New_table_requests_create_table_tab()
    {
        var root = TestApp.Init(new WorkspaceFeature(), new FakeTabsFeature(withSqlEditor: true), new SidebarFeature(), new CreateTableFake());
        _ = root;
        var ws = new WorkspaceViewModel(TestApp.Profile(), ConnectionSecrets.None, null, shell: null);
        var tab = SidebarActions.NewTable(ws, "sales");
        Assert.NotNull(tab);
        Assert.Equal(TabKinds.CreateTable, tab!.Kind);
        Assert.Equal("sales", tab.Schema);
        await ws.CloseAsync();
    }

    private sealed class CreateTableFake : IFeature
    {
        public void Register(FeatureContext ctx) => ctx.Tabs.Register(TabKinds.CreateTable, r => new FakeTab(r.Kind, r.Schema, null));
    }

    [Fact]
    public async Task History_tab_loads_newest_first_filters_deletes_and_opens_editor()
    {
        var (ws, vm, _) = await CreateAsync();
        ws.History.Clear(ws.Profile.Id);
        ws.History.Append(ws.Profile.Id, new HistoryEntry { Sql = "select 1", Database = "db" });
        ws.History.Append(ws.Profile.Id, new HistoryEntry { Sql = "select * from customers\nwhere id = 2", Database = "db" });
        ws.History.Append(ws.Profile.Id, new HistoryEntry { Sql = "update orders set x = 1", Database = "db", Error = true });

        await vm.LoadHistoryAsync();
        Assert.Equal(["update orders set x = 1", "select * from customers …", "select 1"], vm.HistoryItems.Select(r => r.FirstLine));
        Assert.True(vm.HistoryItems[0].IsError);

        vm.HistorySearchText = "SELECT cust";
        Assert.Equal(["select * from customers …"], vm.HistoryItems.Select(r => r.FirstLine));

        var tab = vm.OpenHistory(vm.HistoryItems[0]);
        Assert.Equal(TabKinds.SqlEditor, tab!.Kind);

        vm.HistorySearchText = "";
        await vm.DeleteHistoryAsync([vm.HistoryItems[0]]);
        Assert.Equal(2, vm.HistoryItems.Count);
        await vm.ClearHistoryAsync();
        Assert.Empty(vm.HistoryItems);
        await ws.CloseAsync();
    }

    private static Task<T> RunSta<T>(Func<T> body)
    {
        var tcs = new TaskCompletionSource<T>();
        var t = new Thread(() =>
        {
            try { tcs.SetResult(body()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    // ---- integration ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Truncate_and_drop_run_through_safe_mode_and_refresh_the_schema()
    {
        if (!TestPg.Available(output)) return;
        TestApp.Init(new WorkspaceFeature(), new FakeTabsFeature(withSqlEditor: true), new SidebarFeature());
        var (profile, secrets) = TestPg.Profile();
        var ws = new WorkspaceViewModel(profile with { SafeMode = SafeModeLevel.Silent }, secrets, null, null);
        var schema = "sb_test_" + Guid.NewGuid().ToString("N")[..10];
        var oldConfirm = SidebarActions.ConfirmDestructive;
        try
        {
            await ws.ConnectAsync();
            await using (var conn = await ws.Session.OpenConnectionAsync())
            {
                await using var cmd = new Npgsql.NpgsqlCommand($"create schema {schema}; create table {schema}.t(id int generated always as identity, v text); insert into {schema}.t(v) values ('a'),('b'); create view {schema}.v as select * from {schema}.t;", conn);
                await cmd.ExecuteNonQueryAsync();
            }
            var loaded = await ws.Metadata.EnsureSchemaLoadedAsync(schema);
            Assert.NotNull(loaded!.FindTable("t"));

            DestructiveOptions? options = new(Cascade: false, RestartIdentity: true);
            SidebarActions.ConfirmDestructive = (_, action, objects) =>
            {
                Assert.Equal([$"{schema}.t"], objects.Select(o => o.QualifiedName));
                return options;
            };
            var t = new SidebarItemRef(schema, "t", ObjectKinds.Table);
            Assert.True(await SidebarActions.RunDestructiveAsync(ws, DestructiveAction.Truncate, [t]));
            await using (var conn = await ws.Session.OpenConnectionAsync())
            {
                await using var cmd = new Npgsql.NpgsqlCommand($"insert into {schema}.t(v) values ('c') returning id", conn);
                Assert.Equal(1, await cmd.ExecuteScalarAsync()); // identity restarted
            }

            // Script CREATE for the view comes from the server.
            var create = await SidebarActions.BuildScriptAsync(ws, [new SidebarItemRef(schema, "v", ObjectKinds.View)], ScriptAs.Create);
            Assert.StartsWith($"CREATE OR REPLACE VIEW {schema}.v AS", create);

            // Drop without cascade fails (the view depends on t) and reports the error; with cascade it succeeds.
            string? error = null;
            var oldError = SidebarActions.ShowError;
            SidebarActions.ShowError = (m, _) => error = m;
            options = new DestructiveOptions(Cascade: false, RestartIdentity: false);
            SidebarActions.ConfirmDestructive = (_, _, _) => options;
            try
            {
                Assert.False(await SidebarActions.RunDestructiveAsync(ws, DestructiveAction.Drop, [t]));
                Assert.NotNull(error);
                options = new DestructiveOptions(Cascade: true, RestartIdentity: false);
                var changed = new TaskCompletionSource();
                ws.Metadata.Changed += (_, e) => { if (e.Schema == schema) changed.TrySetResult(); };
                Assert.True(await SidebarActions.RunDestructiveAsync(ws, DestructiveAction.Drop, [t]));
                await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Null(ws.Metadata.Current.FindSchema(schema)!.FindTable("t"));
            }
            finally
            {
                SidebarActions.ShowError = oldError;
            }
            Assert.Contains(ws.History.Load(ws.Profile.Id), h => h.Source == "sidebar" && h.Sql.StartsWith($"DROP TABLE {schema}.t CASCADE", StringComparison.Ordinal));
        }
        finally
        {
            SidebarActions.ConfirmDestructive = oldConfirm;
            try
            {
                await using var conn = await ws.Session.OpenConnectionAsync();
                await using var cmd = new Npgsql.NpgsqlCommand($"drop schema if exists {schema} cascade", conn);
                await cmd.ExecuteNonQueryAsync();
            }
            catch (InvalidOperationException) { }
            await ws.CloseAsync();
        }
    }
}
