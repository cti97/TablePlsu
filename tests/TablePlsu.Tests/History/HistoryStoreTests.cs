using TablePlsu.Core.History;
using TablePlsu.Tests.Infrastructure;

namespace TablePlsu.Tests.History;

public class HistoryStoreTests
{
    private static HistoryEntry Entry(string sql, bool error = false, string db = "app") =>
        new() { Timestamp = DateTimeOffset.UtcNow, Database = db, Sql = sql, DurationMs = 1.5, Rows = 3, Error = error };

    [Fact]
    public void Append_and_load_newest_first()
    {
        using var tmp = new TempDir();
        var store = new HistoryStore(tmp.Paths);
        store.Append("c1", Entry("select 1"));
        store.Append("c1", Entry("select 2", error: true));
        var all = store.Load("c1");
        Assert.Equal(new[] { "select 2", "select 1" }, all.Select(e => e.Sql));
        Assert.True(all[0].Error);
        Assert.Equal(3, all[1].Rows);
        Assert.Equal("app", all[1].Database);
        Assert.False(string.IsNullOrEmpty(all[0].Id));
        Assert.Empty(store.Load("other"));
        Assert.True(File.Exists(tmp.Paths.HistoryFile("c1")));
    }

    [Fact]
    public void Cap_keeps_newest_entries()
    {
        using var tmp = new TempDir();
        var store = new HistoryStore(tmp.Paths) { MaxEntries = 10 };
        for (var i = 0; i < 35; i++) store.Append("c1", Entry("select " + i));
        var all = store.Load("c1");
        Assert.Equal(10, all.Count);
        Assert.Equal("select 34", all[0].Sql);
        Assert.Equal("select 25", all[^1].Sql);
        // The file is compacted so it doesn't grow without bound.
        Assert.True(File.ReadAllLines(tmp.Paths.HistoryFile("c1")).Length <= 12);
    }

    [Fact]
    public void Search_matches_all_terms_case_insensitively()
    {
        using var tmp = new TempDir();
        var store = new HistoryStore(tmp.Paths);
        store.Append("c1", Entry("SELECT * FROM orders"));
        store.Append("c1", Entry("select * from customers"));
        store.Append("c1", Entry("update orders set x = 1"));
        Assert.Equal(new[] { "update orders set x = 1", "SELECT * FROM orders" }, store.Search("c1", "ORDERS").Select(e => e.Sql));
        Assert.Equal(new[] { "SELECT * FROM orders" }, store.Search("c1", "select orders").Select(e => e.Sql));
        Assert.Equal(3, store.Search("c1", "").Count);
        Assert.Single(store.Search("c1", "", limit: 1));
    }

    [Fact]
    public void Delete_and_clear()
    {
        using var tmp = new TempDir();
        var store = new HistoryStore(tmp.Paths);
        store.Append("c1", Entry("a"));
        store.Append("c1", Entry("b"));
        var b = store.Load("c1")[0];
        store.Delete("c1", b.Id);
        Assert.Equal(new[] { "a" }, store.Load("c1").Select(e => e.Sql));
        store.Clear("c1");
        Assert.Empty(store.Load("c1"));
    }

    [Fact]
    public void Disabled_store_does_not_write()
    {
        using var tmp = new TempDir();
        var store = new HistoryStore(tmp.Paths) { Enabled = false };
        store.Append("c1", Entry("a"));
        Assert.Empty(store.Load("c1"));
    }

    [Fact]
    public void Corrupt_lines_are_skipped()
    {
        using var tmp = new TempDir();
        var store = new HistoryStore(tmp.Paths);
        store.Append("c1", Entry("a"));
        File.AppendAllText(tmp.Paths.HistoryFile("c1"), "{ broken\n");
        store.Append("c1", Entry("b"));
        Assert.Equal(new[] { "b", "a" }, store.Load("c1").Select(e => e.Sql));
    }

    [Fact]
    public void Multiline_sql_round_trips()
    {
        using var tmp = new TempDir();
        var store = new HistoryStore(tmp.Paths);
        store.Append("c1", Entry("select 1\nfrom t\r\nwhere x = 'a\"b'"));
        Assert.Equal("select 1\nfrom t\r\nwhere x = 'a\"b'", store.Load("c1")[0].Sql);
    }
}
