using TablePlsu.App.Features.Workspace;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Commands;
using TablePlsu.Core.Connections;
using TablePlsu.Core.Db;
using Xunit.Abstractions;

namespace TablePlsu.App.Tests;

[Collection(AppServicesCollection.Name)]
public class WorkspaceViewModelTests
{
    private static WorkspaceViewModel NewWorkspace(bool withSqlEditor = true)
    {
        TestApp.Init(new WorkspaceFeature(), new FakeTabsFeature(withSqlEditor));
        return new WorkspaceViewModel(TestApp.Profile(), ConnectionSecrets.None, null, shell: null);
    }

    private static TabRequest Table(string name, bool pinned = false) => TabRequest.ForObject("public", name, ObjectKinds.Table, pinned);

    [Fact]
    public void Starts_disconnected_with_profile_database()
    {
        var ws = NewWorkspace();
        Assert.Equal(DbSessionState.Disconnected, ws.State);
        Assert.Equal("db", ws.Database);
        Assert.False(ws.HasTabs);
        Assert.True(ws.IsSidebarVisible);
        Assert.False(ws.IsConsoleVisible);
    }

    [Fact]
    public void Single_click_replaces_clean_preview_tab_and_double_click_pins()
    {
        var ws = NewWorkspace();
        var a = (FakeTab)ws.OpenTab(Table("a"))!;
        Assert.True(a.IsPreview);
        var b = ws.OpenTab(Table("b"))!;
        Assert.Single(ws.Tabs);
        Assert.Same(b, ws.ActiveTab);
        Assert.Equal([TabCloseReason.Replaced], a.ClosedReasons);

        // Double-click on the open item pins it; the next single click opens a new tab.
        Assert.Same(b, ws.OpenTab(Table("b", pinned: true)));
        Assert.False(b.IsPreview);
        ws.OpenTab(Table("c"));
        Assert.Equal(2, ws.Tabs.Count);
    }

    [Fact]
    public void Tab_with_pending_changes_is_kept()
    {
        var ws = NewWorkspace();
        var a = ws.OpenTab(Table("a"))!;
        a.HasPendingChanges = true;
        ws.OpenTab(Table("b"));
        Assert.Equal(2, ws.Tabs.Count);
        Assert.Contains(a, ws.Tabs);
    }

    [Fact]
    public void New_tab_opens_sql_editor_or_falls_back_to_empty()
    {
        var ws = NewWorkspace(withSqlEditor: true);
        Assert.Equal(TabKinds.SqlEditor, ws.NewTab().Kind);

        var ws2 = NewWorkspace(withSqlEditor: false);
        Assert.Equal(TabKinds.Empty, ws2.NewTab().Kind);
        Assert.Equal(TabKinds.Empty, ws2.NewSqlEditor().Kind);
    }

    [Fact]
    public void Close_others_right_and_all()
    {
        var ws = NewWorkspace();
        var tabs = Enumerable.Range(1, 5).Select(_ => ws.NewSqlEditor()).ToList();
        ws.CloseToTheRight(tabs[2]);
        Assert.Equal(tabs.Take(3), ws.Tabs);
        ws.CloseOthers(tabs[1]);
        Assert.Equal([tabs[1]], ws.Tabs);
        Assert.Same(tabs[1], ws.ActiveTab);
        ws.CloseAll();
        Assert.Empty(ws.Tabs);
        Assert.Null(ws.ActiveTab);
    }

    [Fact]
    public void Closing_active_tab_activates_right_neighbour_then_left()
    {
        var ws = NewWorkspace();
        var t1 = ws.NewSqlEditor();
        var t2 = ws.NewSqlEditor();
        var t3 = ws.NewSqlEditor();
        ws.ActivateTab(t2);
        ws.CloseActiveTab();
        Assert.Same(t3, ws.ActiveTab);
        ws.CloseActiveTab();
        Assert.Same(t1, ws.ActiveTab);
    }

    [Fact]
    public void Tab_can_refuse_to_close()
    {
        var ws = NewWorkspace();
        var t = (FakeTab)ws.NewSqlEditor();
        t.RefuseClose = true;
        Assert.False(ws.CloseTab(t));
        Assert.Single(ws.Tabs);
    }

    [Fact]
    public void New_tabs_insert_after_active()
    {
        var ws = NewWorkspace();
        var t1 = ws.NewSqlEditor();
        var t2 = ws.NewSqlEditor();
        ws.ActivateTab(t1);
        var t3 = ws.NewSqlEditor();
        Assert.Equal([t1, t3, t2], ws.Tabs);
    }

