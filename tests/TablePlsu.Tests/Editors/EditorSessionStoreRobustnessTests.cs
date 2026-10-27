using TablePlsu.Core.Editors;
using TablePlsu.Core.IO;
using TablePlsu.Tests.Infrastructure;

namespace TablePlsu.Tests.Editors;

/// <summary>Never-lose-a-query guarantees of <see cref="EditorSessionStore"/>: write ordering, failed writes, several
/// stores (windows) on one connection, crash leftovers.</summary>
public class EditorSessionStoreRobustnessTests
{
    private static EditorSessionStore Create(TempDir tmp, int debounceMs = 60_000, List<string>? log = null) =>
        new(tmp.Paths, "conn1", TimeSpan.FromMilliseconds(debounceMs), log is null ? null : m => { lock (log) log.Add(m); });

    private static string TabPath(TempDir tmp, string id) => Path.Combine(tmp.Paths.EditorDir("conn1"), id + ".sql");

    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs);
        return r.ReadToEnd();
    }

    /// <summary>Holds the file open without delete sharing: File.Replace on it fails (and AtomicFile retries) until released.</summary>
    private static FileStream Block(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    [Fact]
    public void Flush_waits_for_an_in_flight_debounced_write()
    {
        using var tmp = new TempDir();
        using var store = Create(tmp, debounceMs: 30);
        var file = TabPath(tmp, "t1");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "v0");
        var hold = Block(file);
        store.SaveText("t1", "v1");
        Thread.Sleep(200); // the debounce timer has taken "v1" and is retrying the replace
        _ = Task.Run(() => { Thread.Sleep(300); hold.Dispose(); });

        store.Flush();

        Assert.Equal("v1", ReadShared(file));
    }

    [Fact]
    public void A_slow_older_write_never_overwrites_newer_text()
    {
        using var tmp = new TempDir();
        using var store = Create(tmp, debounceMs: 30);
        var file = TabPath(tmp, "t1");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "v0");
        var hold = Block(file);
        store.SaveText("t1", "v1");
        Thread.Sleep(400); // the timer's write of "v1" is deep in its retry back-off
        store.SaveText("t1", "v2");
        var flush = Task.Run(store.Flush);
        Thread.Sleep(50);
        hold.Dispose();
        flush.Wait();
        Thread.Sleep(1200); // let any straggling write land

        Assert.Equal("v2", ReadShared(file));
    }

    [Fact]
    public void Failed_write_keeps_the_text_and_retries_on_the_next_flush()
    {
        using var tmp = new TempDir();
        var log = new List<string>();
        using var store = Create(tmp, log: log);
        var file = TabPath(tmp, "t1");
        Directory.CreateDirectory(file); // a directory where the tab file goes: the write fails
        store.SaveText("t1", "precious");
        store.Flush();
        Assert.NotEmpty(log);
        Assert.Equal("precious", store.ReadText("t1"));

        Directory.Delete(file);
        store.Flush();
        Assert.Equal("precious", File.ReadAllText(file));
    }

    [Fact]
    public void Two_stores_on_one_connection_keep_each_others_tabs()
    {
        using var tmp = new TempDir();
        using var a = Create(tmp);
        using var b = Create(tmp);
        a.SaveText("a1", "select 'a1'");
        a.SaveSession(new EditorSession { Tabs = [new EditorTabState { Id = "a1", Title = "A1" }], ActiveTabId = "a1" });
        b.SaveText("b1", "select 'b1'");
        b.SaveSession(new EditorSession { Tabs = [new EditorTabState { Id = "b1", Title = "B1" }], ActiveTabId = "b1" });
        a.SaveText("a2", "select 'a2'");
        a.SaveSession(new EditorSession { Tabs = [new EditorTabState { Id = "a1" }, new EditorTabState { Id = "a2" }], ActiveTabId = "a2" });
        a.Flush();
        b.Flush();

        using (var check = Create(tmp))
            Assert.Equal(["a1", "a2", "b1"], check.Load().Tabs.Select(t => t.Id).Order());

        // Closing a tab in one window removes only that tab.
        b.CloseTab("b1");
        a.SaveSession(new EditorSession { Tabs = [new EditorTabState { Id = "a2" }, new EditorTabState { Id = "a1" }] });
        using (var check = Create(tmp))
        {
            var s = check.Load();
            Assert.Equal(["a2", "a1"], s.Tabs.Select(t => t.Id));
            Assert.Equal("b1", Assert.Single(s.RecentlyClosed).Id);
        }
        // The recently-closed list is shared: the other window can reopen it.
        var reopened = a.ReopenLast();
        Assert.Equal("select 'b1'", reopened!.Value.Text);
        using (var check = Create(tmp))
        {
            Assert.Equal(["a2", "a1", "b1"], check.Load().Tabs.Select(t => t.Id));
            Assert.Empty(check.RecentlyClosed);
        }
    }

    [Fact]
    public void A_second_store_does_not_restore_tabs_another_live_store_has_open()
    {
        using var tmp = new TempDir();
        using (var seed = Create(tmp))
        {
            seed.SaveText("x", "select x");
            seed.SaveSession(new EditorSession { Tabs = [new EditorTabState { Id = "x" }] });
        }
        var first = Create(tmp);
        Assert.Single(first.LoadTabs());
        using (var second = Create(tmp))
            Assert.Empty(second.LoadTabs()); // editing "x" from two windows would overwrite each other

        first.Dispose();
        using var third = Create(tmp);
        Assert.Equal("select x", Assert.Single(third.LoadTabs()).Text);
    }

    [Fact]
    public void Tabs_not_restored_by_LoadTabs_max_stay_in_the_session()
    {
        using var tmp = new TempDir();
        using (var seed = Create(tmp))
        {
            seed.SaveText("a", "a");
            seed.SaveText("b", "b");
            seed.SaveSession(new EditorSession { Tabs = [new EditorTabState { Id = "a" }, new EditorTabState { Id = "b" }] });
        }
        using (var store = Create(tmp))
        {
            var only = Assert.Single(store.LoadTabs(max: 1));
            store.SaveSession(new EditorSession { Tabs = [only.State] });
        }
        using var check = Create(tmp);
        Assert.Equal(["a", "b"], check.Load().Tabs.Select(t => t.Id));
    }

    [Fact]
    public void Tab_file_without_a_session_entry_is_recovered()
    {
        // A crash after the text was written but before session.json listed the new tab.
        using var tmp = new TempDir();
        using (var seed = Create(tmp))
        {
            seed.SaveText("old", "old");
            seed.SaveSession(new EditorSession { Tabs = [new EditorTabState { Id = "old" }] });
            seed.SaveText("fresh", "typed just before the crash");
            seed.Flush();
        }
        using var store = Create(tmp);
        var tabs = store.LoadTabs();
        Assert.Equal(["old", "fresh"], tabs.Select(t => t.State.Id));
        Assert.Equal("typed just before the crash", tabs[1].Text);
    }

    [Fact]
    public void Corrupt_session_json_keeps_every_tab_text_and_a_backup()
    {
        using var tmp = new TempDir();
        using (var seed = Create(tmp))
        {
            seed.SaveText("a", "select a");
            seed.SaveSession(new EditorSession { Tabs = [new EditorTabState { Id = "a" }] });
        }
        var dir = tmp.Paths.EditorDir("conn1");
        File.WriteAllText(Path.Combine(dir, "session.json"), "{ \"tabs\": [ {\"id\": ");
        using var store = Create(tmp, log: []);
        Assert.Equal("select a", Assert.Single(store.LoadTabs()).Text);
        store.SaveSession(new EditorSession { Tabs = [new EditorTabState { Id = "a" }] });
        Assert.NotEmpty(Directory.GetFiles(dir, "session.json.corrupt*"));
    }

    [Fact]
    public void Failed_atomic_write_leaves_the_old_file_and_no_temp_or_empty_files()
    {
        using var tmp = new TempDir();
        var file = Path.Combine(tmp.Path, "q.sql");
        File.WriteAllText(file, "old text");
        using (Block(file))
            Assert.ThrowsAny<IOException>(() => AtomicFile.WriteAllText(file, "new text"));
        Assert.Equal("old text", File.ReadAllText(file));
        Assert.Equal([file], Directory.GetFiles(tmp.Path));
    }

    [Fact]
    public void Stale_temp_files_from_a_crash_mid_write_are_cleaned_and_never_restored()
    {
        using var tmp = new TempDir();
        var dir = tmp.Paths.EditorDir("conn1");
        using (var seed = Create(tmp))
        {
            seed.SaveText("a", "select a");
            seed.SaveSession(new EditorSession { Tabs = [new EditorTabState { Id = "a" }] });
        }
        var temp = Path.Combine(dir, ".a.sql.0123456789abcdef.tmp");
        File.WriteAllText(temp, "");
        File.SetLastWriteTimeUtc(temp, DateTime.UtcNow.AddHours(-1));
        using var store = Create(tmp);
        Assert.Equal("select a", Assert.Single(store.LoadTabs()).Text);
        Assert.False(File.Exists(temp));
    }
}
