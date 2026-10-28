using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Npgsql;
using TablePlsu.App.Features.Palette;
using TablePlsu.App.Features.Sidebar;
using TablePlsu.App.Features.Workspace;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Connections;
using Xunit.Abstractions;

namespace TablePlsu.App.Tests;

/// <summary>
/// The Items tab's empty-database hint: connected to a database with no user objects (the maintenance DB 'postgres'),
/// the sidebar lists the server's other databases instead of a bare tree.
/// </summary>
[Collection(AppServicesCollection.Name)]
public class SidebarEmptyHintTests(ITestOutputHelper output)
{
    private sealed class FakeShell : IWorkspaceShell
    {
        public List<(string? Database, bool NewWindow)> Opened { get; } = [];
        public Window? Window => null;
        public void CloseWorkspace(WorkspaceViewModel workspace) { }
        public void OpenWorkspace(ConnectionProfile profile, ConnectionSecrets secrets, string? database, bool newWindow) => Opened.Add((database, newWindow));
        public ConnectionProfile? EditConnection(ConnectionProfile profile) => null;
    }

    private static async Task<(WorkspaceViewModel Ws, SidebarViewModel Vm, FakeShell Shell)> ConnectAsync(string database)
    {
        UiDispatch.Initialize(null!); // run marshalled work inline
        TestApp.Init(new WorkspaceFeature(), new FakeTabsFeature(withSqlEditor: true), new SidebarFeature(), new PaletteFeature());
        var (profile, secrets) = TestPg.Profile();
        var shell = new FakeShell();
        var ws = new WorkspaceViewModel(profile, secrets, database, shell);
        var vm = SidebarFeature.For(ws);
        await ws.ConnectAsync();
        return (ws, vm, shell);
    }

    /// <summary>Waits for the first server refresh and the hint check that follows it.</summary>
    private static async Task SettleAsync(WorkspaceViewModel ws, SidebarViewModel vm, int checksBefore = 0)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        // A refresh that is still finishing (saving the cache) would be joined by the next RefreshAsync: wait it out too.
        while (DateTime.UtcNow < deadline && (ws.Metadata.LastRefreshed is null || ws.Metadata.IsRefreshing || vm.EmptyHintChecks <= checksBefore))
            await Task.Delay(25);
        Assert.True(vm.EmptyHintChecks > checksBefore, "the hint check never ran");
        await vm.EmptyHintTask.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Empty_maintenance_database_shows_hint_with_other_databases_and_switches_on_click()
    {
        if (!TestPg.Available(output)) return;
        var (ws, vm, shell) = await ConnectAsync("postgres");
        try
        {
            Assert.False(vm.ShowEmptyDatabaseHint);
            await SettleAsync(ws, vm);
            Assert.True(vm.ShowEmptyDatabaseHint);
            Assert.Equal("No tables in database “postgres”.", vm.EmptyDatabaseMessage);
            Assert.False(vm.DatabaseListUnavailable);
            Assert.Contains("tableplsu_test", vm.OtherDatabases);
            Assert.DoesNotContain("postgres", vm.OtherDatabases);
            Assert.DoesNotContain("template0", vm.OtherDatabases);
            Assert.Equal(vm.OtherDatabases.Order(StringComparer.Ordinal), vm.OtherDatabases);
            Assert.True(vm.OtherDatabases.Count <= SidebarViewModel.MaxHintDatabases);

            vm.OpenOtherDatabase("tableplsu_test");
            Assert.Equal([("tableplsu_test", false)], shell.Opened);
        }
        finally
        {
            await ws.CloseAsync();
        }
    }

    [Fact]
    public async Task Database_with_tables_shows_no_hint()
    {
        if (!TestPg.Available(output)) return;
        var (ws, vm, _) = await ConnectAsync("tableplsu_test");
        try
        {
            await SettleAsync(ws, vm);
            Assert.False(vm.ShowEmptyDatabaseHint);
            Assert.Empty(vm.OtherDatabases);
        }
        finally
        {
            await ws.CloseAsync();
        }
    }

