using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using TablePlsu.App.Infrastructure;
using TablePlsu.App.Shell;

namespace TablePlsu.App;

/// <summary>
/// Startup (architecture §Speed rules 1): load settings, themes and connections (small JSON files), apply the theme and
/// show the window. No DB, network or Npgsql work before first render. <c>--measure-startup</c> prints the milliseconds
/// from process start to the first <c>ContentRendered</c> and exits.
/// </summary>
public partial class App : Application
{
    private bool _measureStartup;
    private readonly List<(string Step, double Ms)> _marks = [];

    private void Mark(string step)
    {
        if (_measureStartup) _marks.Add((step, (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds));
    }

    public App()
    {
        // TABLEPLSU_ROOT redirects all app data (roaming + local) for smoke tests and portable runs.
        if (Environment.GetEnvironmentVariable("TABLEPLSU_ROOT") is { Length: > 0 } root)
            Core.Paths.AppPaths.Current = Core.Paths.AppPaths.ForRoot(root);
        // Settings/themes/connections JSON loads on the thread pool while WPF parses App.xaml and starts up.
        AppServices.Preload();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _measureStartup = e.Args.Any(a => string.Equals(a, "--measure-startup", StringComparison.OrdinalIgnoreCase));

        Mark("OnStartup");
        UiDispatch.Initialize(Dispatcher);
        DispatcherUnhandledException += OnUnhandledException;

        AppServices.Init();
        Mark("services");
        AppServices.Theme.Attach(Resources);
        Mark("theme");

        var window = new MainWindow();
        Mark("window");
        MainWindow = window;
        AppServices.Features.RaiseWindowCreated(window);
        window.ContentRendered += OnFirstContentRendered;
        window.Show();
        Mark("shown");
    }

    private void OnFirstContentRendered(object? sender, EventArgs e)
    {
        var window = (MainWindow)sender!;
        window.ContentRendered -= OnFirstContentRendered;

        if (_measureStartup)
        {
            Mark("rendered");
            ReportStartup(_marks);
            Shutdown(0);
            return;
        }

        // Deferred until the window is interactive.
        Dispatcher.BeginInvoke(() =>
        {
            AppServices.StartBackgroundServices();
            RestoreWorkspaces(window);
        }, DispatcherPriority.ApplicationIdle);
    }

    private static void RestoreWorkspaces(MainWindow window)
    {
        if (!AppServices.Current.General.ReopenWorkspacesOnStartup) return;
        var first = true;
        foreach (var entry in OpenWorkspaces.Load())
        {
            if (AppServices.Connections.Get(entry.ConnectionId) is not { } profile) continue;
            if (first)
            {
                window.ViewModel.ConnectAsync(profile, newWindow: false, entry.Database).Forget();
                first = false;
            }
            else
            {
                var p = profile;
                ShellWindows.OpenNew(vm => vm.ConnectAsync(p, newWindow: false, entry.Database).Forget());
            }
        }
    }

    private static void ReportStartup(List<(string Step, double Ms)> marks)
    {
        var started = Process.GetCurrentProcess().StartTime;
        var ms = (DateTime.Now - started).TotalMilliseconds;
        var npgsqlLoaded = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Npgsql");
        var steps = string.Join(", ", marks.Select(m => $"{m.Step} {m.Ms:F0}"));
        var line = $"startup: {ms:F0} ms to first ContentRendered (npgsql loaded: {(npgsqlLoaded ? "yes" : "no")}; {steps})";

        AttachConsole(-1);
        try
        {
            var stdout = new StreamWriter(System.Console.OpenStandardOutput()) { AutoFlush = true };
            stdout.WriteLine(line);
        }
        catch (Exception)
        {
            // No console attached.
        }
        try
        {
            File.AppendAllText(Path.Combine(AppServices.Paths.LogsDir, "startup.log"), $"{DateTime.Now:O} {line}{Environment.NewLine}");
        }
        catch (Exception)
        {
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppServices.Shutdown();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Trace.WriteLine(e.Exception);
        try
        {
            Directory.CreateDirectory(AppServices.Paths.LogsDir);
            File.AppendAllText(Path.Combine(AppServices.Paths.LogsDir, "errors.log"), $"{DateTime.Now:O} {e.Exception}{Environment.NewLine}");
        }
        catch (Exception)
        {
        }
        Dialogs.Error(e.Exception.Message, "Unexpected error", detail: e.Exception.ToString());
        e.Handled = true;
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
}
