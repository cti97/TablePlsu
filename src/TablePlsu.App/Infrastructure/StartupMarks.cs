using System.Diagnostics;

namespace TablePlsu.App.Infrastructure;

/// <summary>
/// Startup phase marks for <c>--measure-startup</c> (milliseconds since process start). Off (a single bool test) in
/// normal runs. Each step is recorded once: later calls with the same name are ignored, so hot paths (layout, render)
/// can mark their first occurrence cheaply.
/// </summary>
internal static class StartupMarks
{
    private static readonly List<(string Step, double Ms)> _marks = [];
    private static readonly List<double> _cpu = [];
    private static DateTime _processStart;

    public static bool Enabled { get; set; }

    public static IReadOnlyList<(string Step, double Ms)> All => _marks;

    /// <summary>
    /// CPU cycles (millions) used by the marking thread at each mark (<c>QueryThreadCycleTime</c>: exact, not rounded to
    /// the 15.6 ms scheduler tick like GetThreadTimes, and unaffected by time spent waiting or preempted by other load).
    /// </summary>
    public static IReadOnlyList<double> ThreadCpu => _cpu;

    public static double CurrentThreadMegaCycles() =>
        QueryThreadCycleTime(GetCurrentThread(), out var cycles) ? cycles / 1e6 : double.NaN;

    public static double CurrentThreadCpuMs() =>
        GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user) ? (kernel + user) / 10_000.0 : double.NaN;

    public static void Mark(string step)
    {
        if (!Enabled) return;
        foreach (var m in _marks)
            if (m.Step == step) return;
        if (_processStart == default) _processStart = Process.GetCurrentProcess().StartTime;
        _marks.Add((step, (DateTime.Now - _processStart).TotalMilliseconds));
        _cpu.Add(CurrentThreadMegaCycles());
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);
}
