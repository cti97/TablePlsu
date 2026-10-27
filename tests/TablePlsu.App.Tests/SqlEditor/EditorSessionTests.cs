using System.IO;
using TablePlsu.App.Features.SqlEditor;
using TablePlsu.App.Features.Workspace;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Connections;
using TablePlsu.Core.Editors;

namespace TablePlsu.App.Tests.SqlEditor;

// These tests stay synchronous on purpose: the AvalonEdit TextDocument may only be used by the thread that created it.
#pragma warning disable xUnit1031

[Collection(AppServicesCollection.Name)]
public class EditorSessionTests
{
    private static readonly ConnectionProfile Profile = TestApp.Profile() with { Id = "sess" + Guid.NewGuid().ToString("N")[..8] };

    private static WorkspaceViewModel Open(ConnectionProfile? profile = null)
    {
        var ws = new WorkspaceViewModel(profile ?? Profile, ConnectionSecrets.None, null, shell: null);
        AppServices.Features.RaiseWorkspaceOpened(ws);
        return ws;
    }

    private static void Init() => TestApp.Init(new WorkspaceFeature(), new SqlEditorFeature());

    private static string EditorDir(ConnectionProfile p) => AppServices.Paths.EditorDir(p.Id);

    [Fact]
    public void New_tab_opens_numbered_sql_editors()
    {
        Init();
        var ws = Open();
        var a = Assert.IsType<SqlEditorTabViewModel>(ws.NewTab());
        var b = Assert.IsType<SqlEditorTabViewModel>(ws.NewSqlEditor());
        Assert.Equal("Query 1", a.Title);
        Assert.Equal("Query 2", b.Title);
        var c = Assert.IsType<SqlEditorTabViewModel>(ws.OpenTab(TabRequest.SqlEditor("select 42", "From history")));
        Assert.Equal("From history", c.Title);
        Assert.Equal("select 42", c.Document.Text);
        ws.CloseAsync().Wait();
    }

    [Fact]
    public void Text_and_session_survive_workspace_close_and_restore_in_order()
    {
        Init();
        var ws = Open();
        var a = (SqlEditorTabViewModel)ws.NewTab();
        var b = (SqlEditorTabViewModel)ws.NewTab();
        var c = (SqlEditorTabViewModel)ws.NewTab();
        a.Document.Text = "select 'a';";
        b.Document.Text = "select 'b';\nselect 2;";
        c.Document.Text = "select 'c';";
        b.CustomTitle = "Bee";
        b.SetSelection(caret: 12, selectionStart: 12, selectionLength: 6);
        ws.ActivateTab(b);
        ws.CloseAsync().Wait();

        Assert.Equal("select 'b';\nselect 2;", File.ReadAllText(Path.Combine(EditorDir(Profile), b.Id + ".sql")));

        // A new workspace for the same connection restores every tab (text before any connection).
        var ws2 = Open();
        var tabs = ws2.Tabs.Cast<SqlEditorTabViewModel>().ToList();
        Assert.Equal([a.Id, b.Id, c.Id], tabs.Select(t => t.Id));
        Assert.Equal(["select 'a';", "select 'b';\nselect 2;", "select 'c';"], tabs.Select(t => t.Document.Text));
        var b2 = tabs[1];
        Assert.Same(b2, ws2.ActiveTab);
        Assert.Equal("Bee", b2.DisplayTitle);
        Assert.Equal("Query 2", b2.Title);
        Assert.Equal((12, 12, 6), (b2.CaretOffset, b2.SelectionStart, b2.SelectionLength));
        Assert.Equal("Query 4", ((SqlEditorTabViewModel)ws2.NewTab()).Title);
        ws2.CloseAsync().Wait();
    }

    [Fact]
    public void Closed_tab_goes_to_recently_closed_and_reopens()
    {
        Init();
        var profile = Profile with { Id = "rc" + Guid.NewGuid().ToString("N")[..8] };
        var ws = Open(profile);
        var keep = (SqlEditorTabViewModel)ws.NewTab();
        var gone = (SqlEditorTabViewModel)ws.NewTab();
        keep.Document.Text = "select 1;";
        gone.Document.Text = "select 'closed';";
        Assert.True(ws.CloseTab(gone));
        Assert.Single(ws.Tabs);

        var manager = EditorSessionManager.For(ws);
        manager.FlushNow();
        Assert.Equal(gone.Id, manager.Store.RecentlyClosed[0].Id);

        var reopened = manager.ReopenLast()!;
        Assert.Equal(gone.Id, reopened.Id);
        Assert.Equal("select 'closed';", reopened.Document.Text);
        Assert.Equal(gone.Title, reopened.Title);
        Assert.Same(reopened, ws.ActiveTab);
        Assert.Equal(2, ws.Tabs.Count);
        Assert.Null(manager.ReopenLast());
        ws.CloseAsync().Wait();

        // Closing the workspace does not send tabs to "recently closed".
        var store = new EditorSessionStore(AppServices.Paths, profile.Id);
        Assert.Equal(2, store.Load().Tabs.Count);
        Assert.Empty(store.RecentlyClosed);
    }

