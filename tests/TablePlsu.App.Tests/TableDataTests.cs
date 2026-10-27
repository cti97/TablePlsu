using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using Npgsql;
using TablePlsu.App.Features.Results;
using TablePlsu.Core.Settings;
using TablePlsu.Core.Themes;
using TablePlsu.App.Features.SafeMode;
using TablePlsu.App.Features.TableData;
using TablePlsu.App.Features.Workspace;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Commands;
using TablePlsu.Core.Connections;
using TablePlsu.Core.Data;
using Xunit.Abstractions;

namespace TablePlsu.App.Tests;

public class FilterBarViewModelTests
{
    private static FilterBarViewModel Bar()
    {
        var bar = new FilterBarViewModel();
        bar.SetColumns(["id", "status"]);
        return bar;
    }

    [Fact]
    public void Columns_include_any_and_raw_and_default_to_first_column()
    {
        var bar = Bar();
        Assert.Equal(["Any column", "Raw SQL", "id", "status"], bar.ColumnOptions.Select(o => o.Label));
        Assert.Equal("id", bar.Rows[0].Column!.Column);
    }

    [Fact]
    public void Add_copies_current_remove_and_toggle()
    {
        var bar = Bar();
        bar.Rows[0].Value = "5";
        var added = bar.AddRow();
        Assert.Equal(2, bar.Rows.Count);
        Assert.Same(added, bar.Current);
        Assert.Equal("5", added.Value);
        bar.ToggleRow();
        Assert.False(added.Enabled);
        bar.RemoveRow();
        Assert.Single(bar.Rows);
        bar.RemoveRow();
        Assert.Single(bar.Rows);
        Assert.Equal("", bar.Rows[0].Value);
    }

    [Fact]
    public void Enter_applies_one_row_and_ctrl_enter_applies_checked_rows()
    {
        var bar = Bar();
        var applied = 0;
        bar.AppliedChanged += (_, _) => applied++;
        bar.Rows[0].Value = "1";
        var second = bar.AddRow();
        second.Column = bar.ColumnOptions[3];
        second.Operator = FilterOperatorOption.Of(FilterOperator.IContains);
        second.Value = "pa";

        bar.ApplyRow(second);
        var only = Assert.Single(bar.Applied);
        Assert.Equal("status", only.Column);
        Assert.Equal(FilterOperator.IContains, only.Operator);

        bar.Rows[0].Enabled = false;
        bar.ApplyAll(FilterJoin.Or);
        Assert.Equal(FilterJoin.Or, bar.Join);
        Assert.Single(bar.Applied, c => c.Enabled);
        Assert.Equal(2, applied);
        Assert.True(bar.HasApplied);

        bar.Clear();
        Assert.Empty(bar.Applied);
        Assert.False(bar.HasApplied);
        Assert.Equal(3, applied);
    }

    [Fact]
    public void Raw_and_between_rows_expose_the_right_inputs()
    {
        var bar = Bar();
        var row = bar.Rows[0];
        row.Operator = FilterOperatorOption.Of(FilterOperator.Between);
        Assert.True(row.ShowValue2);
        row.Operator = FilterOperatorOption.Of(FilterOperator.IsNull);
        Assert.False(row.ShowValue);
        row.Column = FilterColumnOption.RawSql;
        Assert.True(row.IsRaw);
        Assert.True(row.ShowValue);
        Assert.False(row.ShowOperator);
        Assert.Equal(FilterTarget.RawSql, row.ToCondition().Target);
    }

    [Fact]
    public void Save_and_restore_round_trip()
    {
        var bar = Bar();
        bar.Rows[0].Value = "7";
        bar.ApplyAll();
        bar.IsVisible = true;
        var state = bar.Save();
        var other = Bar();
        other.Restore(state);
        Assert.Equal("7", other.Rows[0].Value);
        Assert.Equal("id", other.Rows[0].Column!.Column);
        Assert.True(other.IsVisible);
        Assert.Single(other.Applied);
    }
}

public class TableSubViewRegistryTests
{
    [Fact]
    public void Register_and_create()
    {
        const string kind = "test-kind-registry";
        Assert.False(TableSubViewRegistry.IsRegistered(kind));
        TableSubViewRegistry.Register(kind, (_, s, t) => new System.Windows.FrameworkElement { Tag = s + "." + t });
        try
        {
            Assert.True(TableSubViewRegistry.IsRegistered(kind));
        }
        finally
        {
            Assert.True(TableSubViewRegistry.Unregister(kind));
        }
    }
}

