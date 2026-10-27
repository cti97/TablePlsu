using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Connections;
using TablePlsu.Core.Settings;
using TablePlsu.Core.Startup;

namespace TablePlsu.App.Shell;

/// <summary>
/// Single instance + opt-in resident mode (<c>general.keepRunningInTray</c>, <c>general.startWithWindows</c>).
/// <list type="bullet">
/// <item>Always: a second launch forwards its args over the instance pipe (see <see cref="SingleInstance"/>) and exits;
/// this process then activates a window (restoring a hidden one, or opening a new one when none exists) and connects
/// to a <c>postgresql://</c> URL if one was passed. Without resident mode this is the only change from a plain app:
/// a double launch brings the existing window forward instead of starting a second process.</item>
/// <item>Resident: closing the last visible window flushes every autosave store and hides it (workspace, tabs and
/// connection stay alive) behind a tray icon (Open / New Window / Quit). Quit closes every window the normal way
/// (pending-change prompts, autosave flush, workspaces remembered for <c>reopenWorkspacesOnStartup</c>) and exits.</item>
/// <item><c>startWithWindows</c> registers <c>HKCU\…\Run</c> with <c>--background</c>: the app starts hidden with the main
/// window already built and rendered once offscreen (types loaded, templates applied, code jitted), so the first Open is
/// a Show(). Workspaces are restored on that first Open, not at sign-in (no surprise password prompts).</item>
/// </list>
/// </summary>
public static class ResidentMode
{
    private static TrayIcon? _tray;
    private static SingleInstance? _instance;
    private static bool _started;
    private static bool _syncRunKey = true;

    /// <summary>Set while Quit / session end closes the windows: closing then really closes.</summary>
    public static bool Quitting { get; private set; }

    /// <summary>Restore of the last workspaces deferred by a <c>--background</c> start; runs on the first Open.</summary>
    internal static Action<MainWindow>? PendingRestore { get; set; }

    public static bool IsEnabledBy(AppSettings s) => s.General.KeepRunningInTray || s.General.StartWithWindows;

    public static bool IsEnabled => AppServices.IsInitialized && IsEnabledBy(AppServices.Current);

    /// <summary>Hide instead of close: resident, not quitting, and no other main window (visible or hidden) is left.</summary>
    internal static bool ShouldHideOnClose(bool enabled, bool quitting, int otherMainWindows) =>
        enabled && !quitting && otherMainWindows == 0;

    private static IEnumerable<MainWindow> MainWindows =>
        Application.Current?.Windows.OfType<MainWindow>().Where(w => !w.IsClosed) ?? [];

    /// <summary>After first render: listen for second launches, apply the tray / Run-key settings, follow changes live.</summary>
    public static void Start(SingleInstance? instance)
    {
        if (_started) return;
        _started = true;
        _instance = instance;
        // A portable / test data root must never register itself at sign-in.
        _syncRunKey = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TABLEPLSU_ROOT"));
        if (instance is { IsPrimary: true })
        {
            var dispatcher = Application.Current.Dispatcher;
            instance.StartListening(m => dispatcher.BeginInvoke(() => HandleLaunch(m), DispatcherPriority.Send));
        }
        AppServices.SettingsChanged += (_, _) => Apply();
        Application.Current.SessionEnding += (_, _) =>
        {
            Quitting = true;
            FlushAll();
        };
        Apply();
    }

    /// <summary>Creates/removes the tray icon and syncs the Run key for the current settings.</summary>
    public static void Apply()
    {
        var s = AppServices.Current;
        if (IsEnabledBy(s))
        {
            if (_tray is null)
            {
                try
                {
                    _tray = new TrayIcon("TablePlsu");
                    _tray.Activated += (_, _) => ShowWindow();
                    _tray.Menu = BuildMenu();
                }
                catch (Exception ex)
                {
                    Log("tray icon failed: " + ex.Message);
                }
            }
        }
        else
        {
            _tray?.Dispose();
            _tray = null;
            // No way back to a hidden window without the icon: bring it back.
            if (MainWindows.Any() && !MainWindows.Any(w => w.IsVisible)) ShowWindow();
        }

        if (_syncRunKey && Environment.ProcessPath is { } exe)
        {
            try { RunAtLogin.Sync(s.General.StartWithWindows, exe); }
            catch (Exception ex) { Log("startWithWindows: " + ex.Message); }
        }
    }

