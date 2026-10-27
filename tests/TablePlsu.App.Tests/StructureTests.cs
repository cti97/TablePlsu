using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using Npgsql;
using TablePlsu.App.Features.Results;
using TablePlsu.App.Features.Structure;
using TablePlsu.App.Features.Workspace;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Commands;
using TablePlsu.Core.Ddl;
using TablePlsu.Core.Metadata;
using TablePlsu.Core.Settings;
using TablePlsu.Core.Themes;
using Xunit.Abstractions;

namespace TablePlsu.App.Tests;

public class StructureUnitTests
{
    [Fact]
    public void Highlighting_xshd_from_theme_loads()
    {
        foreach (var theme in new[] { ThemeService.BuiltInDark, ThemeService.BuiltInLight })
        {
            using var reader = XmlReader.Create(new StringReader(SqlText.BuildXshd(theme)));
            var def = HighlightingLoader.Load(HighlightingLoader.LoadXshd(reader), HighlightingManager.Instance);
            Assert.NotNull(def.GetNamedColor("Keyword"));
        }
    }

    [Fact]
    public void Structure_and_index_rows_from_metadata()
    {
        var t = new TableInfo
        {
            Schema = "sales",
            Name = "orders",
            Columns =
            [
                new ColumnInfo { Name = "id", Ordinal = 1, DataType = "bigint", IsNullable = false, Identity = "a" },
                new ColumnInfo { Name = "customer_id", Ordinal = 2, DataType = "integer", Comment = "who" },
            ],
            PrimaryKey = ["id"],
            ForeignKeys = [new ForeignKeyInfo { Name = "fk", Columns = ["customer_id"], RefSchema = "public", RefTable = "customers", RefColumns = ["id"] }],
            Indexes =
            [
                new IndexInfo { Name = "z_idx", Method = "gin", Columns = ["a", "b"], Predicate = "(a > 0)" },
                new IndexInfo { Name = "orders_pkey", IsPrimary = true, IsUnique = true, Method = "btree", Columns = ["id"] },
            ],
        };
        var rows = StructureViews.ColumnRows(t);
        Assert.Equal(["id", "bigint", "NO", "GENERATED ALWAYS AS IDENTITY", null, null], rows[0]);
        Assert.Equal(["customer_id", "integer", "YES", null, "public.customers(id)", "who"], rows[1]);

        var idx = StructureViews.IndexRows(t, new TableDdlExtras { IndexComments = new Dictionary<string, string> { ["z_idx"] = "c" } });
        Assert.Equal(["orders_pkey", true, "id", "btree", null, null], idx[0]);
        Assert.Equal(["z_idx", false, "a, b", "gin", "(a > 0)", "c"], idx[1]);
    }
}

[Collection(AppServicesCollection.Name)]
public class StructureIntegrationTests(ITestOutputHelper output)
{
    /// <summary>
    /// Runs an async body on an STA thread with a pumping dispatcher. UiDispatch is deliberately left alone: it is global,
    /// and pointing it at this short-lived dispatcher would break UI tests running in parallel.
    /// </summary>
    private static void RunSta(Func<Task> body)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            try
            {
                var task = body();
                var frame = new DispatcherFrame();
                task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
                Dispatcher.PushFrame(frame);
                task.GetAwaiter().GetResult();
            }
            catch (Exception ex) { error = ex; }
            finally
            {
                try { dispatcher.InvokeShutdown(); } catch (NullReferenceException) { }
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException("STA body failed: " + error);
    }

    private static Window Show(FrameworkElement content)
    {
        var window = new Window
        {
            Width = 1200, Height = 800, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Content = content,
        };
        ThemeApplier.ApplyTo(window.Resources, ThemeService.BuiltInDark);
        GridResources.ApplyTo(window.Resources, new GridSettings());
        window.Show();
        return window;
    }

    private static async Task Until(Func<bool> condition, int timeoutMs = 15000)
    {
        for (var waited = 0; !condition(); waited += 25)
        {
            if (waited > timeoutMs) throw new TimeoutException("condition not met");
            await Task.Delay(25);
        }
    }

    private static async Task<WorkspaceViewModel> ConnectAsync()
    {
        TestApp.Init(new WorkspaceFeature(), new FakeTabsFeature(withSqlEditor: false), new StructureFeature());
        var (profile, secrets) = TestPg.Profile();
        var ws = new WorkspaceViewModel(profile, secrets, null, null);
        await ws.ConnectAsync();
        await ws.RefreshMetadata();
        return ws;
    }

