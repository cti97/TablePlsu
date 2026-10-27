using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Commands;
using TablePlsu.Core.Connections;
using TablePlsu.Core.Db;
using TablePlsu.Core.History;
using TablePlsu.Core.Settings;

namespace TablePlsu.App.Features.Workspace;

/// <summary>What a workspace needs from the window that hosts it (implemented by the Shell's MainViewModel).</summary>
public interface IWorkspaceShell
{
    Window? Window { get; }

    /// <summary>Closes <paramref name="workspace"/> and shows the welcome screen.</summary>
    void CloseWorkspace(WorkspaceViewModel workspace);

    /// <summary>Opens <paramref name="profile"/>/<paramref name="database"/> in this window (replacing) or a new window.</summary>
    void OpenWorkspace(ConnectionProfile profile, ConnectionSecrets secrets, string? database, bool newWindow);

    /// <summary>Shows the connection form for <paramref name="profile"/>. Returns the saved profile, or null when cancelled.</summary>
    ConnectionProfile? EditConnection(ConnectionProfile profile);
}

/// <summary>
/// One workspace = one connection + one database (spec §2). Owns the <see cref="DbSession"/>, the tabs (with the
/// preview-tab rules), the console log and the toolbar state. Connecting runs on a background task; the status shows
/// "connecting…" meanwhile, and the DbSession's own keep-alive timer (connections.keepAlive) marks it Broken when it drops.
/// </summary>
public sealed partial class WorkspaceViewModel : ObservableObject, IWorkspace, ICommandTarget, IAsyncDisposable
{
    private readonly IWorkspaceShell? _shell;
    private readonly ConnectionSecrets _secrets;
    private readonly ObservableCollection<TabViewModelBase> _tabs = [];
    private CancellationTokenSource? _connectCts;
    private bool _closed;
    private int _emptyCounter;

