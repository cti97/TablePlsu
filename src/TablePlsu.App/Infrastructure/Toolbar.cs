using System.Windows;

namespace TablePlsu.App.Infrastructure;

/// <summary>The toolbar item ids known to settings.json (<c>toolbar.items</c>, see settings.schema.json).</summary>
public static class ToolbarItemIds
{
    public const string ToggleSidebar = "toggleSidebar";
    public const string Connection = "connection";
    public const string Changes = "changes";
    public const string SafeMode = "safeMode";
    public const string Database = "database";
    public const string NewSql = "newSql";
    public const string Status = "status";
    public const string Notifications = "notifications";
    public const string Reload = "reload";
    public const string Command = "command";
    public const string Panels = "panels";

    public static IReadOnlySet<string> Known { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        ToggleSidebar, Connection, Changes, SafeMode, Database, NewSql, Status, Notifications, Reload, Command, Panels,
    };
}

/// <summary>
/// A toolbar item. <see cref="Create"/> builds the element for one workspace (its DataContext is the workspace view-model).
/// A <see cref="Stretch"/> item takes the remaining width (the status bar).
/// </summary>
public sealed record ToolbarItemDefinition(string Id, Func<IWorkspace, FrameworkElement> Create)
{
    public bool Stretch { get; init; }
}

/// <summary>
/// Toolbar items by id. The workspace shows the ids listed in <c>settings.toolbar.items</c> in that order, then any
/// feature items whose id is not one of <see cref="ToolbarItemIds.Known"/> (so feature items show up without a settings change).
/// Registering an existing id replaces it.
/// </summary>
public sealed class ToolbarRegistry
{
    private readonly List<ToolbarItemDefinition> _items = [];

    public void Register(ToolbarItemDefinition item)
    {
        _items.RemoveAll(i => i.Id == item.Id);
        _items.Add(item);
    }

    public ToolbarItemDefinition? Get(string id) => _items.Find(i => i.Id == id);

    public IReadOnlyList<ToolbarItemDefinition> Items => _items;

    /// <summary>The items to show, in order, for the configured id list.</summary>
    public IReadOnlyList<ToolbarItemDefinition> Layout(IReadOnlyList<string> configured)
    {
        var result = new List<ToolbarItemDefinition>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in configured)
            if (seen.Add(id) && Get(id) is { } item) result.Add(item);
        foreach (var item in _items)
            if (!ToolbarItemIds.Known.Contains(item.Id) && seen.Add(item.Id)) result.Add(item);
        return result;
    }
}
