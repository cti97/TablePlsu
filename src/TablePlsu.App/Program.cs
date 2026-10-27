using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TablePlsu.Core.Paths;
using TablePlsu.Core.Startup;

namespace TablePlsu.App;

/// <summary>
/// Entry point. Before any WPF type loads: parse the args and take the single-instance gate. A second launch forwards its
/// args to the running instance over a named pipe and exits (a few ms of managed code), so double-clicking the exe, a
/// <c>postgresql://</c> URL or the tray-resident instance never start a second process. <c>--measure-startup</c> always
/// runs a fresh, standalone process.
/// </summary>
public static class Program
{
    private const int AsfwAny = -1;

    [STAThread]
    public static int Main(string[] args)
    {
        var mainAt = DateTime.UtcNow;
        // TABLEPLSU_ROOT redirects all app data (roaming + local) for smoke tests and portable runs; it also scopes the
        // single-instance name, so such runs never talk to the user's normal instance.
        if (Environment.GetEnvironmentVariable("TABLEPLSU_ROOT") is { Length: > 0 } root)
            AppPaths.Current = AppPaths.ForRoot(root);

        var launch = LaunchArgs.Parse(args);
        SingleInstance? instance = null;
        if (!launch.MeasureStartup)
        {
            // USERDOMAIN from the environment: Environment.UserDomainName may look the account up (slow on domain machines).
            var user = Environment.GetEnvironmentVariable("USERDOMAIN") + "\\" + Environment.UserName;
            var name = SingleInstance.NameFor(user, AppPaths.Current.Roaming);
            instance = SingleInstance.Acquire(name);
            if (!instance.IsPrimary)
            {
                if (launch.Background) return 0; // already running
                // Let the running instance take the foreground (this process has the right, it was just launched).
                AllowSetForegroundWindow(AsfwAny);
                string[] forwarded = launch.MeasureActivation
                    ? [.. args, "--launched-at=" + ProcessStartUtcTicks(), "--main-at=" + mainAt.Ticks]
                    : args;
                if (SingleInstance.TrySend(name, new LaunchMessage(forwarded, Environment.CurrentDirectory), TimeSpan.FromSeconds(5)))
                    return 0;
                // No answer (the other process is hung or exiting): run on our own rather than not at all.
                instance.Dispose();
                instance = null;
            }
        }
        if (launch.Quit) return 0; // nothing to quit
        return RunApp(launch, instance);
    }

    // Separate, never inlined: compiling Main must not resolve WPF types, so a forwarding launch never loads WPF.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunApp(LaunchArgs launch, SingleInstance? instance)
    {
        // Off the critical path: the JIT profile replays on a background thread and WPF's render system starts on another
        // while the UI thread builds App and the main window (Infrastructure/StartupWarmup).
        Infrastructure.StartupWarmup.StartJitProfile(AppPaths.Current.CacheDir, launch.MeasureStartup);
        Infrastructure.StartupWarmup.StartRenderWarmup();
        var app = new App { Launch = launch, Instance = instance };
        app.InitializeComponent();
        return app.Run();
    }

    private static long ProcessStartUtcTicks() =>
        GetProcessTimes(GetCurrentProcess(), out var creation, out _, out _, out _)
            ? DateTime.FromFileTimeUtc(creation).Ticks
            : DateTime.UtcNow.Ticks;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
}
