using System.Windows;

namespace TablePlsu.App.Infrastructure;

/// <summary>Slots in the workspace layout that features fill.</summary>
public enum WorkspacePanel
{
    /// <summary>Left sidebar (object tree / history). Filled by the Sidebar feature.</summary>
    Sidebar,
    /// <summary>Bottom panel (console log). Filled by the Console feature.</summary>
    Console,
    /// <summary>Right sidebar (row detail, P1).</summary>
    Right,
}

/// <summary>Panel factories: one element per workspace per slot, created when the workspace view loads.</summary>
public sealed class WorkspacePanelRegistry
{
    private readonly Dictionary<WorkspacePanel, Func<IWorkspace, FrameworkElement>> _factories = [];

    public void Register(WorkspacePanel panel, Func<IWorkspace, FrameworkElement> factory) => _factories[panel] = factory;

    public bool IsRegistered(WorkspacePanel panel) => _factories.ContainsKey(panel);

    public FrameworkElement? Create(WorkspacePanel panel, IWorkspace workspace) =>
        _factories.TryGetValue(panel, out var f) ? f(workspace) : null;
}
