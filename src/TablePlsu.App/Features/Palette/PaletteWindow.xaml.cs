using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TablePlsu.App.Features.Sidebar;
using TablePlsu.App.Infrastructure;

namespace TablePlsu.App.Features.Palette;

/// <summary>
/// The palette overlay: a borderless window owned by the main window, placed near the top of it, closed when it loses
/// activation. It has no key router attached, so Ctrl+N/Ctrl+P/Tab/Enter reach its own handler.
/// </summary>
public partial class PaletteWindow : Window
{
    private readonly PaletteViewModel _vm;
    private bool _closing;

    public PaletteWindow(PaletteViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Title = vm.Mode switch { PaletteMode.Databases => "Open Database", PaletteMode.Connections => "Open Connection", _ => "Open Anything" };
        vm.CloseRequested += CloseQuietly;
        vm.PropertyChanged += OnVmPropertyChanged;
        PreviewKeyDown += OnKeyDown;
        Deactivated += (_, _) => CloseQuietly();
        Closing += (_, _) => _closing = true;
        Closed += (_, _) =>
        {
            _closing = true;
            if (Current == this) Current = null;
            _vm.Dispose();
        };
        Input.TextChanged += (_, _) => UpdateHint();
        Loaded += (_, _) =>
        {
            Input.Focus();
            Keyboard.Focus(Input);
            UpdateHint();
            UpdateStatus();
        };
        if (vm.Mode == PaletteMode.Databases) List.ContextMenu = BuildDatabaseMenu();
    }

    public PaletteViewModel ViewModel => _vm;

    /// <summary>The open palette, if any (one at a time).</summary>
    public static PaletteWindow? Current { get; private set; }

    /// <summary>Opens the palette over <paramref name="owner"/> (closing any open one first).</summary>
    public static PaletteWindow Open(PaletteMode mode, IWorkspace? workspace, Window? owner)
    {
        Current?.CloseQuietly();
        var vm = new PaletteViewModel(mode, workspace, owner);
        var w = new PaletteWindow(vm);
        if (owner is { IsLoaded: true })
        {
            w.Owner = owner;
            var width = Math.Min(w.Width, Math.Max(360, owner.ActualWidth - 40));
            w.Width = width;
            var left = owner.WindowState == WindowState.Maximized ? 0 : owner.Left;
            var top = owner.WindowState == WindowState.Maximized ? 0 : owner.Top;
            w.Left = left + (owner.ActualWidth - width) / 2;
            w.Top = top + 56;
        }
        else
        {
            w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        Current = w;
        w.Show();
        w.Activate();
        return w;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PaletteViewModel.SelectedIndex):
                if (_vm.Selected is { } row) List.ScrollIntoView(row);
                break;
            case nameof(PaletteViewModel.StatusText):
                UpdateStatus();
                break;
        }
    }

    private void UpdateHint() => Hint.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateStatus() => Status.Visibility = string.IsNullOrEmpty(_vm.StatusText) ? Visibility.Collapsed : Visibility.Visible;

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Down:
                _vm.MoveSelection(1);
                break;
            case Key.Up:
                _vm.MoveSelection(-1);
                break;
            case Key.N when ctrl:
                _vm.MoveSelection(1);
                break;
            case Key.P when ctrl:
                _vm.MoveSelection(-1);
                break;
            case Key.PageDown:
                _vm.MoveSelection(10);
                break;
            case Key.PageUp:
                _vm.MoveSelection(-10);
                break;
            case Key.Tab:
                _vm.Complete();
                Input.CaretIndex = Input.Text.Length;
                break;
            case Key.Enter:
                _vm.Invoke(newTab: ctrl, newWindow: shift);
                break;
            case Key.Escape:
                if (_vm.Escape()) CloseQuietly();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void OnListClick(object sender, MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d is not null && d != List; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is ListBoxItem { DataContext: PaletteRow row })
            {
                _vm.Invoke(newTab: Keyboard.Modifiers.HasFlag(ModifierKeys.Control), newWindow: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), row: row);
                e.Handled = true;
                return;
            }
        }
    }

    private ContextMenu BuildDatabaseMenu()
    {
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "Open in Current Window", InputGestureText = "Enter" };
        open.Click += (_, _) => _vm.Invoke();
        var newWindow = new MenuItem { Header = "Open in New Window", InputGestureText = "Shift+Enter" };
        newWindow.Click += (_, _) => _vm.Invoke(newWindow: true);
        var copy = new MenuItem { Header = "Copy Name" };
        copy.Click += (_, _) =>
        {
            if (_vm.Selected is { } r) SidebarActions.SetClipboard(r.Title);
        };
        menu.Items.Add(open);
        menu.Items.Add(newWindow);
        menu.Items.Add(copy);
        return menu;
    }

    private void CloseQuietly()
    {
        if (_closing) return;
        if (List.ContextMenu?.IsOpen == true) return;
        _closing = true;
        if (Current == this) Current = null;
        var owner = Owner;
        _vm.Dispose();
        Close();
        try
        {
            if (owner is { IsLoaded: true, IsVisible: true }) owner.Activate();
        }
        catch (InvalidOperationException)
        {
            // The owner is closing too.
        }
    }
}
