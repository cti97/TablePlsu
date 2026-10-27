using System.IO;
using TablePlsu.App.Features.Workspace;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.IO;

namespace TablePlsu.App.Shell;

/// <summary>
/// Remembers which workspaces (connection id + database) are open, for <c>general.reopenWorkspacesOnStartup</c>.
/// Stored as "connectionId\tdatabase" lines in <c>%LOCALAPPDATA%\TablePlsu\open-workspaces.txt</c> (atomic writes).
/// The last window's workspace stays in the list when the app exits, so it reopens next time.
/// </summary>
public static class OpenWorkspaces
{
    private static readonly List<WorkspaceViewModel> Open = [];

    public readonly record struct Entry(string ConnectionId, string Database);

    private static string FilePath => Path.Combine(AppServices.Paths.Local, "open-workspaces.txt");

    public static void Add(WorkspaceViewModel ws)
    {
        Open.Add(ws);
        Save();
    }

    public static void Remove(WorkspaceViewModel ws)
    {
        if (Open.Remove(ws)) Save();
    }

    public static IReadOnlyList<Entry> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            return
            [
                .. AtomicFile.ReadAllTextShared(FilePath)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(l => l.Split('\t'))
                    .Where(p => p.Length == 2 && p[0].Length > 0)
                    .Select(p => new Entry(p[0], p[1])),
            ];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static void Save()
    {
        try
        {
            var text = string.Join("\n", Open.Select(w => $"{w.Profile.Id}\t{w.Database}"));
            AtomicFile.WriteAllText(FilePath, text);
        }
        catch (Exception)
        {
            // Not critical: the next launch just shows the welcome screen.
        }
    }
}
