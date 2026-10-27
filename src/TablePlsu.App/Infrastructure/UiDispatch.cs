using System.Diagnostics;
using System.Windows.Threading;

namespace TablePlsu.App.Infrastructure;

/// <summary>
/// Marshalling to the UI thread. <see cref="Initialize"/> is called by App startup; before that (unit tests) actions run inline.
/// </summary>
public static class UiDispatch
{
    private static Dispatcher? _dispatcher;

    public static void Initialize(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public static Dispatcher? Dispatcher => _dispatcher;

    public static bool CheckAccess() => _dispatcher is null || _dispatcher.CheckAccess();

    /// <summary>Runs now when on the UI thread, otherwise posts.</summary>
    public static void Run(Action action, DispatcherPriority priority = DispatcherPriority.Normal)
    {
        if (CheckAccess()) action();
        else _dispatcher!.BeginInvoke(action, priority);
    }

    /// <summary>Always posts (even from the UI thread), so the caller's stack unwinds first.</summary>
    public static void Post(Action action, DispatcherPriority priority = DispatcherPriority.Normal)
    {
        if (_dispatcher is null) action();
        else _dispatcher.BeginInvoke(action, priority);
    }

    /// <summary>Posts at Background priority: the batch-marshalling rule for streamed results (architecture §Speed rules 3).</summary>
    public static void PostBackground(Action action) => Post(action, DispatcherPriority.Background);

    public static Task InvokeAsync(Action action, DispatcherPriority priority = DispatcherPriority.Normal)
    {
        if (_dispatcher is null || _dispatcher.CheckAccess() && priority == DispatcherPriority.Send)
        {
            action();
            return Task.CompletedTask;
        }
        return _dispatcher.InvokeAsync(action, priority).Task;
    }

    public static Task<T> InvokeAsync<T>(Func<T> func, DispatcherPriority priority = DispatcherPriority.Normal)
    {
        if (_dispatcher is null) return Task.FromResult(func());
        return _dispatcher.InvokeAsync(func, priority).Task;
    }

    /// <summary>Observes a fire-and-forget task so failures are logged instead of crashing the finalizer thread.</summary>
    public static async void Forget(this Task task, Action<Exception>? onError = null)
    {
        try
        {
            await task.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Trace.WriteLine("Background task failed: " + ex);
            onError?.Invoke(ex);
        }
    }
}
