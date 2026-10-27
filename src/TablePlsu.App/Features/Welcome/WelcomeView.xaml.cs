using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TablePlsu.App.Infrastructure;

namespace TablePlsu.App.Features.Welcome;

/// <summary>Welcome screen. DataContext: <see cref="WelcomeViewModel"/>.</summary>
public partial class WelcomeView : UserControl
{
    private WelcomeViewModel? _vm;

    public WelcomeView()
    {
        StartupMarks.Mark("welcome.ctor");
        InitializeComponent();
        StartupMarks.Mark("welcome.xaml");
        DataContextChanged += (_, e) =>
        {
            if (_vm is not null) _vm.FocusSearchRequested -= OnFocusSearch;
            _vm = e.NewValue as WelcomeViewModel;
            if (_vm is not null) _vm.FocusSearchRequested += OnFocusSearch;
        };
        Loaded += (_, _) => StartupMarks.Mark("loaded");
        Loaded += (_, _) => Dispatcher.BeginInvoke(FocusList, DispatcherPriority.Input);
    }

    private void FocusList()
    {
        if (_vm?.IsFormOpen == true) return;
        if (ConnectionList.SelectedItem is { } item && ConnectionList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem lbi)
            lbi.Focus();
        else
            SearchBox.Focus();
    }

    private void OnFocusSearch(object? sender, EventArgs e)
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                _vm?.ConnectCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Down:
                FocusList();
                e.Handled = true;
                break;
            case Key.Escape when SearchBox.Text.Length > 0:
                SearchBox.Clear();
                e.Handled = true;
                break;
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            _vm?.ConnectCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if ((e.OriginalSource as DependencyObject) is { } d && ItemsControl.ContainerFromElement(ConnectionList, d) is ListBoxItem { DataContext: ConnectionItemViewModel item })
            _vm?.ConnectCommand.Execute(item);
    }

    private static ConnectionItemViewModel? MenuItemTarget(object sender) =>
        ((sender as MenuItem)?.Parent as ContextMenu)?.PlacementTarget is FrameworkElement { DataContext: ConnectionItemViewModel i } ? i : null;

    private void OnMenuConnect(object sender, RoutedEventArgs e) => _vm?.ConnectCommand.Execute(MenuItemTarget(sender));

    private void OnMenuConnectNewWindow(object sender, RoutedEventArgs e)
    {
        if (MenuItemTarget(sender) is { } item && Window.GetWindow(this)?.DataContext is Shell.MainViewModel shell)
            _ = shell.ConnectAsync(item.Profile, newWindow: true);
    }

    private void OnMenuEdit(object sender, RoutedEventArgs e) => _vm?.EditCommand.Execute(MenuItemTarget(sender));

    private void OnMenuDuplicate(object sender, RoutedEventArgs e) => _vm?.DuplicateCommand.Execute(MenuItemTarget(sender));

    private void OnMenuDelete(object sender, RoutedEventArgs e) => _vm?.DeleteCommand.Execute(MenuItemTarget(sender));
}
