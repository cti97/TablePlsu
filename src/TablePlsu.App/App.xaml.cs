using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using TablePlsu.App.Infrastructure;
using TablePlsu.App.Shell;
using TablePlsu.Core.Startup;

namespace TablePlsu.App;

/// <summary>
/// Startup (architecture §Speed rules 1): load settings, themes and connections (small JSON files), apply the theme and
/// show the window. No DB, network or Npgsql work before first render. <c>--measure-startup</c> prints the milliseconds
/// from process start to the first <c>ContentRendered</c> and exits.
/// </summary>
public partial class App : Application
{
    private bool _measureStartup;
    private bool _background;
    private double _savedLeft, _savedTop;

    /// <summary>Parsed command line (set by <see cref="Program.Main"/>; parsed from the startup args otherwise).</summary>
    internal LaunchArgs? Launch { get; init; }

    /// <summary>The single-instance gate when this process is the primary (null for --measure-startup).</summary>
    internal SingleInstance? Instance { get; init; }
    private static void Mark(string step) => StartupMarks.Mark(step);

    public App()
    {
        // TABLEPLSU_ROOT (portable / smoke-test data root) is applied by Program.Main before this runs.
        // Settings/themes/connections JSON loads on the thread pool while WPF parses App.xaml and starts up.
        AppServices.Preload();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var launch = Launch ?? LaunchArgs.Parse(e.Args);
        _measureStartup = launch.MeasureStartup;
        _background = launch.Background && !_measureStartup;
        StartupMarks.Enabled = _measureStartup;

        Mark("OnStartup");
        UiDispatch.Initialize(Dispatcher);
        DispatcherUnhandledException += OnUnhandledException;
        // Never lose editor text: crashes on any thread and Windows logoff/shutdown write every autosave store first.
        AppDomain.CurrentDomain.UnhandledException += (_, _) => Features.SqlEditor.EditorSessionManager.FlushAll();
        SessionEnding += (_, _) => Features.SqlEditor.EditorSessionManager.FlushAll();

        AppServices.Init();
        Mark("services");
        AppServices.Theme.Attach(Resources);
        Mark("theme");

        var window = new MainWindow();
        Mark("window");
        MainWindow = window;
        AppServices.Features.RaiseWindowCreated(window);
        window.ContentRendered += OnFirstContentRendered;
        StartupWarmup.UseSoftwareFirstFrame(window);
        if (_measureStartup)
        {
            window.SourceInitialized += (_, _) => Mark("hwnd");
            window.LayoutUpdated += (_, _) => Mark("layout");
        }
        if (_background)
        {
            // --background (sign-in start): build and render the window once offscreen so its types, templates and code
            // are warm, then hide it; the first Open is just Show().
            _savedLeft = window.Left;
            _savedTop = window.Top;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000;
            window.Top = -20000;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
        }
        window.Show();
        Mark("shown");
        // Subscribed after Show: subscribing earlier creates the MediaContext (render channel) outside Show.
        if (_measureStartup) System.Windows.Media.CompositionTarget.Rendering += (_, _) => Mark("render");
    }

    private void OnFirstContentRendered(object? sender, EventArgs e)
    {
        var window = (MainWindow)sender!;
        window.ContentRendered -= OnFirstContentRendered;

        if (_measureStartup)
        {
            Mark("rendered");
            // Also time the switch back to hardware rendering (reported as "hw", after the first-render total).
            StartupWarmup.EndFirstWindow();
            window.InvalidateVisual();
            Dispatcher.BeginInvoke(() =>
            {
                Mark("hw");
                ReportStartup(StartupMarks.All);
                Shutdown(0);
            }, DispatcherPriority.ApplicationIdle);
            return;
        }

        if (_background)
        {
            window.Hide();
            window.ShowInTaskbar = true;
            window.ShowActivated = true;
            var area = SystemParameters.WorkArea;
            window.Left = double.IsNaN(_savedLeft) ? area.Left + Math.Max(0, (area.Width - window.Width) / 2) : _savedLeft;
            window.Top = double.IsNaN(_savedTop) ? area.Top + Math.Max(0, (area.Height - window.Height) / 2) : _savedTop;
            // Workspaces (connections, password prompts) wait for the first Open.
            ResidentMode.PendingRestore = RestoreWorkspaces;
        }

        // Deferred until the window is interactive.
        Dispatcher.BeginInvoke(() =>
        {
            StartupWarmup.EndFirstWindow();
            AppServices.StartBackgroundServices();
            ResidentMode.Start(Instance);
            if (_background) return;
            RestoreWorkspaces(window);
            if (Launch?.Url is { } url) ResidentMode.OpenUrl(url, window);
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

    private static void ReportStartup(IReadOnlyList<(string Step, double Ms)> marks)
    {
        var ms = marks.FirstOrDefault(m => m.Step == "rendered").Ms;
        var npgsqlLoaded = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Npgsql");
        var steps = string.Join(", ", marks.Select(m => $"{m.Step} {m.Ms:F0}"));
        // JIT work is a load-independent signal (wall-clock timings swing with machine load): methods compiled at run time
        // and the time spent compiling them, plus assemblies loaded.
        var jitted = System.Runtime.JitInfo.GetCompiledMethodCount();
        var jitMs = System.Runtime.JitInfo.GetCompilationTime().TotalMilliseconds;
        var assemblies = AppDomain.CurrentDomain.GetAssemblies().Length;
        // CPU time is far less sensitive to machine load than wall-clock: UI thread (the critical path) and whole process.
        var uiCpu = StartupMarks.CurrentThreadCpuMs();
        // UI-thread CPU (millions of cycles) at each mark, same order as the wall-clock steps.
        var cpuSteps = string.Join(", ", marks.Select((m, i) => $"{m.Step} {StartupMarks.ThreadCpu[i]:F0}"));
        var cpu = Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds;
        var uiJitted = System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true);
        var uiJitMs = System.Runtime.JitInfo.GetCompilationTime(currentThread: true).TotalMilliseconds;
        var line = $"startup: {ms:F0} ms to first ContentRendered (npgsql loaded: {(npgsqlLoaded ? "yes" : "no")}; jit {jitted} methods {jitMs:F0} ms; ui jit {uiJitted} methods {uiJitMs:F0} ms; assemblies {assemblies}; cpu ui {uiCpu:F0} ms process {cpu:F0} ms; ui mcycles {cpuSteps}; {steps})";

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
        Features.SqlEditor.EditorSessionManager.FlushAll();
        ResidentMode.Shutdown();
        AppServices.Shutdown();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Trace.WriteLine(e.Exception);
        Features.SqlEditor.EditorSessionManager.FlushAll();
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

