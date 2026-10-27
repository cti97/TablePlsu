using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows.Threading;
using TablePlsu.App.Features.Workspace;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Editors;
using TablePlsu.Core.IO;
using TablePlsu.Core.Paths;
using TablePlsu.Core.Settings;

namespace TablePlsu.App.Features.SqlEditor;

/// <summary>
/// Per-workspace autosave coordinator for SQL editor tabs (spec §9 "Autosave"). Owns the connection's
/// <see cref="EditorSessionStore"/> (<c>editors\{connId}\{tabId}.sql</c> + <c>session.json</c>), restores every tab when
/// the workspace opens (before the connection completes), writes session.json (order, titles, caret, selection, scroll,
/// linked file, active tab) debounced on every change, moves user-closed tabs to "recently closed" and reopens them.
/// Store calls that change session.json run on one serial background queue so they never block the UI and keep their order.
/// Lives in <see cref="IWorkspace.Services"/> and is disposed with the workspace.
/// </summary>
public sealed class EditorSessionManager : IDisposable
{
    private readonly IWorkspace _ws;
    private readonly EditorSessionStore _store;
    private readonly object _queueGate = new();
    private Task _queue = Task.CompletedTask;
    private DispatcherTimer? _sessionTimer;
    private bool _closing;
    private bool _disposed;
    private int _rowLimit;

    public EditorSessionManager(IWorkspace ws, AppPaths paths)
    {
        _ws = ws;
        // The tab view-models debounce keystrokes themselves (editor.autoSaveDebounceMs) and every write goes through the
        // serial queue below, so the store's own timer is effectively off.
        _store = new EditorSessionStore(paths, ws.Profile.Id, TimeSpan.FromHours(1), msg => ws.ConsoleLog.Add(msg, ConsoleEntryKind.Error));
        var stored = AppServices.IsInitialized ? AppServices.Connections.Get(ws.Profile.Id) : null;
        _rowLimit = stored?.RowLimit ?? ws.Profile.RowLimit ?? Settings.Editor.DefaultRowLimit;

        if (ws.Tabs is INotifyCollectionChanged tabs) tabs.CollectionChanged += OnTabsChanged;
        ws.Closing += OnWorkspaceClosing;
        lock (Live) Live.Add(this);
    }

    private static readonly List<EditorSessionManager> Live = [];