    [Fact]
    public void Open_file_links_tab_and_save_writes_the_file()
    {
        Init();
        var dir = Path.Combine(Path.GetTempPath(), "tableplsu-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "report.sql");
        File.WriteAllText(file, "select * from report;");
        var ws = Open();
        var tab = EditorSessionManager.For(ws).OpenFile(file);
        Assert.Equal("report.sql", tab.Title);
        Assert.Equal(file, tab.LinkedFilePath);
        Assert.Same(tab, EditorSessionManager.For(ws).OpenFile(file));

        tab.Document.Text = "select 1 from report;";
        tab.Save();
        Assert.Equal("select 1 from report;", File.ReadAllText(file));

        var other = Path.Combine(dir, "copy.sql");
        tab.SaveAs(other);
        Assert.Equal("copy.sql", tab.Title);
        Assert.Equal(other, tab.LinkedFilePath);
        Assert.Equal("select 1 from report;", File.ReadAllText(other));
        ws.CloseAsync().Wait();
    }

    [Fact]
    public void Row_limit_is_shared_by_tabs_and_remembered_per_connection()
    {
        Init();
        var profile = TestApp.Profile() with { Id = "rl" + Guid.NewGuid().ToString("N")[..8] };
        AppServices.Connections.Save(profile);
        var ws = Open(profile);
        var a = (SqlEditorTabViewModel)ws.NewTab();
        var b = (SqlEditorTabViewModel)ws.NewTab();
        Assert.Equal(1000, a.RowLimit);
        a.RowLimit = 5000;
        Assert.Equal(5000, b.RowLimit);
        Assert.Equal(5000, AppServices.Connections.Get(profile.Id)!.RowLimit);
        ws.CloseAsync().Wait();

        var ws2 = Open(AppServices.Connections.Get(profile.Id)!);
        Assert.Equal(5000, ((SqlEditorTabViewModel)ws2.NewTab()).RowLimit);
        ws2.CloseAsync().Wait();
    }

    [Fact]
    public void Same_connection_in_two_windows_loses_no_tab_and_never_restores_one_twice()
    {
        Init();
        var profile = Profile with { Id = "two" + Guid.NewGuid().ToString("N")[..8] };
        var first = Open(profile);
        var a = (SqlEditorTabViewModel)first.NewTab();
        a.Document.Text = "select 'window 1';";
        EditorSessionManager.For(first).FlushNow();

        // Second window on the same connection (Ctrl+K "open in new window", New Window…).
        var second = Open(profile);
        Assert.DoesNotContain(second.Tabs.OfType<SqlEditorTabViewModel>(), t => t.Id == a.Id);
        var b = (SqlEditorTabViewModel)second.NewTab();
        b.Document.Text = "select 'window 2';";
        EditorSessionManager.For(second).FlushNow();
        EditorSessionManager.For(first).FlushNow();
        second.CloseAsync().Wait();
        first.CloseAsync().Wait();

        var again = Open(profile);
        var texts = again.Tabs.OfType<SqlEditorTabViewModel>().Select(t => t.Document.Text).Order().ToList();
        Assert.Equal(["select 'window 1';", "select 'window 2';"], texts);
        again.CloseAsync().Wait();
    }

    [Fact]
    public void FlushAll_writes_text_still_inside_the_editor_debounce()
    {
        Init();
        var previous = UiDispatch.Dispatcher;
        UiDispatch.Initialize(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        try
        {
            var profile = Profile with { Id = "crash" + Guid.NewGuid().ToString("N")[..8] };
            var ws = Open(profile);
            var tab = (SqlEditorTabViewModel)ws.NewTab();
            tab.Document.Text = "select 'typed 100 ms before the crash';"; // the 500 ms debounce timer has not fired
            EditorSessionManager.FlushAll();
            Assert.Equal("select 'typed 100 ms before the crash';", File.ReadAllText(Path.Combine(EditorDir(profile), tab.Id + ".sql")));
            var store = new EditorSessionStore(AppServices.Paths, profile.Id);
            Assert.Contains(store.Load().Tabs, t => t.Id == tab.Id);
            ws.CloseAsync().Wait();
        }
        finally
        {
            UiDispatch.Initialize(previous!);
        }
    }

    [Fact]
    public void Restore_first_mode_keeps_the_other_tabs_on_disk()
    {
        Init();
        var profile = Profile with { Id = "first" + Guid.NewGuid().ToString("N")[..8] };
        var ws = Open(profile);
        ((SqlEditorTabViewModel)ws.NewTab()).Document.Text = "one";
        ((SqlEditorTabViewModel)ws.NewTab()).Document.Text = "two";
        ws.CloseAsync().Wait();

        AppServices.Settings.SetValue("editor.autoSaveTabs", System.Text.Json.Nodes.JsonValue.Create("first"));
        Assert.Equal(TablePlsu.Core.Settings.AutoSaveTabs.First, AppServices.Current.Editor.AutoSaveTabs);
        var ws2 = Open(profile);
        Assert.Single(ws2.Tabs);
        ws2.CloseAsync().Wait();

        Assert.Equal(2, new EditorSessionStore(AppServices.Paths, profile.Id).LoadTabs().Count);
    }

    [Fact]
    public void Run_without_connection_shows_a_message_tab()
    {
        Init();
        var ws = Open();
        var tab = (SqlEditorTabViewModel)ws.NewTab();
        tab.Document.Text = "select 1";
        tab.RunCurrentAsync().Wait();
        var result = Assert.Single(tab.Results);
        Assert.True(result.ShowMessageView);
        Assert.Contains("Not connected", result.MessagesText);
        Assert.False(tab.IsRunning);
        ws.CloseAsync().Wait();
    }
}
