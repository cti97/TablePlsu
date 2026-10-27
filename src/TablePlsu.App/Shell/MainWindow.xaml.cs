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

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (AppServices.IsInitialized) AppServices.Theme.ApplyTitleBar(this);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;
        var isLast = Application.Current.Windows.OfType<MainWindow>().Count(w => w != this && w.IsLoaded) == 0;
        if (!ViewModel.OnWindowClosing(isLast)) e.Cancel = true;
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
