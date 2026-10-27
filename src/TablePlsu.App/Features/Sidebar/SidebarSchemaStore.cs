using System.IO;
using System.Text.Json;
using TablePlsu.Core.IO;
using TablePlsu.Core.Paths;

namespace TablePlsu.App.Features.Sidebar;

/// <summary>
/// Remembers which schemas the sidebar's schema selector shows, per connection and database:
/// <c>%LOCALAPPDATA%\TablePlsu\sidebar\{connectionId}.json</c> = <c>{ "db": ["public", "sales"] }</c>. A missing entry
/// means "all schemas". Errors are swallowed (it is a convenience).
/// </summary>
public sealed class SidebarSchemaStore(AppPaths paths)
{
    private static readonly object Gate = new();

    public string FileFor(string connectionId) => Path.Combine(paths.Local, "sidebar", AppPaths.SafeSegment(connectionId) + ".json");

    /// <summary>The remembered schema list, or null when none was saved (show all).</summary>
    public IReadOnlyList<string>? Load(string connectionId, string database)
    {
        lock (Gate)
        {
            var map = Read(FileFor(connectionId));
            return map.TryGetValue(database, out var list) ? list : null;
        }
    }

    /// <summary>Saves the selection; null removes it (show all).</summary>
    public void Save(string connectionId, string database, IEnumerable<string>? schemas)
    {
        lock (Gate)
        {
            var file = FileFor(connectionId);
            var map = Read(file);
            if (schemas is null) map.Remove(database);
            else map[database] = [.. schemas];
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                AtomicFile.WriteAllText(file, JsonSerializer.Serialize(map));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static Dictionary<string, List<string>> Read(string file)
    {
        try
        {
            if (!File.Exists(file)) return [];
            return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(AtomicFile.ReadAllTextShared(file)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }
}
