using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using TablePlsu.App.Features.Results;
using TablePlsu.App.Features.Settings;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Settings;
using TablePlsu.Core.Themes;

namespace TablePlsu.App.Tests.Perf;

/// <summary>Shared plumbing for the end-to-end perf tests: a UI thread with a live dispatcher, an off-screen window, a stall probe.</summary>
internal static class PerfHarness
{
    /// <summary>
    /// Runs <paramref name="body"/> on an STA thread whose dispatcher is pumping (awaits come back to it) and points
    /// <see cref="UiDispatch"/> at it, like the real app. UiDispatch is reset afterwards.
    /// </summary>
    public static void OnUiThread(Func<Task> body, int timeoutSeconds = 240)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            UiDispatch.Initialize(dispatcher);
            try
            {
                var frame = new DispatcherFrame();
                var task = body();
                task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
                Dispatcher.PushFrame(frame);
                error = task.Exception?.GetBaseException();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                UiDispatch.Initialize(null!);
                dispatcher.InvokeShutdown();
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        if (!t.Join(TimeSpan.FromSeconds(timeoutSeconds))) throw new TimeoutException("UI-thread test body did not finish.");
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    public static Window Host(FrameworkElement content, int width = 1200, int height = 800)
    {
        var window = new Window
        {
            Width = width, Height = height, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        window.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/TablePlsu.App;component/Shell/Styles.xaml", UriKind.Relative)));
        ThemeApplier.ApplyTo(window.Resources, ThemeService.BuiltInDark);
        GridResources.ApplyTo(window.Resources, new GridSettings());
        SettingsResources.ApplyTo(window.Resources, new AppSettings());
        window.Content = content;
        window.Show();
        return window;
    }

    /// <summary>Lets queued work at <paramref name="priority"/> and above run.</summary>
    public static Task Yield(DispatcherPriority priority = DispatcherPriority.ApplicationIdle) =>
        Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, priority).Task;

    /// <summary>Completes on the first <c>CompositionTarget.Rendering</c> at which <paramref name="condition"/> holds.</summary>
    public static Task<TimeSpan> WhenRendered(Func<bool> condition, Stopwatch clock, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<TimeSpan>();
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (clock.Elapsed > timeout) { CompositionTarget.Rendering -= handler; tcs.TrySetException(new TimeoutException("never rendered")); return; }
            if (!condition()) return;
            CompositionTarget.Rendering -= handler;
            tcs.TrySetResult(clock.Elapsed);
        };
        CompositionTarget.Rendering += handler;
        return tcs.Task;
    }
}

/// <summary>Times every dispatcher operation (diagnostics: names the slowest ones).</summary>
internal sealed class OpProfiler : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<DispatcherOperation, long> _started = [];
    private readonly List<(double Ms, string What)> _ops = [];
    private readonly Dictionary<string, (double Ms, int N)> _totals = [];

    /// <summary>The op kinds with the most total UI-thread time.</summary>
    public string Totals(int n = 4) => string.Join("; ", _totals.OrderByDescending(t => t.Value.Ms).Take(n).Select(t => $"{t.Key}: {t.Value.N} ops, {t.Value.Ms:F0} ms"));

    public OpProfiler(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        dispatcher.Hooks.OperationStarted += OnStarted;
        dispatcher.Hooks.OperationCompleted += OnCompleted;
    }

    private void OnStarted(object? sender, DispatcherHookEventArgs e) => _started[e.Operation] = Stopwatch.GetTimestamp();

    private void OnCompleted(object? sender, DispatcherHookEventArgs e)
    {
        if (!_started.Remove(e.Operation, out var t0)) return;
        var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        var d = e.Operation.GetType().GetField("_method", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(e.Operation) as Delegate;
        var what = $"{e.Operation.Priority} {d?.Method.DeclaringType?.Name}.{d?.Method.Name}";
        _totals[what] = (_totals.GetValueOrDefault(what).Ms + ms, _totals.GetValueOrDefault(what).N + 1);
        if (ms >= 15) _ops.Add((ms, what));
    }

    public string Top(int n = 5) => string.Join("; ", _ops.OrderByDescending(o => o.Ms).Take(n).Select(o => $"{o.Ms:F0} ms {o.What}"));

    public void Reset() { _ops.Clear(); _totals.Clear(); }

    public void Dispose()
    {
        _dispatcher.Hooks.OperationStarted -= OnStarted;
        _dispatcher.Hooks.OperationCompleted -= OnCompleted;
    }
}

/// <summary>
/// UI-thread heartbeat: a background thread posts a probe at Input priority every few ms (after the previous one ran)
/// and records how long each waited. The max wait is the longest the UI thread was unable to react to input.
/// </summary>
internal sealed class StallProbe : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Thread _thread;
    private volatile bool _stop;
    private double _max;
    private readonly List<double> _samples = [];

    public StallProbe(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _thread = new Thread(Loop) { IsBackground = true, Name = "stall probe" };
        _thread.Start();
    }

    public double MaxStallMs { get { lock (_samples) return _max; } }

    public int Over(double ms) { lock (_samples) return _samples.Count(s => s > ms); }

    public void Reset() { lock (_samples) { _max = 0; _samples.Clear(); } }

    private void Loop()
    {
        while (!_stop)
        {
            var posted = Stopwatch.GetTimestamp();
            var op = _dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                var waited = Stopwatch.GetElapsedTime(posted).TotalMilliseconds;
                lock (_samples)
                {
                    _samples.Add(waited);
                    if (waited > _max) _max = waited;
                }
            });
            if (op.Wait(TimeSpan.FromSeconds(10)) != DispatcherOperationStatus.Completed) return;
            Thread.Sleep(4);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(1000);
    }
}
