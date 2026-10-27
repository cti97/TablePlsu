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
        // TABLEPLSU_ROOT redirects all app data (roaming + local) for smoke tests and portable runs; it also scopes the
        // single-instance name, so such runs never talk to the user's normal instance.
        if (Environment.GetEnvironmentVariable("TABLEPLSU_ROOT") is { Length: > 0 } root)
            AppPaths.Current = AppPaths.ForRoot(root);

        var launch = LaunchArgs.Parse(args);
        SingleInstance? instance = null;
        if (!launch.MeasureStartup)
        {
            var name = SingleInstance.NameFor(Environment.UserDomainName + "\\" + Environment.UserName, AppPaths.Current.Roaming);
            instance = SingleInstance.Acquire(name);
            if (!instance.IsPrimary)
            {
                if (launch.Background) return 0; // already running
                // Let the running instance take the foreground (this process has the right, it was just launched).
                AllowSetForegroundWindow(AsfwAny);
                string[] forwarded = launch.MeasureActivation ? [.. args, "--launched-at=" + ProcessStartUtcTicks()] : args;
                if (SingleInstance.TrySend(name, new LaunchMessage(forwarded, Environment.CurrentDirectory), TimeSpan.FromSeconds(5)))
                    return 0;
                // No answer (the other process is hung or exiting): run on our own rather than not at all.
                instance.Dispose();
                instance = null;
            }
        }
        if (launch.Quit) return 0; // nothing to quit

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