    private static async Task ExecAsync(WorkspaceViewModel ws, string sql)
    {
        await using var conn = await ws.Session.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(WorkspaceViewModel ws, string sql)
    {
        await using var conn = await ws.Session.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync();
    }

    [Fact]
    public void Structure_and_index_views_show_seed_tables()
    {
        if (!TestPg.Available(output)) return;
        RunSta(async () =>
        {
            var ws = await ConnectAsync();
            try
            {
                var tab = (StructureTabViewModel)ws.OpenTab(new TabRequest { Kind = TabKinds.Structure, Schema = "public", Name = "customers", Pinned = true })!;
                var window = Show(tab.CreateView());
                var cols = tab.ColumnsPanel!;
                await Until(() => cols.TableInfo is not null && cols.VisibleRows.Count > 0);
                Assert.Equal(["id", "name", "email", "created_at", "meta", "active"], cols.VisibleRows.Select(r => (string)r[0]!));
                Assert.Equal("NO", cols.VisibleRows[1][2]);
                Assert.Contains(cols.Extras.Constraints, c => c.Type == 'u');
                Assert.Contains("CONSTRAINT customers_email_key UNIQUE (email)", cols.Ddl);

                cols.SearchText = "MAIL";
                Assert.Equal(["email"], cols.VisibleRows.Select(r => (string)r[0]!));
                Assert.True(tab.TryExecuteCommand(CommandIds.TableShowIndexes, new CommandContext { CommandId = CommandIds.TableShowIndexes }));

                var idx = tab.IndexesPanel!;
                await Until(() => idx.VisibleRows.Count == 2);
                Assert.Equal("customers_pkey", idx.VisibleRows[0][0]);
                Assert.Equal(true, idx.VisibleRows[1][1]);

                // Hostable factory for another table: FK column shows its target.
                var orders = (StructurePanel)StructureViews.CreateStructure(ws, "sales", "orders");
                window.Content = orders;
                await Until(() => orders.VisibleRows.Count > 0);
                Assert.Equal("public.customers(id)", orders.VisibleRows.Single(r => (string)r[0]! == "customer_id")[4]);
                window.Close();
            }
            finally
            {
                await ws.CloseAsync();
            }
        });
    }

    [Fact]
    public void View_ddl_edit_and_matview_refresh_and_function_save()
    {
        if (!TestPg.Available(output)) return;
        var schema = "struct_test_" + Guid.NewGuid().ToString("N")[..10];
        var errors = new List<string>();
        var previous = StructureServices.ReportError;
        StructureServices.ReportError = (m, t, d) => errors.Add(t + ": " + m);
        try
        {
            RunSta(async () =>
            {
                var ws = await ConnectAsync();
                try
                {
                    await ExecAsync(ws, $"""
                        create schema {schema};
                        create table {schema}.t(a int);
                        insert into {schema}.t values (1),(2),(3);
                        create view {schema}.v as select a from {schema}.t where a > 1;
                        create materialized view {schema}.mv as select count(*) n from {schema}.t;
                        create function {schema}.f(x int) returns int language sql as $$ select x $$;
                        create function {schema}.f(x text) returns text language sql as $$ select x $$;
                        """);
                    await ws.Metadata.EnsureSchemaLoadedAsync(schema, force: true);

                    // View: editable, Ctrl+S runs CREATE OR REPLACE VIEW.
                    var vtab = (ViewDdlTabViewModel)ws.OpenTab(new TabRequest { Kind = StructureTabKinds.ViewDdl, Schema = schema, Name = "v", Pinned = true })!;
                    var window = Show(vtab.CreateView());
                    var panel = vtab.Panel!;
                    await Until(() => panel.Definition is not null);
                    Assert.False(panel.Editor.IsReadOnly);
                    Assert.StartsWith($"CREATE OR REPLACE VIEW {schema}.v AS", panel.Editor.Text);
                    panel.Editor.Text = panel.Editor.Text.Replace("> 1", "> 2");
                    Assert.True(vtab.IsDirty);
                    Assert.True(panel.TryExecuteCommand(CommandIds.ChangesCommit, new CommandContext { CommandId = CommandIds.ChangesCommit, Workspace = ws }));
                    await Until(() => !vtab.IsDirty && panel.Editor.Text.Contains("> 2"));
                    Assert.Equal(1L, await ScalarAsync(ws, $"select count(*) from {schema}.v"));

                    // Bad edit reports the error and keeps the text.
                    panel.Editor.Text = panel.Editor.Text.Replace("SELECT", "SELEC");
                    Assert.False(await panel.SaveAsync());
                    Assert.Single(errors);
                    Assert.True(vtab.IsDirty);

                    // Materialized view: read-only + refresh.
                    var mpanel = (ViewDdlPanel)StructureViews.CreateViewDdl(ws, schema, "mv");
                    window.Content = mpanel;
                    await Until(() => mpanel.Definition is not null);
                    Assert.True(mpanel.IsMaterialized);
                    Assert.True(mpanel.Editor.IsReadOnly);
                    Assert.StartsWith($"CREATE MATERIALIZED VIEW {schema}.mv AS", mpanel.Editor.Text);
                    await ExecAsync(ws, $"insert into {schema}.t values (4)");
                    Assert.True(await mpanel.RefreshMaterializedAsync());
                    Assert.Equal(4L, await ScalarAsync(ws, $"select n from {schema}.mv"));

                    // Function: the overload picked by Parameter; Ctrl+S runs the edited definition.
                    var ftab = (FunctionTabViewModel)ws.OpenTab(new TabRequest
                    {
                        Kind = TabKinds.Function, Schema = schema, Name = "f", ObjectKind = ObjectKinds.Function, Parameter = "x integer", Pinned = true,
                    })!;
                    window.Content = ftab.CreateView();
                    await Until(() => ftab.Function is not null && ftab.Text.Contains("CREATE OR REPLACE FUNCTION"));
                    Assert.Equal("f(x integer)", ftab.Title);
                    ftab.Editor!.Text = ftab.Text.Replace("select x", "select x * 100");
                    Assert.True(ftab.IsDirty);
                    Assert.True(await ftab.SaveAsync());
                    Assert.False(ftab.IsDirty);
                    Assert.Equal(700, await ScalarAsync(ws, $"select {schema}.f(7)"));
                    Assert.Same(ftab, ws.OpenTab(new TabRequest { Kind = TabKinds.Function, Schema = schema, Name = "f", Parameter = "x integer" }));
                    window.Close();
                }
                finally
                {
                    await ExecAsync(ws, $"drop schema if exists {schema} cascade");
                    await ws.CloseAsync();
                }
            });
        }
        finally
        {
            StructureServices.ReportError = previous;
        }
        Assert.Single(errors);
        Assert.Contains("syntax error", errors[0]);
    }

    [Fact]
    public void Create_table_round_trip_opens_new_table()
    {
        if (!TestPg.Available(output)) return;
        var schema = "struct_test_" + Guid.NewGuid().ToString("N")[..10];
        var errors = new List<string>();
        var previous = StructureServices.ReportError;
        StructureServices.ReportError = (m, t, d) => errors.Add(t + ": " + m);
        try
        {
            RunSta(async () =>
            {
                var ws = await ConnectAsync();
                try
                {
                    await ExecAsync(ws, $"create schema {schema}");
                    await ws.Metadata.EnsureSchemaLoadedAsync(schema, force: true);
                    var tab = (CreateTableTabViewModel)ws.OpenTab(new TabRequest { Kind = TabKinds.CreateTable, Schema = schema, Pinned = true, InNewTab = true })!;
                    var window = Show(tab.CreateView());
                    Assert.Equal(schema, tab.SchemaName);
                    Assert.Contains(schema, tab.Schemas);
                    Assert.Single(tab.Columns);

                    string? previewed = null;
                    tab.ConfirmPreview = sql => { previewed = sql; return true; };
                    Assert.False(await tab.CreateAsync()); // no name
                    Assert.Contains("name", tab.Message);

                    tab.TableName = "gadgets";
                    Assert.True(tab.TryExecuteCommand(CommandIds.GridInsertRow, new CommandContext { CommandId = CommandIds.GridInsertRow }));
                    var label = tab.Columns[1];
                    label.Name = "label";
                    label.DataType = "varchar(30)";
                    label.IsNullable = false;
                    label.Default = DefaultChoices.Empty;
                    label.Comment = "visible label";
                    tab.AddColumn();
                    tab.Columns[2].Name = "created_at";
                    tab.Columns[2].DataType = "timestamptz";
                    tab.Columns[2].Default = DefaultChoices.CurrentTimestamp;
                    tab.AddColumn();
                    tab.DeleteColumn(tab.Columns[3]);
                    Assert.Equal(3, tab.Columns.Count);
                    Assert.True(tab.IsDirty);

                    Assert.True(await tab.CreateAsync());
                    Assert.Contains($"CREATE TABLE {schema}.gadgets (", previewed);
                    Assert.Empty(errors);

                    var t = ws.Metadata.FindTable(schema, "gadgets");
                    Assert.NotNull(t);
                    Assert.Equal(["id"], t.PrimaryKey);
                    Assert.Equal("visible label", t.FindColumn("label")!.Comment);
                    Assert.Equal("character varying(30)", t.FindColumn("label")!.DataType);
                    Assert.DoesNotContain(tab, ws.Tabs);
                    Assert.Contains(ws.Tabs, x => x.Kind == TabKinds.Table && x.Schema == schema && x.Name == "gadgets");
                    await ExecAsync(ws, $"insert into {schema}.gadgets default values");
                    window.Close();
                }
                finally
                {
                    await ExecAsync(ws, $"drop schema if exists {schema} cascade");
                    await ws.CloseAsync();
                }
            });
        }
        finally
        {
            StructureServices.ReportError = previous;
        }
    }
}
