using System.ComponentModel;
using System.Windows;
using TablePlsu.App.Infrastructure;

namespace TablePlsu.App.Shell;

/// <summary>A standard (themed) Windows window hosting the welcome screen or one workspace.</summary>
public partial class MainWindow : Window
{
    public MainWindow(MainViewModel? vm = null)
    {
        InitializeComponent();
        ViewModel = vm ?? new MainViewModel();
        ViewModel.Window = this;
        DataContext = ViewModel;
        KeyBindingRouter.Attach(this);
        Deactivated += (_, _) => ViewModel.OnWindowDeactivated();
    }

    public MainViewModel ViewModel { get; }

    /// <summary>True once the window has really closed (not just hidden to the tray).</summary>
    public bool IsClosed { get; private set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (AppServices.IsInitialized) AppServices.Theme.ApplyTitleBar(this);
        StartupMarks.Mark("titlebar");
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;
        // Resident mode: the last window hides to the tray (autosave flushed) instead of closing.
        if (ResidentMode.TryHideOnClose(this))
        {
            e.Cancel = true;
            return;
        }
        // Quit closes every window: all of them stay in the reopen-on-startup list, like a plain exit.
        var isLast = ResidentMode.Quitting
            || Application.Current.Windows.OfType<MainWindow>().Count(w => w != this && w.IsLoaded && !w.IsClosed) == 0;
        if (!ViewModel.OnWindowClosing(isLast)) e.Cancel = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        IsClosed = true;
        base.OnClosed(e);
    }
}

/// <summary>Creates and shows main windows (Ctrl+N, "open in new window").</summary>
public static class ShellWindows
{
    public static MainWindow OpenNew(Action<MainViewModel>? init = null)
    {
        var window = new MainWindow();
        if (AppServices.ActiveWindow is { } current && current.WindowState == WindowState.Normal)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = current.Left + 28;
            window.Top = current.Top + 28;
            window.Width = current.Width;
            window.Height = current.Height;
        }
        AppServices.Features.RaiseWindowCreated(window);
        window.Show();
        init?.Invoke(window.ViewModel);
        return window;
    }
}