[Collection(AppServicesCollection.Name)]
public class SafeModeGateTests
{
    private sealed class FakePrompts : ISafeModePrompts
    {
        public Queue<string?> Passwords { get; } = new();
        public bool ConfirmAnswer { get; set; } = true;
        public int Confirms { get; private set; }
        public int PasswordPrompts { get; private set; }
        public List<string?> Errors { get; } = [];

        public bool Confirm(SafeModeLevel level, IReadOnlyList<string> statements)
        {
            Confirms++;
            return ConfirmAnswer;
        }

        public string? AskPassword(string message, IReadOnlyList<string>? statements, string? error)
        {
            PasswordPrompts++;
            Errors.Add(error);
            return Passwords.Count > 0 ? Passwords.Dequeue() : null;
        }
    }

    private sealed class FakeVerifier(bool canVerify = true) : ISafeModePasswordVerifier
    {
        public bool CanVerify(ConnectionProfile profile) => canVerify;

        public Task<bool> VerifyAsync(ConnectionProfile profile, string database, string password, CancellationToken ct = default) =>
            Task.FromResult(password == "secret");
    }

    private static (SafeModeGate Gate, FakePrompts Prompts, List<ConnectionProfile> Saved) Gate(SafeModeLevel level, bool canVerify = true)
    {
        TestApp.Init(new WorkspaceFeature());
        var ws = new WorkspaceViewModel(TestApp.Profile() with { SafeMode = level }, ConnectionSecrets.None, null, null);
        var prompts = new FakePrompts();
        var saved = new List<ConnectionProfile>();
        return (new SafeModeGate(ws, prompts, new FakeVerifier(canVerify), saved.Add), prompts, saved);
    }

    [Fact]
    public async Task Silent_sends_without_asking()
    {
        var (gate, prompts, _) = Gate(SafeModeLevel.Silent);
        Assert.True(await gate.ConfirmAsync(["delete from t"]));
        Assert.Equal(0, prompts.Confirms + prompts.PasswordPrompts);
    }

    [Fact]
    public async Task Alert2_skips_reads_and_confirms_writes()
    {
        var (gate, prompts, _) = Gate(SafeModeLevel.Alert2);
        Assert.True(await gate.ConfirmAsync(["select 1"]));
        Assert.Equal(0, prompts.Confirms);
        prompts.ConfirmAnswer = false;
        Assert.False(await gate.ConfirmAsync(["update t set a = 1"]));
        Assert.Equal(1, prompts.Confirms);
    }

    [Fact]
    public async Task Safe1_requires_the_password_with_retries()
    {
        var (gate, prompts, _) = Gate(SafeModeLevel.Safe1);
        prompts.Passwords.Enqueue("wrong");
        prompts.Passwords.Enqueue("secret");
        Assert.True(await gate.ConfirmAsync(["select 1"]));
        Assert.Equal(2, prompts.PasswordPrompts);
        Assert.Equal([null, "Wrong password."], prompts.Errors);

        prompts.Passwords.Enqueue(null); // cancel
        Assert.False(await gate.ConfirmAsync(["select 1"]));
    }

    [Fact]
    public async Task Safe_level_without_verifiable_password_falls_back_to_confirm()
    {
        var (gate, prompts, _) = Gate(SafeModeLevel.Safe2, canVerify: false);
        Assert.True(await gate.ConfirmAsync(["delete from t"]));
        Assert.Equal(1, prompts.Confirms);
        Assert.Equal(0, prompts.PasswordPrompts);
    }

