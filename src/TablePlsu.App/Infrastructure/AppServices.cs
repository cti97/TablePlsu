using System.Windows;
using TablePlsu.Core.Connections;
using TablePlsu.Core.History;
using TablePlsu.Core.Keymap;
using TablePlsu.Core.Paths;
using TablePlsu.Core.Settings;
using TablePlsu.Core.Themes;

namespace TablePlsu.App.Infrastructure;

/// <summary>
/// Static composition root holding the app-wide singletons. <see cref="Init"/> does only small synchronous file reads
/// (settings, themes, connections) and registers features; it never touches the DB, the network or Npgsql types.
/// Settings changes (file watcher) are marshalled to the UI thread, rebuild the keymap and re-apply the theme.
/// </summary>
public static class AppServices
{
    private static KeymapService? _keymap;

    public static bool IsInitialized { get; private set; }

    public static AppPaths Paths { get; private set; } = null!;
    public static SettingsService Settings { get; private set; } = null!;
    public static ThemeService Themes { get; private set; } = null!;
    public static ThemeApplier Theme { get; private set; } = null!;
    public static SecretStore Secrets { get; private set; } = null!;
    public static ConnectionStore Connections { get; private set; } = null!;
    public static HistoryStore History { get; private set; } = null!;
    public static CommandRegistry Commands { get; private set; } = null!;
    public static FeatureContext Features { get; private set; } = null!;

    public static KeymapService Keymap => _keymap ?? throw new InvalidOperationException("AppServices.Init has not run.");

    internal static KeymapService? KeymapOrNull => _keymap;

    /// <summary>"Title (Ctrl+X)" by command id, for tooltips. Refreshes when the keymap changes.</summary>
    public static ShortcutText Shortcuts { get; } = new(withTitle: true);

    /// <summary>"Ctrl+X" by command id, for menu gesture text. Refreshes when the keymap changes.</summary>
    public static ShortcutText Gestures { get; } = new(withTitle: false);

    /// <summary>Current settings (shortcut for <c>Settings.Current</c>).</summary>
    public static AppSettings Current => Settings.Current;

    /// <summary>Raised on the UI thread after settings were reloaded (keymap and theme already updated).</summary>
    public static event EventHandler<SettingsChangedEventArgs>? SettingsChanged;

    /// <summary>Raised on the UI thread after the keymap was rebuilt.</summary>
    public static event EventHandler? KeymapChanged;

    private sealed record CoreServices(
        AppPaths Paths, SettingsService Settings, ThemeService Themes, SecretStore Secrets,
        ConnectionStore Connections, HistoryStore History, KeymapService Keymap);

    private static Task<CoreServices>? _preload;

    /// <summary>
    /// Starts loading the file-backed services (settings, themes, connections, keymap) on a thread-pool thread so the
    /// JSON work overlaps WPF's own startup. Called from the App constructor; <see cref="Init"/> picks up the result.
    /// </summary>
    public static void Preload(AppPaths? paths = null)
    {
        var p = paths ?? AppPaths.Current;
        _preload = Task.Run(() => LoadCore(p));
    }

    /// <summary>
    /// Loads settings, themes and connections (or takes the <see cref="Preload"/> result) and registers
    /// <paramref name="features"/> (default: <see cref="Infrastructure.Features.CreateAll"/>). Tests pass a temp <paramref name="paths"/>.
    /// </summary>
    public static void Init(AppPaths? paths = null, IEnumerable<IFeature>? features = null)
    {
        var preload = Interlocked.Exchange(ref _preload, null);
        var core = paths is null && preload is not null
            ? preload.GetAwaiter().GetResult()
            : LoadCore(paths ?? AppPaths.Current);

        Settings?.Dispose();
        Paths = core.Paths;
        Settings = core.Settings;
        Themes = core.Themes;
        Theme = new ThemeApplier(Themes, () => Settings.Current.Theme);
        Secrets = core.Secrets;
        Connections = core.Connections;
        History = core.History;
        _keymap = core.Keymap;

        Commands = new CommandRegistry();
        Features = new FeatureContext(Commands);
        foreach (var feature in features ?? Infrastructure.Features.CreateAll())
            feature.Register(Features);

        Settings.Changed -= OnSettingsChanged;
        Settings.Changed += OnSettingsChanged;
        IsInitialized = true;
    }

    /// <summary>Work deferred until after first render: settings file watching and following the Windows theme.</summary>
    public static void StartBackgroundServices()
    {
        Settings.StartWatching();
        Theme.WatchSystemTheme();
    }

    public static void Shutdown()
    {
        if (!IsInitialized) return;
        Theme.StopWatchingSystemTheme();
        Settings.Dispose();
    }

    /// <summary>Re-applies settings after a reload (also used by tests and the settings editor after a save).</summary>
    public static void ApplySettings(SettingsChangedEventArgs e)
    {
        _keymap = new KeymapService(e.Settings.Keymap);
        Shortcuts.Refresh();
        Gestures.Refresh();
        KeymapChanged?.Invoke(null, EventArgs.Empty);

        ApplyHistorySettings(e.Settings);
        Themes.Reload();
        Theme.Apply();

        SettingsChanged?.Invoke(null, e);
    }

    /// <summary>Tests: drops every SettingsChanged/KeymapChanged subscriber (views left behind by earlier tests on dead threads).</summary>
    internal static void ClearSubscribersForTests()
    {
        SettingsChanged = null;
        KeymapChanged = null;
    }

    /// <summary>The window that should own dialogs: the active one, else the main window.</summary>
    public static Window? ActiveWindow
    {
        get
        {
            var app = Application.Current;
            if (app is null) return null;
            foreach (Window w in app.Windows)
                if (w.IsActive) return w;
            return app.MainWindow;
        }
    }

    private static CoreServices LoadCore(AppPaths paths)
    {
        paths.EnsureCreated();
        var settings = new SettingsService(paths);
        settings.Load();
        var themes = new ThemeService(paths);
        var secrets = new SecretStore(paths);
        var connections = new ConnectionStore(paths, secrets);
        connections.Load();
        var history = new HistoryStore(paths)
        {
            Enabled = settings.Current.History.Enabled,
            MaxEntries = settings.Current.History.MaxEntries,
        };
        var keymap = new KeymapService(settings.Current.Keymap);
        return new CoreServices(paths, settings, themes, secrets, connections, history, keymap);
    }

    private static void OnSettingsChanged(object? sender, SettingsChangedEventArgs e) => UiDispatch.Run(() => ApplySettings(e));

    private static void ApplyHistorySettings(AppSettings s)
    {
        History.Enabled = s.History.Enabled;
        History.MaxEntries = s.History.MaxEntries;
    }
}