    [Fact]
    public void Keyboard_tab_commands_cycle_and_jump()
    {
        var ws = NewWorkspace();
        var tabs = Enumerable.Range(1, 3).Select(_ => ws.NewSqlEditor()).ToList();
        Assert.True(ws.ExecuteCommand(CommandIds.TabNext));
        Assert.Same(tabs[0], ws.ActiveTab); // wraps from last
        Assert.True(ws.ExecuteCommand(CommandIds.TabPrev));
        Assert.Same(tabs[2], ws.ActiveTab);
        Assert.True(ws.ExecuteCommand(CommandIds.TabGoto2));
        Assert.Same(tabs[1], ws.ActiveTab);
        Assert.True(ws.ExecuteCommand(CommandIds.TabGoto9)); // out of range: no-op
        Assert.Same(tabs[1], ws.ActiveTab);
        Assert.True(ws.ExecuteCommand(CommandIds.TabClose));
        Assert.Equal(2, ws.Tabs.Count);
    }

    [Fact]
    public void Layout_toggles_and_reload()
    {
        var ws = NewWorkspace();
        var t = (FakeTab)ws.NewSqlEditor();
        var reloads = 0;
        ws.ReloadRequested += (_, _) => reloads++;
        ws.ExecuteCommand(CommandIds.SidebarToggle);
        ws.ExecuteCommand(CommandIds.ConsoleToggle);
        ws.ExecuteCommand(CommandIds.WorkspaceReload);
        ws.ExecuteCommand(CommandIds.TabReload);
        Assert.False(ws.IsSidebarVisible);
        Assert.True(ws.IsConsoleVisible);
        Assert.Equal(1, reloads);
        Assert.Equal(2, t.ReloadCount);
    }

    [Fact]
    public void Ctrl_s_commits_when_pending_else_saves()
    {
        var ws = NewWorkspace();
        var t = (FakeTab)ws.NewSqlEditor();
        ws.ExecuteCommand(CommandIds.ChangesCommit);
        Assert.Equal((1, 0), (t.SaveCount, t.CommitCount));
        Assert.False(ws.ExecuteCommand(CommandIds.ChangesPreview)); // nothing pending
        t.HasPendingChanges = true;
        ws.ExecuteCommand(CommandIds.ChangesCommit);
        Assert.Equal((1, 1), (t.SaveCount, t.CommitCount));
    }

    [Fact]
    public void Busy_follows_tabs()
    {
        var ws = NewWorkspace();
        var t = ws.NewSqlEditor();
        t.IsBusy = true;
        Assert.True(ws.IsBusy);
        t.IsBusy = false;
        Assert.False(ws.IsBusy);
    }

    [Fact]
    public void Workspace_opened_hook_and_safe_mode_factory_are_used()
    {
        IWorkspace? opened = null;
        TestApp.Init(new WorkspaceFeature(), new HookFeature(ws => opened = ws));
        var ws = new WorkspaceViewModel(TestApp.Profile(), ConnectionSecrets.None, null, null);
        AppServices.Features.RaiseWorkspaceOpened(ws);
        Assert.Same(ws, opened);
        Assert.IsType<AlwaysNoGate>(ws.SafeMode);
    }

    [Fact]
    public async Task Close_closes_tabs_with_workspace_reason_and_disposes_services()
    {
        var ws = NewWorkspace();
        var t = (FakeTab)ws.NewSqlEditor();
        var closing = 0;
        ws.Closing += (_, _) => closing++;
        var svc = ws.Services.GetOrAdd(() => new Flag());
        await ws.CloseAsync();
        Assert.Equal([TabCloseReason.WorkspaceClosing], t.ClosedReasons);
        Assert.Equal(1, closing);
        Assert.True(svc.Disposed);
        Assert.Empty(ws.Tabs);
    }

    [Fact]
    public void Console_log_keeps_last_1000_entries()
    {
        var ws = NewWorkspace();
        for (var i = 0; i < 1005; i++) ws.Log("line " + i);
        Assert.Equal(WorkspaceLog.Capacity, ws.ConsoleLog.Entries.Count);
        Assert.Equal("line 5", ws.ConsoleLog.Entries[0].Message);
    }

    private sealed class Flag : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class HookFeature(Action<IWorkspace> onOpened) : IFeature
    {
        public void Register(FeatureContext ctx)
        {
            ctx.OnWorkspaceOpened(onOpened);
            ctx.UseSafeMode(_ => new AlwaysNoGate());
        }
    }

    private sealed class AlwaysNoGate : ISafeModeGate
    {
        public SafeModeLevel Level => SafeModeLevel.Alert1;
        public event EventHandler? LevelChanged { add { } remove { } }
        public Task<bool> ConfirmAsync(IReadOnlyList<string> statements, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> ChangeLevelAsync(SafeModeLevel level) => Task.FromResult(false);
    }
}

[Collection(AppServicesCollection.Name)]
public class FeatureRegistrationTests
{
    [Fact]
    public void All_features_register_and_the_shell_handles_its_commands()
    {
        TestApp.Init(TablePlsu.App.Infrastructure.Features.CreateAll().ToArray());
        var registered = AppServices.Commands.RegisteredIds;
        foreach (var id in new[]
                 {
                     CommandIds.TabNew, CommandIds.TabClose, CommandIds.TabNext, CommandIds.TabPrev, CommandIds.TabGoto1, CommandIds.TabGoto9,
                     CommandIds.TabReload, CommandIds.EditorNew, CommandIds.WorkspaceReload, CommandIds.WorkspaceReconnect,
                     CommandIds.WorkspaceDisconnect, CommandIds.SidebarToggle, CommandIds.ConsoleToggle, CommandIds.ChangesCommit,
                     CommandIds.ChangesPreview, CommandIds.ChangesDiscard, CommandIds.WelcomeSearch, CommandIds.WindowNew,
                 })
        {
            Assert.Contains(id, registered);
        }
        foreach (var id in ToolbarItemIds.Known.Where(i => i != ToolbarItemIds.Notifications))
            Assert.NotNull(AppServices.Features.Toolbar.Get(id));
        Assert.True(AppServices.Features.Tabs.IsRegistered(TabKinds.Empty));
    }