    [Fact]
    public async Task Lowering_from_safe_requires_password_and_persists()
    {
        var (gate, prompts, saved) = Gate(SafeModeLevel.Safe2);
        var changed = 0;
        gate.LevelChanged += (_, _) => changed++;
        prompts.Passwords.Enqueue(null);
        Assert.False(await gate.ChangeLevelAsync(SafeModeLevel.Silent));
        Assert.Equal(SafeModeLevel.Safe2, gate.Level);

        Assert.True(await gate.ChangeLevelAsync(SafeModeLevel.Safe1)); // raising: no password
        Assert.Equal(1, prompts.PasswordPrompts);

        prompts.Passwords.Enqueue("secret");
        Assert.True(await gate.ChangeLevelAsync(SafeModeLevel.Alert1));
        Assert.Equal(SafeModeLevel.Alert1, gate.Level);
        Assert.Equal(2, changed);
        Assert.Equal(SafeModeLevel.Alert1, saved[^1].SafeMode);
        Assert.False(await gate.ChangeLevelAsync(SafeModeLevel.Alert1));
    }

    [Fact]
    public void Feature_replaces_the_default_gate()
    {
        TestApp.Init(new WorkspaceFeature(), new SafeModeFeature());
        var ws = new WorkspaceViewModel(TestApp.Profile(), ConnectionSecrets.None, null, null);
        Assert.IsType<SafeModeGate>(ws.SafeMode);
    }
}

[Collection(AppServicesCollection.Name)]
public class TableTabViewModelTests(ITestOutputHelper output)
{
    private static async Task<WorkspaceViewModel> ConnectAsync()
    {
        TestApp.Init(new WorkspaceFeature(), new TableDataFeature(), new SafeModeFeature());
        var (profile, secrets) = TestPg.Profile();
        var ws = new WorkspaceViewModel(profile, secrets, null, null);
        await ws.ConnectAsync();
        return ws;
    }

    private static async Task<TableTabViewModel> OpenAsync(WorkspaceViewModel ws, string schema, string name, string kind = ObjectKinds.Table)
    {
        var tab = Assert.IsType<TableTabViewModel>(ws.OpenTab(TabRequest.ForObject(schema, name, kind, pinned: true)));
        await tab.LoadAsync(recount: true);
        return tab;
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }

    [Fact]
    public void Factories_are_registered_for_tables_views_and_matviews()
    {
        TestApp.Init(new WorkspaceFeature(), new TableDataFeature());
        foreach (var kind in new[] { TabKinds.Table, TabKinds.View, TabKinds.MaterializedView })
            Assert.True(AppServices.Features.Tabs.IsRegistered(kind));
    }

    [Fact]
    public async Task Sales_orders_pages_sorts_and_filters()
    {
        if (!TestPg.Available(output)) return;
        var ws = await ConnectAsync();
        try
        {
            var tab = await OpenAsync(ws, "sales", "orders");
            Assert.Null(tab.ErrorText);
            await WaitFor(() => tab.IsEditable);
            Assert.Equal(["id", "customer_id", "total", "status", "placed_at"], tab.Columns!.Select(c => c.Name));
            Assert.Equal(300, tab.Rows.Count);
            Assert.True(tab.CanNextPage);
            Assert.False(tab.CanPrevPage);
            await WaitFor(() => tab.RowCountText.Contains("200,000"));

            tab.NextPage();
            await WaitFor(() => tab.Offset == 300 && !tab.IsBusy && tab.LoadedCount == 300);
            Assert.Equal("301–600", tab.PageText);
            Assert.Equal(301L, Convert.ToInt64(tab.Rows[0][0]));

            // Header click: DESC, then ASC (server-side).
            tab.OnSortRequested("id", addToMulti: false);
            await WaitFor(() => !tab.IsBusy && tab.Offset == 0 && Convert.ToInt64(tab.Rows[0][0]) == 200_000);
            tab.OnSortRequested("id", addToMulti: false);
            await WaitFor(() => !tab.IsBusy && Convert.ToInt64(tab.Rows[0][0]) == 1);

            // Ctrl+Right / Ctrl+Left through the command target.
            Assert.True(tab.TryExecuteCommand(CommandIds.PageNext, new CommandContext { CommandId = CommandIds.PageNext }));
            await WaitFor(() => !tab.IsBusy && tab.Offset == 300);

            // Filter: status = paid AND total between 10 and 20 → exact count, back to page 1.
            tab.Filter.Rows[0].Column = tab.Filter.ColumnOptions.First(o => o.Column == "status");
            tab.Filter.Rows[0].Value = "paid";
            var second = tab.Filter.AddRow();
            second.Column = tab.Filter.ColumnOptions.First(o => o.Column == "total");
            second.Operator = FilterOperatorOption.Of(FilterOperator.Between);
            second.Value = "10";
            second.Value2 = "20";
            tab.Filter.ApplyAll();
            await WaitFor(() => !tab.IsBusy && tab.Offset == 0 && tab.Rows.Count > 0 && tab.Rows.All(r => (string?)r[3] == "paid"));
            Assert.All(tab.Rows, r => Assert.InRange((decimal)r[2]!, 10m, 20m));
            await WaitFor(() => tab.RowCountText.EndsWith(" rows", StringComparison.Ordinal) && !tab.RowCountText.StartsWith('≈'));
            output.WriteLine(tab.RowCountText);
        }
        finally
        {
            await ws.CloseAsync();
        }
    }

