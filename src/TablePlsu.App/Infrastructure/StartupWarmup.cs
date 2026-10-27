using System.IO;
using System.Runtime;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace TablePlsu.App.Infrastructure;

/// <summary>
/// Takes work off the first window's critical path (measured with <c>--measure-startup</c> and dotnet-trace; see
/// scripts/perf-windowshow.ps1):
/// <list type="bullet">
/// <item><b>Multi-core JIT profile</b>: methods the last run needed (JIT, and R2R fixups) are prepared on a background
/// thread while the UI thread starts up (UI-thread work to first frame -25 %).</item>
/// <item><b>Render system warm-up</b>: WPF's process-wide composition setup (render thread, channels, display set:
/// ~170-300 ms of native code inside the first <c>Window.Show</c>) runs on a short-lived background dispatcher thread,
/// in parallel with <c>App</c> construction, App.xaml and the main window's constructor. The UI thread's
/// <c>MediaContext</c> then only opens its channel.</item>
/// <item><b>Software first frame</b>: the first window's first frame is rasterised in software so <c>Window.Show</c>
/// does not wait for the Direct3D device (~120-160 ms); hardware rendering is restored once the window has rendered.</item>
/// </list>
/// Nothing here changes what is drawn or how the app behaves once it is up.
/// </summary>
internal static class StartupWarmup
{
    private static Dispatcher? _renderWarmup;
    private static Window? _softwareWindow;
    private static ManualResetEventSlim? _ready;

    /// <summary>
    /// Starts recording this run's JIT profile and replaying the previous one on a background thread. A measurement run
    /// uses its own profile, so it never replaces the one recorded by normal use.
    /// </summary>
    public static void StartJitProfile(string cacheDir, bool measureRun)
    {
        try
        {
            Directory.CreateDirectory(cacheDir);
            ProfileOptimization.SetProfileRoot(cacheDir);
            ProfileOptimization.StartProfile(measureRun ? "startup-measure.jitprofile" : "startup.jitprofile");
        }
        catch (Exception)
        {
            // Read-only or missing cache folder: start without the profile.
        }
    }

    /// <summary>Before any WPF window exists: start the render warm-up thread.</summary>
    public static void StartRenderWarmup()
    {
        var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                _renderWarmup = Dispatcher.CurrentDispatcher;
                // Subscribing to Rendering creates this thread's MediaContext, which starts the process-wide render
                // system. Unsubscribed at once: no frames are ever ticked here.
                EventHandler tick = static (_, _) => { };
                CompositionTarget.Rendering += tick;
                CompositionTarget.Rendering -= tick;
            }
            catch (Exception)
            {
                // The UI thread's first window starts the render system itself.
            }
            finally
            {
                ready.Set();
            }
            if (_renderWarmup is not null) Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "Render warm-up",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        _ready = ready;
    }

    /// <summary>
    /// Renders the first window's first frame in software (process-wide flag: only it keeps Show from waiting for the
    /// render thread's Direct3D set-up; the per-window <see cref="HwndTarget.RenderMode"/> does not).
    /// </summary>
    public static void UseSoftwareFirstFrame(Window window)
    {
        if (RenderOptions.ProcessRenderMode != RenderMode.Default) return;
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        _softwareWindow = window;
    }

    /// <summary>
    /// After the first window has rendered: hardware rendering back on for it (its render target is re-created; the
    /// Direct3D device set-up happens now instead of inside Show) and the warm-up thread shut down (the UI thread's
    /// MediaContext keeps the render system alive).
    /// </summary>
    public static void EndFirstWindow()
    {
        if (_softwareWindow is { } window)
        {
            _softwareWindow = null;
            RenderOptions.ProcessRenderMode = RenderMode.Default;
            // The process flag is only read when a render target is created: toggle the window's own mode so its target
            // is re-created with hardware rendering.
            if (PresentationSource.FromVisual(window) is HwndSource { CompositionTarget: { } target } && target.RenderMode == RenderMode.Default)
            {
                target.RenderMode = RenderMode.SoftwareOnly;
                target.RenderMode = RenderMode.Default;
            }
        }
        if (_ready is { } ready)
        {
            _ready = null;
            ready.Wait();
            ready.Dispose();
            _renderWarmup?.BeginInvokeShutdown(DispatcherPriority.Send);
            _renderWarmup = null;
        }
    }
}
