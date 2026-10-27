using TablePlsu.App.Features.Workspace;
using TablePlsu.Core.Connections;
using TablePlsu.Core.Db;
using TablePlsu.Core.History;
using TablePlsu.Core.Metadata;

namespace TablePlsu.App.Infrastructure;

/// <summary>
/// What features see of an open workspace (one connection + one database). Implemented by
/// <see cref="WorkspaceViewModel"/>. All members are UI-thread only unless noted.
/// </summary>
public interface IWorkspace
{
    /// <summary>Unique per workspace instance (not per connection).</summary>
    string Id { get; }

    ConnectionProfile Profile { get; }

    string Database { get; }

    /// <summary>The DB session. Exists from construction; it may not be connected yet (see <see cref="State"/>).</summary>
    DbSession Session { get; }

    /// <summary>
    /// Schema metadata for this connection + database. Created with the workspace; the disk cache loads immediately and a
    /// server refresh runs after each (re)connect and on Ctrl+R. <see cref="MetadataCache.Changed"/> fires on a background
    /// thread (marshal with <see cref="UiDispatch"/>).
    /// </summary>
    MetadataCache Metadata { get; }

    /// <summary>Connection state, mirrored on the UI thread.</summary>
    DbSessionState State { get; }

    /// <summary>Raised on the UI thread whenever <see cref="State"/> changes.</summary>
    event EventHandler<DbSessionState>? StateChanged;

    /// <summary>Raised on the UI thread for "Reload workspace" (Ctrl+R): refresh data and the schema cache.</summary>
    event EventHandler? ReloadRequested;

    /// <summary>Raised on the UI thread before the workspace closes (window close, disconnect, database switch).</summary>
    event EventHandler? Closing;

    /// <summary>Shared per-connection query history (pass <c>Profile.Id</c>).</summary>
    HistoryStore History { get; }

    /// <summary>Safe Mode gate for this workspace. Every editor run, commit, kill, drop… goes through it.</summary>
    ISafeModeGate SafeMode { get; }

    /// <summary>Per-workspace service bag for feature state (for example the metadata cache), keyed by type.</summary>
    WorkspaceServices Services { get; }

    IReadOnlyList<TabViewModelBase> Tabs { get; }

    TabViewModelBase? ActiveTab { get; }

    /// <summary>Opens (or reuses, per the preview-tab rules) a tab for <paramref name="request"/>. Null when no factory handles the kind.</summary>
    TabViewModelBase? OpenTab(TabRequest request);

    /// <summary>Adds an already-created tab (for example one restored from the autosave session).</summary>
    void AddTab(TabViewModelBase tab, bool activate = true);

    void ActivateTab(TabViewModelBase tab);

    /// <summary>Closes the tab unless <see cref="TabViewModelBase.CanCloseNow"/> refuses. Returns true when closed.</summary>
    bool CloseTab(TabViewModelBase tab);

    /// <summary>Appends a line to the console log. Thread-safe.</summary>
    void Log(string message);

    /// <summary>The console log buffer (last 1,000 entries).</summary>
    WorkspaceLog ConsoleLog { get; }

    bool IsSidebarVisible { get; set; }

    bool IsConsoleVisible { get; set; }

    /// <summary>Reconnects in the background (status shows "connecting…").</summary>
    Task ReconnectAsync();

    /// <summary>Closes the workspace and returns the window to the welcome screen.</summary>
    Task DisconnectAsync();

    /// <summary>Opens <paramref name="database"/> on the same connection: replaces this workspace, or opens a new window.</summary>
    void OpenDatabase(string database, bool newWindow);
}

/// <summary>Implemented by the window-level view-model so infrastructure can find the window's workspace.</summary>
public interface IWorkspaceHost
{
    IWorkspace? Workspace { get; }

    /// <summary>The shell content view-model (welcome or workspace).</summary>
    object? Content { get; }

    /// <summary>The command scope of <see cref="Content"/> (Welcome or Workspace), used when focus is outside any scope root.</summary>
    TablePlsu.Core.Commands.CommandScope ContentScope { get; }
}

/// <summary>
/// Safe Mode (spec §8). The default gate (<see cref="DefaultSafeModeGate"/>) is replaced by the SafeMode feature through
/// <see cref="FeatureContext.UseSafeMode"/>.
/// </summary>
public interface ISafeModeGate
{
    SafeModeLevel Level { get; }

    event EventHandler? LevelChanged;

    /// <summary>Asks the user (according to <see cref="Level"/>) whether <paramref name="statements"/> may run. True = send.</summary>
    Task<bool> ConfirmAsync(IReadOnlyList<string> statements, CancellationToken ct = default);

    /// <summary>Changes the level (lowering from Safe 1/2 may require the password). True when changed.</summary>
    Task<bool> ChangeLevelAsync(SafeModeLevel level);
}

/// <summary>A tiny type-keyed bag so features can attach per-workspace state without changing <see cref="IWorkspace"/>.</summary>
public sealed class WorkspaceServices : IDisposable
{
    private readonly Dictionary<Type, object> _items = [];

    public T? Get<T>() where T : class => _items.TryGetValue(typeof(T), out var v) ? (T)v : null;

    public T GetOrAdd<T>(Func<T> create) where T : class
    {
        if (_items.TryGetValue(typeof(T), out var v)) return (T)v;
        var created = create();
        _items[typeof(T)] = created;
        return created;
    }

    public void Set<T>(T value) where T : class => _items[typeof(T)] = value;

    public bool Remove<T>() where T : class => _items.Remove(typeof(T));

    /// <summary>Disposes every item that is <see cref="IDisposable"/> (called when the workspace closes).</summary>
    public void Dispose()
    {
        foreach (var item in _items.Values)
        {
            if (item is IDisposable d)
            {
                try { d.Dispose(); }
                catch (Exception) { /* best effort on close */ }
            }
        }
        _items.Clear();
    }
}
