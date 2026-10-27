namespace TablePlsu.App.Infrastructure;

/// <summary>An item selected in the sidebar tree. <see cref="ObjectKind"/> is one of <see cref="ObjectKinds"/>.</summary>
public sealed record SidebarItemRef(string Schema, string Name, string ObjectKind)
{
    /// <summary>Function signature (argument types) for overloads; null otherwise.</summary>
    public string? Signature { get; init; }
}

/// <summary>Context for building the sidebar item context menu.</summary>
public sealed record SidebarMenuContext(IWorkspace Workspace, IReadOnlyList<SidebarItemRef> Items);

/// <summary>
/// Adds entries to the sidebar item context menu. <c>Contribute</c> appends WPF <c>MenuItem</c>/<c>Separator</c> objects
/// to the list; it runs every time the menu opens, so keep it cheap.
/// </summary>
public interface ISidebarMenuContributor
{
    /// <summary>Lower runs first. The Sidebar feature's own items use 0; other features use 100+.</summary>
    int Order => 100;

    void Contribute(SidebarMenuContext context, IList<object> items);
}

public sealed class SidebarMenuRegistry
{
    private readonly List<ISidebarMenuContributor> _contributors = [];

    public void Register(ISidebarMenuContributor contributor) => _contributors.Add(contributor);

    public IReadOnlyList<ISidebarMenuContributor> Contributors => [.. _contributors.OrderBy(c => c.Order)];

    /// <summary>Builds the menu item list by running every contributor in order.</summary>
    public IList<object> Build(SidebarMenuContext context)
    {
        var items = new List<object>();
        foreach (var c in Contributors) c.Contribute(context, items);
        return items;
    }
}