    [Fact]
    public async Task Tables_without_pk_and_views_are_read_only_with_a_reason()
    {
        if (!TestPg.Available(output)) return;
        var ws = await ConnectAsync();
        try
        {
            var noPk = await OpenAsync(ws, "public", "no_pk");
            await WaitFor(() => noPk.ReadOnlyReason?.Contains("primary key") == true);
            Assert.False(noPk.IsEditable);
            Assert.Contains("primary key", noPk.ReadOnlyReason);
            Assert.Null(noPk.InsertRow());

            var view = await OpenAsync(ws, "public", "active_customers", ObjectKinds.View);
            Assert.False(view.IsEditable);
            Assert.Contains("read-only", view.ReadOnlyReason);
            Assert.True(view.Rows.Count > 0);

            var mv = await OpenAsync(ws, "sales", "order_totals", ObjectKinds.MaterializedView);
            Assert.False(mv.IsEditable);
            Assert.Null(mv.ErrorText);
        }
        finally
        {
            await ws.CloseAsync();
        }
    }

    [Fact]
    public async Task Edits_commit_in_one_transaction_and_failures_keep_changes_staged()
    {
        if (!TestPg.Available(output)) return;
        var table = "td_tab_" + Guid.NewGuid().ToString("N")[..10];
        await using var admin = new NpgsqlConnection(TestPg.ConnectionString);
        await admin.OpenAsync();
        await Exec(admin, $"CREATE TABLE public.{table}(id serial primary key, name text not null, qty int default 7, at timestamptz); " +
                          $"INSERT INTO public.{table}(name, qty) VALUES ('a', 1), ('b', 2), ('c', 3)");
        var ws = await ConnectAsync();
        try
        {
            var tab = await OpenAsync(ws, "public", table);
            await WaitFor(() => tab.IsEditable);
            tab.OnSortRequested("id", false);
            tab.OnSortRequested("id", false); // ASC
            await WaitFor(() => !tab.IsBusy && tab.Rows.Count == 3 && Convert.ToInt32(tab.Rows[0][0]) == 1);
            var rows = tab.Rows.ToList();

            // Edit, insert, delete; the generated column check: "id" is editable (no generated columns here).
            Assert.Equal("100", tab.CommitCellEdit(rows[0], 2, "100"));
            Assert.True(tab.HasPendingChanges);
            Assert.Equal(RowState.Modified, tab.GetRowState(rows[0]));
            var inserted = tab.InsertRow()!;
            Assert.Contains(inserted, tab.Rows);
            Assert.Equal(RowState.New, tab.GetRowState(inserted));
            tab.Pending!.DeleteRow(rows[2]);
            Assert.Equal(RowState.Deleted, tab.GetRowState(rows[2]));
            Assert.False(tab.CanEditCell(rows[2], 1));

            // Undo/redo through the workspace-scope commands.
            Assert.True(tab.TryExecuteCommand(CommandIds.EditUndo, new CommandContext { CommandId = CommandIds.EditUndo }));
            Assert.Equal(RowState.Unchanged, tab.GetRowState(rows[2]));
            Assert.True(tab.TryExecuteCommand(CommandIds.EditRedo, new CommandContext { CommandId = CommandIds.EditRedo }));
            Assert.Equal(RowState.Deleted, tab.GetRowState(rows[2]));

            // The inserted row has name NULL → NOT NULL violation → everything rolled back, changes stay staged.
            tab.Pending.SetCell(inserted, 1, null);
            Assert.False(await tab.CommitCoreAsync(showErrors: false));
            Assert.True(tab.HasPendingChanges);
            Assert.Equal(2, tab.LastFailedStatement);
            Assert.Contains("null", tab.LastCommitError, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(3L, await Scalar(admin, $"SELECT count(*) FROM public.{table}"));
            Assert.Equal(1, Convert.ToInt32(await Scalar(admin, $"SELECT qty FROM public.{table} WHERE id = 1")));

            // Fix it and commit through the shell's Ctrl+S path.
            tab.Pending.SetCell(inserted, 1, "new");
            await tab.CommitAsync();
            await WaitFor(() => !tab.HasPendingChanges && !tab.IsBusy);
            Assert.Equal(100, Convert.ToInt32(await Scalar(admin, $"SELECT qty FROM public.{table} WHERE id = 1")));
            Assert.Equal(0L, await Scalar(admin, $"SELECT count(*) FROM public.{table} WHERE id = 3"));
            Assert.Equal(7, Convert.ToInt32(await Scalar(admin, $"SELECT qty FROM public.{table} WHERE name = 'new'")));
            Assert.Null(tab.LastCommitError);
            await WaitFor(() => tab.Rows.Count == 3);

            // History got the commit statements.
            var history = ws.History.Load(ws.Profile.Id);
            Assert.Contains(history, h => h.Source == "commit" && h.Sql.StartsWith($"UPDATE public.{table} SET qty = '100'", StringComparison.Ordinal));

            // Paste as new rows + discard.
            Assert.Equal(2, tab.PasteAsNewRows("\tx\t5\r\n\ty\t\r\n"));
            Assert.Equal(2, tab.Pending.InsertedRows.Count);
            var spec = tab.GenerateStatements();
            Assert.Equal($"INSERT INTO public.{table} (name, qty) VALUES ('x', '5')", spec[0].LiteralSql);
            Assert.Equal($"INSERT INTO public.{table} (name) VALUES ('y')", spec[1].LiteralSql);
            tab.DiscardChanges();
            Assert.False(tab.HasPendingChanges);
            await WaitFor(() => !tab.IsBusy && tab.Rows.Count == 3);
        }
        finally
        {
            await ws.CloseAsync();
            await Exec(admin, $"DROP TABLE IF EXISTS public.{table}");
        }
    }

    private static async Task Exec(NpgsqlConnection conn, string sql)
    {
        await using var c = new NpgsqlCommand(sql, conn);
        await c.ExecuteNonQueryAsync();
    }

    private static async Task<object?> Scalar(NpgsqlConnection conn, string sql)
    {
        await using var c = new NpgsqlCommand(sql, conn);
        return await c.ExecuteScalarAsync();
    }
}

[Collection(AppServicesCollection.Name)]
public class TableTabViewUiTests(ITestOutputHelper output)
{
    /// <summary>Runs <paramref name="body"/> on an STA thread whose awaits resume on its dispatcher.</summary>
    private static void RunSta(Func<Task> body)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try
            {
                var task = body();
                Pump(() => task.IsCompleted, TimeSpan.FromSeconds(90));
                task.GetAwaiter().GetResult();
            }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException("STA body failed: " + error);
    }