    private static ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("Open", () => ShowWindow(), bold: true));
        menu.Items.Add(Item("New Window", () => Activate(ShellWindows.OpenNew())));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Quit TablePlsu", Quit));
        return menu;

        static MenuItem Item(string header, Action action, bool bold = false)
        {
            var item = new MenuItem { Header = header };
            if (bold) item.FontWeight = FontWeights.SemiBold;
            item.Click += (_, _) => action();
            return item;
        }
    }

    /// <summary>Called by <see cref="MainWindow"/> while closing. True when the window was hidden instead.</summary>
    internal static bool TryHideOnClose(MainWindow window)
    {
        var others = MainWindows.Count(w => !ReferenceEquals(w, window));
        if (!ShouldHideOnClose(IsEnabled, Quitting, others)) return false;
        FlushAll();
        window.Hide();
        return true;
    }

    /// <summary>Writes every window's autosave state (SQL editor text, session.json) synchronously.</summary>
    public static void FlushAll()
    {
        foreach (var w in MainWindows)
        {
            try { w.ViewModel.FlushAutosave(); }
            catch (Exception ex) { Log("flush failed: " + ex.Message); }
        }
        // Also wait for every autosave store's queued writes to reach disk (session.json, in-flight debounced saves).
        Features.SqlEditor.EditorSessionManager.FlushAll();
    }

    /// <summary>Brings a window forward: the active/visible one, else the hidden one, else a new one.</summary>
    public static MainWindow ShowWindow()
    {
        var windows = MainWindows.ToList();
        var target = windows.FirstOrDefault(w => w.IsVisible && w.IsActive)
            ?? windows.LastOrDefault(w => w.IsVisible)
            ?? windows.FirstOrDefault();
        if (target is null) target = ShellWindows.OpenNew();
        else if (!target.IsVisible) target.Show();
        Activate(target);
        if (PendingRestore is { } restore)
        {
            PendingRestore = null;
            restore(target);
        }
        return target;
    }

    private static void Activate(MainWindow w)
    {
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
        w.Focus();
    }

    /// <summary>Handles a launch forwarded by a second process (UI thread).</summary>
    public static void HandleLaunch(LaunchMessage message)
    {
        var launch = LaunchArgs.Parse(message.Args);
        if (launch.Quit)
        {
            Quit();
            return;
        }
        if (launch.Background) return; // already running: a sign-in start has nothing to add

        var wasHidden = !MainWindows.Any(w => w.IsVisible);
        var window = ShowWindow();
        if (launch.Url is { } url) OpenUrl(url, window);
        if (launch.MeasureActivation) ReportActivation(message, window, wasHidden);
    }

    /// <summary>Connects to a <c>postgresql://</c> URL: in <paramref name="window"/> when it shows the welcome screen, else in a new window.</summary>
    public static void OpenUrl(string url, MainWindow window)
    {
        if (!ConnectionUrl.TryParse(url, out var parsed, out var error))
        {
            Dialogs.Error(error, "Invalid connection URL", window);
            return;
        }
        var secrets = new ConnectionSecrets(parsed.Password, parsed.SshPassword, null);
        var newWindow = window.ViewModel.Workspace is not null;
        window.ViewModel.OpenWorkspace(parsed.Profile, secrets, null, newWindow);
    }

    /// <summary>
    /// Closes every main window the normal way (hidden windows with staged changes are shown first so the prompt has
    /// context), then exits. Cancelling a prompt cancels the quit.
    /// </summary>
    public static void Quit()
    {
        Quitting = true;
        FlushAll();
        foreach (var w in MainWindows.ToList())
        {
            if (!w.IsVisible && w.ViewModel.HasPendingChanges) w.Show();
            w.Close();
            if (!w.IsClosed)
            {
                Quitting = false;
                return;
            }
        }
        Shutdown();
        Application.Current.Shutdown();
    }

    public static void Shutdown()
    {
        _tray?.Dispose();
        _tray = null;
        _instance?.Dispose();
        _instance = null;
    }

    private static void ReportActivation(LaunchMessage message, MainWindow window, bool wasHidden)
    {
        var launchedAt = Ticks(message, "--launched-at=");
        var mainAt = Ticks(message, "--main-at=");
        var receivedAt = DateTime.UtcNow.Ticks;
        // Visible = after layout and the render pass that follow Show (ContextIdle runs after Render).
        window.Dispatcher.BeginInvoke(() =>
        {
            var ms = launchedAt > 0 ? (DateTime.UtcNow - new DateTime(launchedAt, DateTimeKind.Utc)).TotalMilliseconds : double.NaN;
            var main = launchedAt > 0 && mainAt > 0 ? (mainAt - launchedAt) / 10_000.0 : double.NaN;
            var received = launchedAt > 0 ? (receivedAt - launchedAt) / 10_000.0 : double.NaN;
            Log($"activation: {ms:F0} ms from second launch to window visible (second process reached Main at {main:F0} ms, " +
                $"message received at {received:F0} ms; window was {(wasHidden ? "hidden" : "visible")})", "startup.log");
            if (wasHidden) window.Hide(); // ready for the next measurement run
        }, DispatcherPriority.ContextIdle);
    }

    private static long Ticks(LaunchMessage message, string prefix) =>
        message.Args.Select(a => a.StartsWith(prefix, StringComparison.Ordinal) && long.TryParse(a[prefix.Length..], out var t) ? t : 0)
            .FirstOrDefault(t => t > 0);

    private static void Log(string line, string file = "errors.log")
    {
        try
        {
            Directory.CreateDirectory(AppServices.Paths.LogsDir);
            File.AppendAllText(Path.Combine(AppServices.Paths.LogsDir, file), $"{DateTime.Now:O} {line}{Environment.NewLine}");
        }
        catch (Exception)
        {
            Trace.WriteLine(line);
        }
    }
}
