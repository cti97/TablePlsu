using TablePlsu.Core.Editors;
using TablePlsu.Tests.Infrastructure;

namespace TablePlsu.Tests.Editors;

public class EditorSessionStoreTests
{
    private static EditorSessionStore Create(TempDir tmp, int debounceMs = 500, List<string>? log = null) =>
        new(tmp.Paths, "conn1", TimeSpan.FromMilliseconds(debounceMs), log is null ? null : log.Add);

    [Fact]
    public void SaveText_is_debounced_then_written()
    {
        using var tmp = new TempDir();
        using var store = Create(tmp, debounceMs: 150);
        store.SaveText("t1", "select 1");
        store.SaveText("t1", "select 12");
        var file = Path.Combine(tmp.Paths.EditorDir("conn1"), "t1.sql");
        Assert.False(File.Exists(file)); // not yet

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(file) && DateTime.UtcNow < deadline) Thread.Sleep(20);
        Assert.Equal("select 12", File.ReadAllText(file));
    }

    [Fact]
    public void Flush_writes_pending_immediately()
    {
        using var tmp = new TempDir();
        using var store = Create(tmp, debounceMs: 60_000);
        store.SaveText("t1", "abc");
        store.SaveText("t2", "def");
        store.Flush();
        Assert.Equal("abc", store.ReadText("t1"));
        Assert.Equal("def", store.ReadText("t2"));
    }

    [Fact]
    public void ReadText_returns_pending_text_before_flush()
    {
        using var tmp = new TempDir();
        using var store = Create(tmp, debounceMs: 60_000);
        store.SaveText("t1", "pending");
        Assert.Equal("pending", store.ReadText("t1"));
        Assert.Null(store.ReadText("nope"));
    }

    [Fact]
    public void Dispose_flushes()
    {
        using var tmp = new TempDir();
        var store = Create(tmp, debounceMs: 60_000);
        store.SaveText("t1", "on exit");
        store.Dispose();
        Assert.Equal("on exit", File.ReadAllText(Path.Combine(tmp.Paths.EditorDir("conn1"), "t1.sql")));
    }

    [Fact]
    public void Session_round_trips_with_texts()
    {
        using var tmp = new TempDir();
        using (var store = Create(tmp))
        {
            store.SaveText("a", "select a");
            store.SaveText("b", "select b");
            store.SaveSession(new EditorSession
            {
                Tabs =
                [
                    new EditorTabState { Id = "a", Title = "Query 1", CaretOffset = 3, SelectionStart = 1, SelectionLength = 2, ScrollOffsetY = 40.5 },
                    new EditorTabState { Id = "b", Title = "Query 2", CustomTitle = "Mine", LinkedFilePath = @"C:\x.sql" },
                ],
                ActiveTabId = "b",
            });
        }

        using var again = Create(tmp);
        var session = again.Load();
        Assert.Equal(new[] { "a", "b" }, session.Tabs.Select(t => t.Id));
        Assert.Equal("b", session.ActiveTabId);
        Assert.Equal(3, session.Tabs[0].CaretOffset);
        Assert.Equal(1, session.Tabs[0].SelectionStart);
        Assert.Equal(2, session.Tabs[0].SelectionLength);
        Assert.Equal(40.5, session.Tabs[0].ScrollOffsetY);
        Assert.Equal("Mine", session.Tabs[1].CustomTitle);
        Assert.Equal(@"C:\x.sql", session.Tabs[1].LinkedFilePath);

        var restored = again.LoadTabs();
        Assert.Equal(new[] { "select a", "select b" }, restored.Select(r => r.Text));
    }

    [Fact]
    public void Load_without_files_returns_empty_session()
    {
        using var tmp = new TempDir();
        using var store = Create(tmp);
        var s = store.Load();
        Assert.Empty(s.Tabs);
        Assert.Null(s.ActiveTabId);
        Assert.Empty(store.RecentlyClosed);
    }

    [Fact]
    public void Corrupt_session_json_does_not_throw_and_logs()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.Paths.EditorDir("conn1"));
        File.WriteAllText(Path.Combine(tmp.Paths.EditorDir("conn1"), "session.json"), "{ nope");
        var log = new List<string>();
        using var store = Create(tmp, log: log);
        var s = store.Load();
        Assert.Empty(s.Tabs);
        Assert.NotEmpty(log);
    }

    [Fact]
    public void Missing_tab_file_restores_as_empty_text()
    {
        using var tmp = new TempDir();
        using var store = Create(tmp);
        store.SaveSession(new EditorSession { Tabs = [new EditorTabState { Id = "gone" }] });
        var tabs = store.LoadTabs();
        Assert.Equal("", Assert.Single(tabs).Text);
    }

    [Fact]
    public void CloseTab_moves_to_recently_closed_and_ReopenLast_restores_text()
    {
        using var tmp = new TempDir();
        using var store = Create(tmp);
        store.SaveText("a", "select a");
        store.SaveText("b", "select b");
        store.SaveSession(new EditorSession { Tabs = [new EditorTabState { Id = "a", Title = "A" }, new EditorTabState { Id = "b", Title = "B" }], ActiveTabId = "a" });

        store.CloseTab("a");
        Assert.Equal(new[] { "b" }, store.Load().Tabs.Select(t => t.Id));
        var closed = Assert.Single(store.RecentlyClosed);
        Assert.Equal("a", closed.Id);
        Assert.Equal("A", closed.Title);

        var reopened = store.ReopenLast();
        Assert.NotNull(reopened);
        Assert.Equal("a", reopened!.Value.State.Id);
        Assert.Equal("select a", reopened.Value.Text);
        Assert.Empty(store.RecentlyClosed);
        Assert.Null(store.ReopenLast());
    }

    [Fact]
    public void Recently_closed_is_capped_at_20_and_old_files_are_removed()
    {
        using var tmp = new TempDir();
        using var store = Create(tmp);
        var tabs = Enumerable.Range(0, 25).Select(i => new EditorTabState { Id = "t" + i }).ToList();
        foreach (var t in tabs) store.SaveText(t.Id, "x" + t.Id);
        store.SaveSession(new EditorSession { Tabs = tabs });
        foreach (var t in tabs) store.CloseTab(t.Id);
        store.Flush();

        Assert.Equal(EditorSessionStore.MaxRecentlyClosed, store.RecentlyClosed.Count);
        Assert.Equal("t24", store.RecentlyClosed[0].Id); // newest first
        Assert.False(File.Exists(Path.Combine(tmp.Paths.EditorDir("conn1"), "t0.sql")));
        Assert.True(File.Exists(Path.Combine(tmp.Paths.EditorDir("conn1"), "t24.sql")));

        using var again = Create(tmp);
        again.Load();
        Assert.Equal(20, again.RecentlyClosed.Count);
    }

    [Fact]
    public void Linked_file_receives_saved_text()
    {
        using var tmp = new TempDir();
        var linked = Path.Combine(tmp.Path, "query.sql");
        using var store = Create(tmp, debounceMs: 60_000);
        store.LinkFile("t1", linked);
        store.SaveText("t1", "select linked");
        store.Flush();
        Assert.Equal("select linked", File.ReadAllText(linked));
    }

    [Fact]
    public void Io_errors_are_logged_not_thrown()
    {
        using var tmp = new TempDir();
        var log = new List<string>();
        using var store = Create(tmp, debounceMs: 60_000, log: log);
        // A directory where the linked file should be makes the write fail.
        var linked = Path.Combine(tmp.Path, "dir.sql");
        Directory.CreateDirectory(linked);
        store.LinkFile("t1", linked);
        store.SaveText("t1", "x");
        store.Flush();
        Assert.NotEmpty(log);
        Assert.Equal("x", store.ReadText("t1"));
    }

    [Fact]
    public void Invalid_tab_ids_are_rejected_without_throwing_from_save()
    {
        using var tmp = new TempDir();
        var log = new List<string>();
        using var store = Create(tmp, debounceMs: 60_000, log: log);
        store.SaveText("..\\evil", "x");
        store.Flush();
        Assert.NotEmpty(log);
        Assert.False(File.Exists(Path.Combine(tmp.Paths.EditorsDir, "evil.sql")));
    }

    [Fact]
    public void NewTabId_is_unique_and_file_safe()
    {
        var a = EditorSessionStore.NewTabId();
        var b = EditorSessionStore.NewTabId();
        Assert.NotEqual(a, b);
        Assert.Equal(-1, a.IndexOfAny(Path.GetInvalidFileNameChars()));
    }
}
