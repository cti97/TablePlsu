namespace TablePlsu.Core.Paths;

/// <summary>
/// Every on-disk location the app uses. <see cref="Roaming"/> is <c>%APPDATA%\TablePlsu</c> (settings, connections,
/// secrets, themes, queries); <see cref="Local"/> is <c>%LOCALAPPDATA%\TablePlsu</c> (editors, history, cache).
/// Tests use <see cref="ForRoot"/> to redirect everything into a temp folder.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string roaming, string local)
    {
        Roaming = Path.GetFullPath(roaming);
        Local = Path.GetFullPath(local);
    }

    /// <summary>The process-wide instance used by the app. Tests may replace it.</summary>
    public static AppPaths Current { get; set; } = CreateDefault();

    public static AppPaths CreateDefault() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TablePlsu"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TablePlsu"));

    /// <summary>Puts both trees under one root (<c>root\roaming</c>, <c>root\local</c>).</summary>
    public static AppPaths ForRoot(string root) => new(Path.Combine(root, "roaming"), Path.Combine(root, "local"));

    public string Roaming { get; }
    public string Local { get; }

    public string SettingsFile => Path.Combine(Roaming, "settings.json");
    public string ConnectionsFile => Path.Combine(Roaming, "connections.json");
    public string SecretsFile => Path.Combine(Roaming, "secrets.dat");
    public string ThemesDir => Path.Combine(Roaming, "themes");
    public string QueriesDir => Path.Combine(Roaming, "queries");

    public string EditorsDir => Path.Combine(Local, "editors");
    public string HistoryDir => Path.Combine(Local, "history");
    public string CacheDir => Path.Combine(Local, "cache");
    public string MetadataCacheDir => Path.Combine(CacheDir, "metadata");
    public string LogsDir => Path.Combine(Local, "logs");

    public string EditorDir(string connectionId) => Path.Combine(EditorsDir, SafeSegment(connectionId));
    public string HistoryFile(string connectionId) => Path.Combine(HistoryDir, SafeSegment(connectionId) + ".jsonl");

    public void EnsureCreated()
    {
        foreach (var dir in new[] { Roaming, Local, ThemesDir, EditorsDir, HistoryDir, MetadataCacheDir })
            Directory.CreateDirectory(dir);
    }

    /// <summary>Validates that an id can be used as a single file/folder name (no separators, no "..").</summary>
    public static string SafeSegment(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id is "." or ".." || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"'{id}' is not a valid file name segment.", nameof(id));
        return id;
    }

    public static bool IsSafeSegment(string id)
    {
        try { SafeSegment(id); return true; }
        catch (ArgumentException) { return false; }
    }
}