    private static void Pump(Func<bool> until, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!until() && sw.Elapsed < timeout)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            if (!until()) Thread.Sleep(5);
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }

    private static void Exec(string sql)
    {
        using var admin = new NpgsqlConnection(TestPg.ConnectionString);
        admin.Open();
        using var c = new NpgsqlCommand(sql, admin);
        c.ExecuteNonQuery();
    }

    [Fact]
    public void View_shows_data_filter_bar_set_value_preview_and_paging()
    {
        if (!TestPg.Available(output)) return;
        var table = "td_ui_" + Guid.NewGuid().ToString("N")[..10];
        Exec($"CREATE TABLE public.{table}(id serial primary key, name text, at timestamptz default now()); " +
             $"INSERT INTO public.{table}(name) SELECT 'n' || g FROM generate_series(1, 50) g");
        try
        {
            RunSta(async () =>
            {
                TestApp.Init(new WorkspaceFeature(), new TableDataFeature(), new SafeModeFeature());
                var (profile, secrets) = TestPg.Profile();
                var ws = new WorkspaceViewModel(profile, secrets, null, null);
                await ws.ConnectAsync();
                try
                {
                    var vm = Assert.IsType<TableTabViewModel>(ws.OpenTab(TabRequest.ForObject("public", table, ObjectKinds.Table, pinned: true)));
                    var view = (TableTabView)vm.CreateView();
                    var window = new Window
                    {
                        Width = 1000, Height = 700, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false,
                        WindowStartupLocation = WindowStartupLocation.Manual, Content = view,
                    };
                    window.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
                        new Uri("/TablePlsu.App;component/Shell/Styles.xaml", UriKind.Relative)));
                    ThemeApplier.ApplyTo(window.Resources, ThemeService.BuiltInLight);
                    GridResources.ApplyTo(window.Resources, new GridSettings());
                    window.Show();
                    try
                    {
                        Assert.Same(view, vm.View);
                        await Until(() => vm.Rows.Count == 50 && !vm.IsBusy);
                        await Until(() => view.Grid.DataGrid.Items.Count == 50);
                        Assert.Equal(3, view.Grid.DataGrid.Columns.Count);
                        await Until(() => vm.IsEditable && !view.Grid.IsReadOnly);

                        // Ctrl+F toggles the filter bar.
                        var bar = (FrameworkElement)view.FindName("FilterBar");
                        Assert.Equal(Visibility.Collapsed, bar.Visibility);
                        Assert.True(vm.TryExecuteCommand(CommandIds.TableFilterToggle, new CommandContext { CommandId = CommandIds.TableFilterToggle }));
                        await Until(() => bar.Visibility == Visibility.Visible);

                        // Set Value (NULL) on the selected cell; delete another row (struck through).
                        view.Grid.ScrollToRow(0, 1);
                        Assert.False(vm.CurrentColumnIsTemporal());
                        vm.SetSelectedCells(null);
                        Assert.True(vm.HasPendingChanges);
                        Assert.Null(vm.Rows[0][1]);
                        view.Grid.ScrollToRow(1, 0);
                        view.Grid.DataGrid.UpdateLayout();
                        vm.DeleteSelectedRows();
                        Assert.Equal(RowState.Deleted, vm.GetRowState(vm.Rows[1]));
                        await Task.Delay(100);
                        view.Grid.DataGrid.UpdateLayout();
                        var row1 = (DataGridRow)view.Grid.DataGrid.ItemContainerGenerator.ContainerFromItem(vm.Rows[1]);
                        var adorners = AdornerLayer.GetAdornerLayer(row1)?.GetAdorners(row1) ?? [];
                        Assert.Contains(adorners, a => a is StrikeThroughAdorner);
                        Assert.Equal(2, vm.GenerateStatements().Count);

                        // Preview window, then Discard drops everything and reloads.
                        vm.PreviewChanges();
                        Assert.True(vm.IsPreviewOpen);
                        vm.DiscardChanges();
                        Assert.False(vm.IsPreviewOpen);
                        Assert.False(vm.HasPendingChanges);
                        await Until(() => !vm.IsBusy && vm.Rows[0][1] is "n1");

                        // Limit / Offset popover's Go.
                        vm.GoTo(20, 40);
                        await Until(() => !vm.IsBusy && vm.LoadedCount == 10);
                        Assert.Equal("41–50", vm.PageText);
                        Assert.False(vm.CanNextPage);

                        // Bottom tabs: a placeholder when nothing is registered for Structure (the Structure feature
                        // registers one when it is loaded in the same test process).
                        Assert.True(vm.TryExecuteCommand(CommandIds.TableShowStructure, new CommandContext { CommandId = CommandIds.TableShowStructure }));
                        Assert.Equal(TableSubView.Structure, vm.SelectedSubView);
                        if (TableSubViewRegistry.IsRegistered(TableSubViewRegistry.Structure)) Assert.IsNotType<TextBlock>(vm.SubViewContent);
                        else Assert.IsType<TextBlock>(vm.SubViewContent);
                        await Until(() => view.Grid.Visibility == Visibility.Collapsed);
                    }
                    finally
                    {
                        window.Close();
                    }
                }
                finally
                {
                    await ws.CloseAsync();
                }
            });
        }
        finally
        {
            Exec($"DROP TABLE IF EXISTS public.{table}");
        }
    }
}
