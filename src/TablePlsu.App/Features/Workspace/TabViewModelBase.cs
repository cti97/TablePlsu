using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using TablePlsu.App.Infrastructure;

namespace TablePlsu.App.Features.Workspace;

/// <summary>Why a tab is being closed.</summary>
public enum TabCloseReason
{
    /// <summary>The user closed the tab (SQL editors go to "recently closed").</summary>
    User,
    /// <summary>The workspace is closing (window close, disconnect, database switch): keep autosaved state as-is.</summary>
    WorkspaceClosing,
    /// <summary>A preview tab was replaced by another object (preview-tab reuse).</summary>
    Replaced,
}

/// <summary>What the preview-tab rules need to know about a tab.</summary>
public interface ITabInfo
{
    string Kind { get; }
    string? Schema { get; }
    string? Name { get; }
    bool IsPreview { get; }
    bool IsDirty { get; }
    bool HasPendingChanges { get; }
}

/// <summary>
/// Base for every workspace tab (SQL editor, table, structure, settings…). The workspace creates tabs through the factory
/// registry, calls <see cref="Attach"/>, then shows <see cref="CreateView"/> (created once and cached while the tab lives,
/// so editor state survives tab switches). Override the virtual hooks for capabilities: the toolbar's
/// Discard | Preview | Commit buttons follow <see cref="HasPendingChanges"/>.
/// Implement <see cref="ICommandTarget"/> on a subclass to receive workspace-scope commands (edit.undo, table.showStructure…)
/// while the tab is active.
/// </summary>
public abstract partial class TabViewModelBase : ObservableObject, ITabInfo
{
    protected TabViewModelBase(string kind, string? id = null)
    {
        Kind = kind;
        Id = id ?? Guid.NewGuid().ToString("N")[..12];
    }

    /// <summary>Stable id (SQL editors use it as the autosave file name).</summary>
    public string Id { get; }

    /// <summary>One of <see cref="TabKinds"/> (or a feature-defined kind).</summary>
    public string Kind { get; }

    /// <summary>The object shown (for object tabs).</summary>
    public string? Schema { get; protected set; }

    public string? Name { get; protected set; }

    public string? ObjectKind { get; protected set; }

    /// <summary>The workspace that owns the tab; set by <see cref="Attach"/> before the tab is shown.</summary>
    public IWorkspace Workspace { get; private set; } = null!;

    /// <summary>Default title (for example "Query 1" or the table name).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    public partial string Title { get; set; } = "";

    /// <summary>User label from "Rename Tab"; saved with the autosave session by the owning feature.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    public partial string? CustomTitle { get; set; }

    public string DisplayTitle => string.IsNullOrWhiteSpace(CustomTitle) ? Title : CustomTitle!;

    /// <summary>Tooltip for the tab header.</summary>
    [ObservableProperty]
    public partial string? ToolTip { get; set; }

    /// <summary>Unsaved local edits (for example a linked .sql file). Dirty tabs are never replaced.</summary>
    [ObservableProperty]
    public partial bool IsDirty { get; set; }

    /// <summary>A preview tab (opened by a single click) may be replaced by the next single click. Double-click pins it.</summary>
    [ObservableProperty]
    public partial bool IsPreview { get; set; }

    /// <summary>Staged GUI changes awaiting commit; enables Discard | Preview | Commit.</summary>
    [ObservableProperty]
    public partial bool HasPendingChanges { get; set; }

    /// <summary>A query or load is running (drives the status bar progress).</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>True while this is the workspace's active tab.</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>Called by the workspace right after creation.</summary>
    public void Attach(IWorkspace workspace)
    {
        Workspace = workspace;
        OnAttached();
    }

    protected virtual void OnAttached()
    {
    }

    /// <summary>Creates the tab's view (called once, lazily, on first activation). Its DataContext defaults to the tab.</summary>
    public abstract FrameworkElement CreateView();

    /// <summary>Whether this tab already shows what <paramref name="request"/> asks for (used to activate instead of open).</summary>
    public virtual bool Matches(TabRequest request) =>
        request.Kind == Kind
        && Kind is not (TabKinds.SqlEditor or TabKinds.Empty)
        && string.Equals(request.Schema, Schema, StringComparison.Ordinal)
        && string.Equals(request.Name, Name, StringComparison.Ordinal);

    /// <summary>Ctrl+S with nothing staged: flush autosave or save the linked file.</summary>
    public virtual void Save()
    {
    }

    /// <summary>Commits staged changes (one transaction). Only called when <see cref="HasPendingChanges"/>.</summary>
    public virtual Task CommitAsync() => Task.CompletedTask;

    /// <summary>Shows the generated SQL for the staged changes.</summary>
    public virtual void PreviewChanges()
    {
    }

    /// <summary>Drops staged changes and reloads.</summary>
    public virtual void DiscardChanges()
    {
    }

    /// <summary>Reload tab (Ctrl+Alt+R) and part of "Reload workspace".</summary>
    public virtual void Reload()
    {
    }

    /// <summary>Flushes autosave state (window deactivate, app exit). Must not block.</summary>
    public virtual void Flush()
    {
    }

    /// <summary>Last chance to refuse closing (for example "discard pending changes?"). True = close.</summary>
    public virtual bool CanCloseNow(TabCloseReason reason) => true;

    public virtual void OnActivated()
    {
    }

    public virtual void OnDeactivated()
    {
    }

    /// <summary>The tab was removed from the workspace. Release resources here.</summary>
    public virtual void OnClosed(TabCloseReason reason)
    {
    }
}