    [Fact]
    public async Task List_failure_shows_text_only_and_more_than_the_cap_offers_the_palette()
    {
        if (!TestPg.Available(output)) return;
        var old = SidebarViewModel.ListDatabases;
        var (ws, vm, _) = await ConnectAsync("postgres");
        try
        {
            SidebarViewModel.ListDatabases = (_, _) => throw new PostgresException("permission denied for table pg_database", "ERROR", "ERROR", "42501");
            await SettleAsync(ws, vm);
            var checks = vm.EmptyHintChecks;
            await ws.RefreshMetadata(); // re-check with the failing list
            await SettleAsync(ws, vm, checks);
            Assert.True(vm.ShowEmptyDatabaseHint);
            Assert.True(vm.DatabaseListUnavailable);
            Assert.Empty(vm.OtherDatabases);
            Assert.False(vm.HasMoreDatabases);

            var many = Enumerable.Range(0, 30).Select(i => $"db{i:00}").Append("postgres").ToList();
            SidebarViewModel.ListDatabases = (_, _) => Task.FromResult<IReadOnlyList<string>>(many);
            checks = vm.EmptyHintChecks;
            await ws.RefreshMetadata();
            await SettleAsync(ws, vm, checks);
            Assert.False(vm.DatabaseListUnavailable);
            Assert.Equal(SidebarViewModel.MaxHintDatabases, vm.OtherDatabases.Count);
            Assert.Equal("db00", vm.OtherDatabases[0]);
            Assert.True(vm.HasMoreDatabases);
        }
        finally
        {
            SidebarViewModel.ListDatabases = old;
            await ws.CloseAsync();
        }
    }

    [Fact]
    public async Task Hint_goes_away_once_a_table_exists_even_in_a_schema_not_loaded_yet()
    {
        if (!TestPg.Available(output)) return;
        var db = "tableplsu_hint_" + Guid.NewGuid().ToString("N")[..8];
        await ExecAsync(TestPg.ConnectionString!, $"create database {db}");
        WorkspaceViewModel? ws = null;
        try
        {
            (ws, var vm, _) = await ConnectAsync(db);
            await SettleAsync(ws, vm);
            Assert.True(vm.ShowEmptyDatabaseHint);

            await using (var conn = await ws.Session.OpenConnectionAsync())
            {
                await using var cmd = new NpgsqlCommand("create schema zz; create table zz.t(id int)", conn);
                await cmd.ExecuteNonQueryAsync();
            }
            var checks = vm.EmptyHintChecks;
            await ws.RefreshMetadata();
            await SettleAsync(ws, vm, checks);
            Assert.False(vm.ShowEmptyDatabaseHint);
            Assert.Empty(vm.OtherDatabases);
        }
        finally
        {
            if (ws is not null) await ws.CloseAsync();
            NpgsqlConnection.ClearAllPools();
            await ExecAsync(TestPg.ConnectionString!, $"drop database if exists {db} with (force)");
        }
    }

    [Fact]
    public async Task View_swaps_the_tree_for_the_hint_panel_and_a_click_switches_database()
    {
        if (!TestPg.Available(output)) return;
        var (ws, vm, shell) = await ConnectAsync("postgres");
        try
        {
            await SettleAsync(ws, vm);
            Assert.True(vm.ShowEmptyDatabaseHint);
            Exception? error = null;
            var t = new Thread(() =>
            {
                try
                {
                    var view = (SidebarView)AppServices.Features.Panels.Create(WorkspacePanel.Sidebar, ws)!;
                    var window = new Window
                    {
                        Width = 320, Height = 700, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false,
                        WindowStartupLocation = WindowStartupLocation.Manual, Content = view,
                    };
                    ThemeApplier.ApplyTo(window.Resources, TablePlsu.Core.Themes.ThemeService.BuiltInDark);
                    window.Show();
                    DoEvents();
                    Assert.Equal(Visibility.Collapsed, ((ListBox)view.FindName("ItemsList")).Visibility);
                    Assert.Equal(Visibility.Visible, ((FrameworkElement)view.FindName("EmptyHintPanel")).Visibility);
                    var list = (ItemsControl)view.FindName("OtherDatabasesList");
                    Assert.Equal(vm.OtherDatabases.Count, list.Items.Count);
                    var index = vm.OtherDatabases.IndexOf("tableplsu_test");
                    var button = FindChild<Button>(list.ItemContainerGenerator.ContainerFromIndex(index))!;
                    button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    Assert.Equal([("tableplsu_test", false)], shell.Opened);

                    // History tab hides the hint panel.
                    vm.SelectedTabIndex = SidebarViewModel.HistoryTab;
                    DoEvents();
                    Assert.Equal(Visibility.Collapsed, ((FrameworkElement)view.FindName("EmptyHintPanel")).Visibility);
                    window.Close();
                }
                catch (Exception ex) { error = ex; }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (error is not null) throw new Xunit.Sdk.XunitException("STA body failed: " + error);
        }
        finally
        {
            await ws.CloseAsync();
        }
    }

    private static void DoEvents() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static T? FindChild<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null) return null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) return t;
            if (FindChild<T>(child) is { } found) return found;
        }
        return null;
    }

    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var c = new NpgsqlConnection(connectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }
}