    /// <summary>
    /// Writes every live workspace's editor text and session now, including text still inside the editor's keystroke
    /// debounce. For crash and logoff paths (unhandled exception, Windows session ending): safe from any thread and never
    /// throws. Off the UI thread it marshals to the UI thread (bounded wait), then writes whatever reached the stores.
    /// </summary>
    public static void FlushAll()
    {
        EditorSessionManager[] live;
        lock (Live) live = [.. Live];
        if (live.Length == 0) return;
        var dispatcher = UiDispatch.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            try
            {
                if (!dispatcher.HasShutdownStarted)
                    dispatcher.InvokeAsync(() => FlushAllOnUiThread(live), DispatcherPriority.Send).Task.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // The UI thread is unusable; the stores below still get whatever they hold.
            }
        }
        else
        {
            FlushAllOnUiThread(live);
        }
        foreach (var m in live)
        {
            try { m._store.Flush(); } catch (Exception) { /* best effort */ }
        }
    }

    private static void FlushAllOnUiThread(EditorSessionManager[] live)
    {
        foreach (var m in live)
        {
            try
            {
                foreach (var t in m._ws.Tabs.OfType<SqlEditorTabViewModel>()) t.PushText();
                m.FlushNow();
            }
            catch (Exception)
            {
                // Keep going: one broken workspace must not stop the others from saving.
            }
        }
    }

    /// <summary>The manager of <paramref name="ws"/> (created on first use).</summary>
    public static EditorSessionManager For(IWorkspace ws) =>
        ws.Services.GetOrAdd(() => new EditorSessionManager(ws, AppServices.IsInitialized ? AppServices.Paths : AppPaths.Current));

    public EditorSessionStore Store => _store;

    public IWorkspace Workspace => _ws;

    private static AppSettings Settings => AppServices.IsInitialized ? AppServices.Current : new AppSettings();

    // ---- row limit (remembered per connection) ------------------------------------------------------------------

    /// <summary>The editor row limit for this connection (0 = no limit).</summary>
    public int RowLimit => _rowLimit;

    /// <summary>Raised when one tab changes the row limit so the other tabs follow.</summary>
    public event EventHandler<int>? RowLimitChanged;

    public void SetRowLimit(int value)
    {
        if (value == _rowLimit) return;
        _rowLimit = value;
        RowLimitChanged?.Invoke(this, value);
        if (!AppServices.IsInitialized) return;
        var stored = AppServices.Connections.Get(_ws.Profile.Id);
        if (stored is null) return;
        try { AppServices.Connections.Save(stored with { RowLimit = value }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ws.ConsoleLog.Add("Could not save the row limit: " + ex.Message, ConsoleEntryKind.Error);
        }
    }

    // ---- tabs -----------------------------------------------------------------------------------------------------

    /// <summary>A new, empty (or pre-filled) editor tab. The caller adds it to the workspace.</summary>
    public SqlEditorTabViewModel CreateNew(string? sql = null, string? title = null)
    {
        var tab = new SqlEditorTabViewModel(EditorSessionStore.NewTabId(), sql ?? "", this)
        {
            Title = title ?? NextTitle(),
        };
        if (!string.IsNullOrEmpty(sql)) SaveText(tab.Id, sql);
        return tab;
    }

    /// <summary>"Query N" following the open editor tabs.</summary>
    public string NextTitle() => SqlTextOps.NextQueryTitle(_ws.Tabs.OfType<SqlEditorTabViewModel>().Select(t => t.Title));

    /// <summary>Restores the session's tabs into the workspace (per <c>editor.autoSaveTabs</c>). Returns the restored tabs.</summary>
    public IReadOnlyList<SqlEditorTabViewModel> Restore()
    {
        var mode = Settings.Editor.AutoSaveTabs;
        if (mode == AutoSaveTabs.None) return [];
        IReadOnlyList<RestoredTab> restored;
        // Tabs another window has open on this connection are skipped; with "first", the rest stay on disk.
        try { restored = _store.LoadTabs(mode == AutoSaveTabs.First ? 1 : null); }
        catch (Exception ex)
        {
            _ws.ConsoleLog.Add("Could not restore editor tabs: " + ex.Message, ConsoleEntryKind.Error);
            return [];
        }
        var activeId = _store.Load().ActiveTabId;
        var tabs = new List<SqlEditorTabViewModel>();
        foreach (var r in restored)
        {
            var tab = new SqlEditorTabViewModel(r.State.Id, r.Text, this, r.State);
            if (r.State.LinkedFilePath is { Length: > 0 } path) _store.LinkFile(tab.Id, path);
            tabs.Add(tab);
            // Activating each keeps the saved order (AddTab inserts after the active tab).
            _ws.AddTab(tab, activate: true);
        }
        var active = tabs.Find(t => t.Id == activeId) ?? tabs.FirstOrDefault();
        if (active is not null) _ws.ActivateTab(active);
        if (tabs.Count > 0) _ws.Log($"Restored {tabs.Count} editor tab{(tabs.Count == 1 ? "" : "s")}.");
        return tabs;
    }

    /// <summary>Ctrl+Shift+T: reopens the most recently closed editor tab. Null when there is none.</summary>
    public SqlEditorTabViewModel? ReopenLast()
    {
        WaitQueue();
        var r = _store.ReopenLast();
        if (r is not { } restored) return null;
        var tab = new SqlEditorTabViewModel(restored.State.Id, restored.Text, this, restored.State);
        if (restored.State.LinkedFilePath is { Length: > 0 } path) _store.LinkFile(tab.Id, path);
        _ws.AddTab(tab, activate: true);
        return tab;
    }

    /// <summary>Ctrl+O: opens <paramref name="path"/> in a new editor tab linked to the file.</summary>
    public SqlEditorTabViewModel OpenFile(string path)
    {
        var existing = _ws.Tabs.OfType<SqlEditorTabViewModel>()
            .FirstOrDefault(t => string.Equals(t.LinkedFilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _ws.ActivateTab(existing);
            return existing;
        }
        var text = AtomicFile.ReadAllTextShared(path);
        var tab = new SqlEditorTabViewModel(EditorSessionStore.NewTabId(), text, this) { Title = Path.GetFileName(path) };
        tab.SetLinkedFile(path);
        SaveText(tab.Id, text);
        _ws.AddTab(tab, activate: true);
        return tab;
    }

    internal void LinkFile(string tabId, string? path)
    {
        _store.LinkFile(tabId, path);
        ScheduleSessionSave();
    }

    // ---- autosave -------------------------------------------------------------------------------------------------

    /// <summary>Queues a tab's text; it is written on the background queue (atomic write, never on the UI thread).</summary>
    public void SaveText(string tabId, string text)
    {
        if (_disposed) return;
        _store.SaveText(tabId, text);
        Enqueue(_store.Flush);
    }

    /// <summary>Writes pending texts on the background queue (run, format).</summary>
    public void FlushInBackground() => Enqueue(_store.Flush);

    /// <summary>Writes pending texts and session.json now (window deactivate, exit, Ctrl+S). Small, synchronous.</summary>
    public void FlushNow()
    {
        if (_disposed) return;
        WaitQueue();
        _store.Flush();
        if (!_closing) SaveSessionCore(BuildSession());
    }

    /// <summary>Saves session.json after a short debounce (tab order, titles, caret, scroll…).</summary>
    public void ScheduleSessionSave()
    {
        if (_closing || _disposed) return;
        var dispatcher = UiDispatch.Dispatcher;
        if (dispatcher is null)
        {
            SaveSessionCore(BuildSession());
            return;
        }
        if (_sessionTimer is null)
        {
            _sessionTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(500) };
            _sessionTimer.Tick += (_, _) =>
            {
                _sessionTimer!.Stop();
                if (_closing || _disposed) return;
                var session = BuildSession();
                Enqueue(() => _store.SaveSession(session));
            };
        }
        _sessionTimer.Stop();
        _sessionTimer.Start();
    }

    /// <summary>The current session snapshot (UI thread): editor tabs in workspace order.</summary>
    public EditorSession BuildSession(SqlEditorTabViewModel? closing = null)
    {
        var tabs = _ws.Tabs.OfType<SqlEditorTabViewModel>().Where(t => t != closing).Select(t => t.ToState()).ToList();
        if (closing is not null) tabs.Add(closing.ToState());
        var active = _ws.ActiveTab as SqlEditorTabViewModel;
        return new EditorSession { Tabs = tabs, ActiveTabId = active?.Id ?? tabs.FirstOrDefault()?.Id };
    }

    /// <summary>A user-closed tab: its text is flushed and it moves to "recently closed".</summary>
    internal void TabClosed(SqlEditorTabViewModel tab)
    {
        if (_closing || _disposed) return;
        var session = BuildSession(closing: tab);
        Enqueue(() =>
        {
            _store.SaveSession(session);
            _store.CloseTab(tab.Id);
        });
    }

    private void SaveSessionCore(EditorSession session)
    {
        WaitQueue();
        _store.SaveSession(session);
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (var t in e.NewItems.OfType<SqlEditorTabViewModel>()) t.PropertyChanged += OnTabPropertyChanged;
        if (e.OldItems is not null)
            foreach (var t in e.OldItems.OfType<SqlEditorTabViewModel>()) t.PropertyChanged -= OnTabPropertyChanged;
        if (e.Action == NotifyCollectionChangedAction.Reset || _closing) return;
        if (_ws.Tabs.OfType<SqlEditorTabViewModel>().Any() || e.OldItems?.OfType<SqlEditorTabViewModel>().Any() == true)
            ScheduleSessionSave();
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabViewModelBase.IsActive) or nameof(TabViewModelBase.Title) or nameof(TabViewModelBase.CustomTitle))
            ScheduleSessionSave();
    }

    /// <summary>The workspace is closing: write the final session while the tabs are still there, then ignore changes.</summary>
    private void OnWorkspaceClosing(object? sender, EventArgs e)
    {
        if (_closing) return;
        foreach (var t in _ws.Tabs.OfType<SqlEditorTabViewModel>()) t.PushText();
        var session = BuildSession();
        _closing = true;
        _sessionTimer?.Stop();
        SaveSessionCore(session);
    }

    private void Enqueue(Action? work)
    {
        if (work is null) return;
        lock (_queueGate)
        {
            _queue = _queue.ContinueWith(_ =>
            {
                try { work(); }
                catch (Exception ex) { _ws.ConsoleLog.Add("Autosave failed: " + ex.Message, ConsoleEntryKind.Error); }
            }, TaskScheduler.Default);
        }
    }

    private void WaitQueue()
    {
        Task q;
        lock (_queueGate) q = _queue;
        try { q.Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { /* logged by the work item */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        lock (Live) Live.Remove(this);
        _closing = true;
        _sessionTimer?.Stop();
        if (_ws.Tabs is INotifyCollectionChanged tabs) tabs.CollectionChanged -= OnTabsChanged;
        _ws.Closing -= OnWorkspaceClosing;
        WaitQueue();
        _disposed = true;
        _store.Dispose();
    }
}