    public WorkspaceViewModel(ConnectionProfile profile, ConnectionSecrets secrets, string? database, IWorkspaceShell? shell)
    {
        _shell = shell;
        _secrets = secrets;
        Profile = profile;
        Database = string.IsNullOrEmpty(database) ? profile.Database : database;
        var settings = AppServices.IsInitialized ? AppServices.Current : new AppSettings();
        Session = new DbSession(profile, secrets, Database, new DbSessionOptions
        {
            KeepAlive = settings.Connections.KeepAlive,
            CommandTimeoutSeconds = settings.Connections.CommandTimeoutSeconds,
        });
        Session.StateChanged += OnSessionStateChanged;
        Session.Log += OnSessionLog;
        Tabs = new ReadOnlyObservableCollection<TabViewModelBase>(_tabs);
        _tabs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTabs));

        var features = AppServices.IsInitialized ? AppServices.Features : null;
        SafeMode = features?.SafeModeFactory(this) ?? new DefaultSafeModeGate(this);
        SafeMode.LevelChanged += (_, _) => OnPropertyChanged(nameof(SafeModeText));
        StatusBrush = ThemeApplier.ParseBrush(profile.Color);
        StatusForeground = ContrastingForeground(StatusBrush);
        TagBrush = ResolveTagBrush(profile.Tag);
        IsSidebarVisible = true;
        RunCommand = new RelayCommand<string>(id => { if (id is not null) ExecuteCommand(id); });
    }

    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];

    public ConnectionProfile Profile { get; private set; }

    public string Database { get; }

    public DbSession Session { get; }

    public HistoryStore History => AppServices.History;

    public ISafeModeGate SafeMode { get; }

    public WorkspaceServices Services { get; } = new();

    public WorkspaceLog ConsoleLog { get; } = new();

    public Window? Window => _shell?.Window;

    public event EventHandler<DbSessionState>? StateChanged;

    public event EventHandler? ReloadRequested;

    public event EventHandler? Closing;

    /// <summary>Executes a command id with this workspace as context (toolbar buttons: <c>CommandParameter="tab.new"</c>).</summary>
    public IRelayCommand<string> RunCommand { get; }

    // ---- connection state ---------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(IsConnecting), nameof(CanReconnectByClick))]
    public partial DbSessionState State { get; private set; } = DbSessionState.Disconnected;

    /// <summary>"connecting…", "connected", "connection lost — click to reconnect"…</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; } = "";

    [ObservableProperty]
    public partial string? LastError { get; private set; }

    public bool IsConnected => State == DbSessionState.Connected;

    public bool IsConnecting => State == DbSessionState.Connecting;

    public bool CanReconnectByClick => State is DbSessionState.Broken or DbSessionState.Disconnected;

    public string? ServerVersion => Session.ServerVersion;

    public string Title => $"{Profile.DisplayName} / {Database}";

    public string HostText => $"{Profile.Host}:{Profile.Port}";

    public string SslText => ConnectionUrl.SslModeText(Profile.SslMode);

    public string SshText => Profile.SshEnabled ? $"yes ({Profile.SshUser}@{Profile.SshHost}:{Profile.SshPort})" : "no";

    public string? Tag => string.IsNullOrWhiteSpace(Profile.Tag) ? null : Profile.Tag;

    public Brush? TagBrush { get; private set; }

    /// <summary>The connection's status colour (null = theme default).</summary>
    public Brush? StatusBrush { get; private set; }

    public Brush? StatusForeground { get; private set; }

    public string SafeModeText => SafeMode.Level switch
    {
        SafeModeLevel.Silent => "Silent",
        SafeModeLevel.Alert1 => "Alert 1",
        SafeModeLevel.Alert2 => "Alert 2",
        SafeModeLevel.Safe1 => "Safe 1",
        SafeModeLevel.Safe2 => "Safe 2",
        _ => SafeMode.Level.ToString(),
    };

    [ObservableProperty]
    public partial bool IsStatusPopupOpen { get; set; }

    // ---- layout -------------------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial bool IsSidebarVisible { get; set; }

    [ObservableProperty]
    public partial bool IsConsoleVisible { get; set; }

    /// <summary>True while any tab reports <see cref="TabViewModelBase.IsBusy"/>.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    // ---- tabs ---------------------------------------------------------------------------------------------------

    public ReadOnlyObservableCollection<TabViewModelBase> Tabs { get; }

    IReadOnlyList<TabViewModelBase> IWorkspace.Tabs => Tabs;

    public bool HasTabs => _tabs.Count > 0;

    [ObservableProperty]
    public partial TabViewModelBase? ActiveTab { get; set; }

    partial void OnActiveTabChanged(TabViewModelBase? oldValue, TabViewModelBase? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsActive = false;
            oldValue.OnDeactivated();
        }
        if (newValue is not null)
        {
            newValue.IsActive = true;
            newValue.OnActivated();
        }
    }

    public TabViewModelBase? OpenTab(TabRequest request)
    {
        var active = ActiveTab is null ? -1 : _tabs.IndexOf(ActiveTab);
        var decision = TabReusePolicy.Decide(_tabs, active, request, i => _tabs[i].Matches(request));
        if (decision.Action == TabOpenAction.ActivateExisting)
        {
            var existing = _tabs[decision.Index];
            if (request.Pinned) existing.IsPreview = false;
            ActiveTab = existing;
            return existing;
        }

        var tab = CreateTab(request);
        if (tab is null) return null;
        tab.IsPreview = TabKinds.IsObjectKind(request.Kind) && !request.Pinned && !request.InNewTab;
        if (request.Title is not null && string.IsNullOrEmpty(tab.Title)) tab.Title = request.Title;

        if (decision.Action == TabOpenAction.Replace)
        {
            var old = _tabs[decision.Index];
            Track(tab);
            _tabs[decision.Index] = tab;
            ActiveTab = tab;
            Untrack(old);
            old.OnClosed(TabCloseReason.Replaced);
        }
        else
        {
            InsertTab(tab, activate: true);
        }
        return tab;
    }

    public void AddTab(TabViewModelBase tab, bool activate = true)
    {
        if (tab.Workspace is null) tab.Attach(this);
        InsertTab(tab, activate);
    }

    public void ActivateTab(TabViewModelBase tab)
    {
        if (_tabs.Contains(tab)) ActiveTab = tab;
    }

    public bool CloseTab(TabViewModelBase tab) => CloseTabCore(tab, TabCloseReason.User);

    public void CloseActiveTab()
    {
        if (ActiveTab is { } t) CloseTab(t);
    }

    public void CloseOthers(TabViewModelBase keep)
    {
        foreach (var t in _tabs.Where(t => t != keep).ToList()) CloseTab(t);
        ActivateTab(keep);
    }

    public void CloseToTheRight(TabViewModelBase tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0) return;
        foreach (var t in _tabs.Skip(index + 1).ToList()) CloseTab(t);
    }

    public void CloseAll()
    {
        foreach (var t in _tabs.ToList()) CloseTab(t);
    }

    public void RenameTab(TabViewModelBase tab)
    {
        var name = Dialogs.PromptText("Rename Tab", "Tab label (empty restores the default):", tab.DisplayTitle, Window);
        if (name is null) return;
        tab.CustomTitle = string.IsNullOrWhiteSpace(name) || name == tab.Title ? null : name.Trim();
    }

    /// <summary>Ctrl+T: a new SQL editor, or an empty tab (<c>general.newTabOpens</c>, or no editor feature yet).</summary>
    public TabViewModelBase NewTab()
    {
        var opens = AppServices.IsInitialized ? AppServices.Current.General.NewTabOpens : NewTabOpens.SqlEditor;
        if (opens == NewTabOpens.SqlEditor && OpenTab(TabRequest.SqlEditor()) is { } editor) return editor;
        return NewEmptyTab();
    }

    /// <summary>Ctrl+E: a new SQL editor (an empty tab until the SQL editor feature registers its factory).</summary>
    public TabViewModelBase NewSqlEditor() => OpenTab(TabRequest.SqlEditor()) ?? NewEmptyTab();

    public void NextTab(int delta)
    {
        if (_tabs.Count == 0) return;
        var i = ActiveTab is null ? 0 : _tabs.IndexOf(ActiveTab);
        ActiveTab = _tabs[((i + delta) % _tabs.Count + _tabs.Count) % _tabs.Count];
    }

    /// <summary>Ctrl+1..9 (1-based).</summary>
    public void GotoTab(int number)
    {
        if (number >= 1 && number <= _tabs.Count) ActiveTab = _tabs[number - 1];
    }

    public void PinTab(TabViewModelBase tab) => tab.IsPreview = false;

    private TabViewModelBase NewEmptyTab()
    {
        var tab = new EmptyTabViewModel();
        if (++_emptyCounter > 1) tab.Title = $"New Tab {_emptyCounter}";
        tab.Attach(this);
        InsertTab(tab, activate: true);
        return tab;
    }

    private TabViewModelBase? CreateTab(TabRequest request)
    {
        TabViewModelBase? tab;
        if (request.Kind == TabKinds.Empty) tab = new EmptyTabViewModel();
        else if (!AppServices.IsInitialized) return null;
        else tab = AppServices.Features.Tabs.Create(request, this);
        tab?.Attach(this);
        return tab;
    }

    private void InsertTab(TabViewModelBase tab, bool activate)
    {
        Track(tab);
        var index = ActiveTab is null ? _tabs.Count : _tabs.IndexOf(ActiveTab) + 1;
        if (index <= 0 || index > _tabs.Count) index = _tabs.Count;
        _tabs.Insert(index, tab);
        if (activate || ActiveTab is null) ActiveTab = tab;
    }

    private bool CloseTabCore(TabViewModelBase tab, TabCloseReason reason)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0) return false;
        if (!tab.CanCloseNow(reason)) return false;
        if (ActiveTab == tab)
            ActiveTab = _tabs.Count > 1 ? _tabs[index + 1 < _tabs.Count ? index + 1 : index - 1] : null;
        _tabs.RemoveAt(index);
        Untrack(tab);
        tab.OnClosed(reason);
        return true;
    }

    private void Track(TabViewModelBase tab) => tab.PropertyChanged += OnTabPropertyChanged;

    private void Untrack(TabViewModelBase tab)
    {
        tab.PropertyChanged -= OnTabPropertyChanged;
        UpdateBusy();
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModelBase.IsBusy)) UpdateBusy();
    }

    private void UpdateBusy() => IsBusy = _tabs.Any(t => t.IsBusy);

    // ---- commands -----------------------------------------------------------------------------------------------

    /// <summary>Runs <paramref name="commandId"/> through the registry with this workspace as context.</summary>
    public bool ExecuteCommand(string commandId, object? parameter = null)
    {
        if (!AppServices.IsInitialized) return false;
        var ctx = new CommandContext
        {
            CommandId = commandId,
            Scope = CommandIds.Get(commandId)?.Scope ?? CommandScope.Workspace,
            Window = Window,
            Workspace = this,
            Target = this,
            Parameter = parameter,
        };
        if (TryExecuteCommand(commandId, ctx)) return true;
        return AppServices.Commands.TryExecute(commandId, ctx);
    }

    /// <summary>Workspace-scope commands are offered to the active tab first (if it implements <see cref="ICommandTarget"/>).</summary>
    public bool TryExecuteCommand(string commandId, CommandContext context) =>
        ActiveTab is ICommandTarget target && target.TryExecuteCommand(commandId, context);

    [RelayCommand]
    private void ToggleStatusPopup()
    {
        if (CanReconnectByClick) ReconnectAsync().Forget();
        else IsStatusPopupOpen = !IsStatusPopupOpen;
    }

    [RelayCommand]
    private async Task SetSafeModeAsync(SafeModeLevel level)
    {
        if (await SafeMode.ChangeLevelAsync(level)) Log($"Safe Mode set to {SafeModeText}.");
    }

    [RelayCommand]
    private void EditConnection()
    {
        IsStatusPopupOpen = false;
        var saved = _shell?.EditConnection(Profile);
        if (saved is null) return;
        Profile = saved;
        StatusBrush = ThemeApplier.ParseBrush(saved.Color);
        StatusForeground = ContrastingForeground(StatusBrush);
        TagBrush = ResolveTagBrush(saved.Tag);
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private void Reconnect()
    {
        IsStatusPopupOpen = false;
        ReconnectAsync().Forget();
    }

    [RelayCommand]
    private void Disconnect()
    {
        IsStatusPopupOpen = false;
        DisconnectAsync().Forget();
    }

    [RelayCommand]
    private void CloseTabItem(TabViewModelBase? tab)
    {
        if (tab is not null) CloseTab(tab);
    }

    // ---- lifecycle ----------------------------------------------------------------------------------------------

    /// <summary>Starts connecting on a background task. Editor text restored by features shows immediately.</summary>
    public Task ConnectAsync()
    {
        _connectCts?.Cancel();
        _connectCts = new CancellationTokenSource();
        var ct = _connectCts.Token;
        State = DbSessionState.Connecting;
        StatusText = "connecting…";
        Log($"Connecting to {Profile.User}@{Profile.Host}:{Profile.Port}/{Database}{(Profile.SshEnabled ? " over SSH" : "")}…");
        return Task.Run(() => ConnectCoreAsync(reconnect: false, ct), ct);
    }

    public Task ReconnectAsync()
    {
        _connectCts?.Cancel();
        _connectCts = new CancellationTokenSource();
        var ct = _connectCts.Token;
        State = DbSessionState.Connecting;
        StatusText = "connecting…";
        Log("Reconnecting…");
        return Task.Run(() => ConnectCoreAsync(reconnect: true, ct), ct);
    }

    private async Task ConnectCoreAsync(bool reconnect, CancellationToken ct)
    {
        try
        {
            if (reconnect) await Session.ReconnectAsync(ct).ConfigureAwait(false);
            else await Session.ConnectAsync(ct).ConfigureAwait(false);
            Log($"Connected. {Session.ServerVersion}");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log("Connection failed: " + ex.Message, ConsoleEntryKind.Error);
            UiDispatch.Run(() => LastError = ex.Message);
        }
    }

    public async Task DisconnectAsync()
    {
        if (_shell is not null) _shell.CloseWorkspace(this);
        else await CloseAsync().ConfigureAwait(true);
    }

    public void OpenDatabase(string database, bool newWindow) =>
        _shell?.OpenWorkspace(Profile, _secrets, database, newWindow);

    public void RequestReload()
    {
        ReloadRequested?.Invoke(this, EventArgs.Empty);
        ActiveTab?.Reload();
    }

    /// <summary>True when any tab has staged changes (the window asks before closing).</summary>
    public bool HasPendingChanges => _tabs.Any(t => t.HasPendingChanges);

    /// <summary>Flushes every tab's autosave state.</summary>
    public void FlushAll()
    {
        foreach (var t in _tabs) t.Flush();
    }

    /// <summary>
    /// Closes the workspace: raises <see cref="Closing"/>, flushes and closes tabs (reason WorkspaceClosing), disposes
    /// feature services and disposes the DB session in the background.
    /// </summary>
    public Task CloseAsync()
    {
        if (_closed) return Task.CompletedTask;
        _closed = true;
        _connectCts?.Cancel();
        Closing?.Invoke(this, EventArgs.Empty);
        foreach (var t in _tabs.ToList())
        {
            t.Flush();
            Untrack(t);
            t.OnClosed(TabCloseReason.WorkspaceClosing);
        }
        _tabs.Clear();
        ActiveTab = null;
        Services.Dispose();
        Session.StateChanged -= OnSessionStateChanged;
        Session.Log -= OnSessionLog;
        return Task.Run(async () => await Session.DisposeAsync().ConfigureAwait(false));
    }

    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);

    public void Log(string message) => Log(message, ConsoleEntryKind.Info);

    public void Log(string message, ConsoleEntryKind kind) => ConsoleLog.Add(message, kind);

    private void OnSessionLog(object? sender, string message) => Log(message, ConsoleEntryKind.Query);

    private void OnSessionStateChanged(object? sender, DbSessionState state) => UiDispatch.Run(() => ApplyState(state));

    private void ApplyState(DbSessionState state)
    {
        if (_closed) return;
        State = state;
        StatusText = state switch
        {
            DbSessionState.Connecting => "connecting…",
            DbSessionState.Connected => "connected",
            DbSessionState.Broken => "connection lost — click to reconnect",
            _ => "disconnected — click to reconnect",
        };
        if (state == DbSessionState.Connected) LastError = null;
        else if (state == DbSessionState.Broken && Session.LastError is { } err) LastError = err;
        OnPropertyChanged(nameof(ServerVersion));
        StateChanged?.Invoke(this, state);
    }

    private static Brush? ResolveTagBrush(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || !AppServices.IsInitialized) return null;
        return AppServices.Current.Tags.TryGetValue(tag, out var hex) ? ThemeApplier.ParseBrush(hex) : null;
    }

    /// <summary>White or black, whichever reads better on <paramref name="background"/>.</summary>
    public static Brush? ContrastingForeground(Brush? background)
    {
        if (background is not SolidColorBrush { Color: var c }) return null;
        var luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;
        return luminance > 0.6 ? Brushes.Black : Brushes.White;
    }
}