    [Fact]
    public void Init_loads_settings_and_keymap_from_temp_root()
    {
        var root = TestApp.Init();
        Assert.StartsWith(root, AppServices.Paths.Roaming);
        Assert.Equal("Ctrl+T", AppServices.Keymap.GetBindingText(CommandIds.TabNew));
        Assert.Equal("New Tab (Ctrl+T)", AppServices.Shortcuts[CommandIds.TabNew]);
        Assert.Equal("Ctrl+T", AppServices.Gestures[CommandIds.TabNew]);
    }

    [Fact]
    public void Keymap_overrides_apply_on_settings_change()
    {
        TestApp.Init();
        AppServices.Settings.SaveUserJson("""{ "keymap": { "tab.new": "Ctrl+Alt+T" } }""");
        AppServices.Settings.Reload();
        AppServices.ApplySettings(new Core.Settings.SettingsChangedEventArgs(AppServices.Settings.Current, null));
        Assert.Equal("Ctrl+Alt+T", AppServices.Gestures[CommandIds.TabNew]);
    }
}

[Collection(AppServicesCollection.Name)]
public class WorkspaceConnectionIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Connects_in_background_and_reports_state()
    {
        if (!TestPg.Available(output)) return;
        TestApp.Init();
        var (profile, secrets) = TestPg.Profile();
        var ws = new WorkspaceViewModel(profile, secrets, null, null);
        var states = new List<DbSessionState>();
        ws.StateChanged += (_, s) => states.Add(s);
        try
        {
            var connect = ws.ConnectAsync();
            Assert.Equal(DbSessionState.Connecting, ws.State);
            Assert.Equal("connecting…", ws.StatusText);
            await connect;
            Assert.Equal(DbSessionState.Connected, ws.State);
            Assert.Equal("connected", ws.StatusText);
            Assert.False(string.IsNullOrEmpty(ws.ServerVersion));
            Assert.Contains(DbSessionState.Connected, states);

            await ws.ReconnectAsync();
            Assert.Equal(DbSessionState.Connected, ws.State);
        }
        finally
        {
            await ws.CloseAsync();
        }
    }

    [Fact]
    public async Task Connect_refreshes_metadata_cache_and_writes_it_to_disk()
    {
        if (!TestPg.Available(output)) return;
        TestApp.Init();
        var (profile, secrets) = TestPg.Profile();
        var ws = new WorkspaceViewModel(profile, secrets, null, null);
        var refreshed = new TaskCompletionSource();
        ws.Metadata.Changed += (_, e) => { if (e.Reason == TablePlsu.Core.Metadata.MetadataChangeReason.Refreshed) refreshed.TrySetResult(); };
        try
        {
            await ws.ConnectAsync();
            await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.NotNull(ws.Metadata.FindTable(null, "customers"));
            for (var i = 0; i < 50 && !System.IO.File.Exists(ws.Metadata.CacheFile); i++) await Task.Delay(50); // saved right after Changed
            Assert.True(System.IO.File.Exists(ws.Metadata.CacheFile));
            IWorkspace iws = ws;
            Assert.Same(ws.Metadata, iws.Metadata);
        }
        finally
        {
            await ws.CloseAsync();
        }
    }

    [Fact]
    public async Task Bad_password_marks_workspace_broken_with_error()
    {
        if (!TestPg.Available(output)) return;
        TestApp.Init();
        var (profile, _) = TestPg.Profile();
        var ws = new WorkspaceViewModel(profile with { User = "no_such_user_xyz" }, new ConnectionSecrets("wrong", null, null), null, null);
        try
        {
            await ws.ConnectAsync();
            Assert.Equal(DbSessionState.Broken, ws.State);
            Assert.True(ws.CanReconnectByClick);
            Assert.False(string.IsNullOrEmpty(ws.LastError));
            Assert.Contains(ws.ConsoleLog.Entries, e => e.Kind == ConsoleEntryKind.Error);
        }
        finally
        {
            await ws.CloseAsync();
        }
    }

    [Fact]
    public async Task Connection_tester_returns_server_version()
    {
        if (!TestPg.Available(output)) return;
        var (profile, secrets) = TestPg.Profile();
        var version = await Features.Welcome.ConnectionTester.TestAsync(profile, secrets);
        output.WriteLine(version);
        Assert.Matches(@"^\d+", version);
    }
}
