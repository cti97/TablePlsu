using TablePlsu.Core.Paths;
using TablePlsu.Tests.Infrastructure;

namespace TablePlsu.Tests.Paths;

public class AppPathsTests
{
    [Fact]
    public void ForRoot_places_roaming_and_local_under_root()
    {
        using var tmp = new TempDir();
        var p = tmp.Paths;
        Assert.StartsWith(tmp.Path, p.Roaming);
        Assert.StartsWith(tmp.Path, p.Local);
        Assert.Equal(Path.Combine(p.Roaming, "settings.json"), p.SettingsFile);
        Assert.Equal(Path.Combine(p.Roaming, "connections.json"), p.ConnectionsFile);
        Assert.Equal(Path.Combine(p.Roaming, "secrets.dat"), p.SecretsFile);
        Assert.Equal(Path.Combine(p.Roaming, "themes"), p.ThemesDir);
        Assert.Equal(Path.Combine(p.Roaming, "queries"), p.QueriesDir);
        Assert.Equal(Path.Combine(p.Local, "editors", "abc"), p.EditorDir("abc"));
        Assert.Equal(Path.Combine(p.Local, "history", "abc.jsonl"), p.HistoryFile("abc"));
        Assert.Equal(Path.Combine(p.Local, "cache", "metadata"), p.MetadataCacheDir);
    }

    [Fact]
    public void Default_uses_appdata_folders()
    {
        var p = AppPaths.CreateDefault();
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TablePlsu"), p.Roaming);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TablePlsu"), p.Local);
    }

    [Fact]
    public void EnsureCreated_creates_directories()
    {
        using var tmp = new TempDir();
        tmp.Paths.EnsureCreated();
        Assert.True(Directory.Exists(tmp.Paths.ThemesDir));
        Assert.True(Directory.Exists(tmp.Paths.EditorsDir));
        Assert.True(Directory.Exists(tmp.Paths.HistoryDir));
    }

    [Fact]
    public void EditorDir_rejects_path_traversal()
    {
        using var tmp = new TempDir();
        Assert.Throws<ArgumentException>(() => tmp.Paths.EditorDir("..\\x"));
    }
}
