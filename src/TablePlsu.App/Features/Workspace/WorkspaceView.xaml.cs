using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Connections;
using TablePlsu.Core.Settings;

namespace TablePlsu.App.Features.Workspace;

/// <summary>
/// Workspace layout: toolbar (built from <c>settings.toolbar.items</c> and <see cref="ToolbarRegistry"/>), left sidebar
/// slot, tab strip, active tab host and bottom console slot. Panels come from <see cref="WorkspacePanelRegistry"/>.
/// </summary>
public partial class WorkspaceView : UserControl
{
    private WorkspaceViewModel? _vm;
    private GridLength _sidebarWidth = new(260);
    private GridLength _consoleHeight = new(200);

    public WorkspaceView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) =>
        {
            AppServices.SettingsChanged += OnSettingsChanged;
            FocusContent();
        };
        Unloaded += (_, _) => AppServices.SettingsChanged -= OnSettingsChanged;
    }

    /// <summary>Moves keyboard focus into the workspace (the active tab's view, else the tab strip).</summary>
    public void FocusContent()
    {
        if (_vm?.ActiveTab is { } tab && Host.GetView(tab) is { } view && view.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)))
            return;
        TabStrip.Focus();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = e.NewValue as WorkspaceViewModel;
        if (_vm is null) return;
        _vm.PropertyChanged += OnVmPropertyChanged;
        BuildToolbar();
        BuildPanels();
        ApplySidebarVisibility();
        ApplyConsoleVisibility();
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e) => BuildToolbar();

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(WorkspaceViewModel.IsSidebarVisible):
                ApplySidebarVisibility();
                break;
            case nameof(WorkspaceViewModel.IsConsoleVisible):
                ApplyConsoleVisibility();
                break;
            case nameof(WorkspaceViewModel.ActiveTab):
                if (_vm?.ActiveTab is { } tab) TabStrip.ScrollIntoView(tab);
                break;
        }
    }

    private void BuildToolbar()
    {
        ToolbarPanel.Children.Clear();
        ToolbarPanel.ColumnDefinitions.Clear();
        if (_vm is null || !AppServices.IsInitialized) return;
        var configured = AppServices.Current.Toolbar.Items;
        if (configured.Count == 0) configured = [.. ToolbarItemIds.Known];
        var items = AppServices.Features.Toolbar.Layout(configured);
        var hasStretch = items.Any(i => i.Stretch);
        foreach (var item in items)
        {
            FrameworkElement element;
            try
            {
                element = item.Create(_vm);
            }
            catch (Exception ex)
            {
                _vm.Log($"Toolbar item '{item.Id}' failed: {ex.Message}", ConsoleEntryKind.Error);
                continue;
            }
            element.VerticalAlignment = VerticalAlignment.Center;
            ToolbarPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = item.Stretch ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            Grid.SetColumn(element, ToolbarPanel.ColumnDefinitions.Count - 1);
            ToolbarPanel.Children.Add(element);
        }
        if (!hasStretch) ToolbarPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
    }

    private void BuildPanels()
    {
        if (_vm is null) return;
        SidebarHost.Content = CreatePanel(WorkspacePanel.Sidebar, "Sidebar");
        ConsoleHost.Content = CreatePanel(WorkspacePanel.Console, "Console");
    }

    private FrameworkElement CreatePanel(WorkspacePanel panel, string placeholder)
    {
        if (AppServices.IsInitialized && AppServices.Features.Panels.IsRegistered(panel))
        {
            try
            {
                if (AppServices.Features.Panels.Create(panel, _vm!) is { } element) return element;
            }
            catch (Exception ex)
            {
                _vm!.Log($"{placeholder} panel failed: {ex.Message}", ConsoleEntryKind.Error);
            }
        }
        var text = new TextBlock
        {
            Text = placeholder,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)FindResource("MutedText"),
        };
        return new Grid { Children = { text } };
    }

    private void ApplySidebarVisibility()
    {
        var visible = _vm?.IsSidebarVisible ?? true;
        if (!visible && SidebarColumn.Width.Value > 0) _sidebarWidth = SidebarColumn.Width;
        SidebarColumn.Width = visible ? _sidebarWidth : new GridLength(0);
        SidebarSplitterColumn.Width = new GridLength(visible ? 4 : 0);
        SidebarBorder.Visibility = SidebarSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyConsoleVisibility()
    {
        var visible = _vm?.IsConsoleVisible ?? false;
        if (!visible && ConsoleRow.Height.Value > 0) _consoleHeight = ConsoleRow.Height;
        ConsoleRow.Height = visible ? _consoleHeight : new GridLength(0);
        ConsoleSplitterRow.Height = new GridLength(visible ? 4 : 0);
        ConsoleBorder.Visibility = ConsoleSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- tab strip ----------------------------------------------------------------------------------------------

    private static TabViewModelBase? TabOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as TabViewModelBase;

    private void OnTabMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && TabOf(sender) is { } tab)
        {
            _vm?.CloseTab(tab);
            e.Handled = true;
        }
    }

    private void OnTabDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && TabOf(sender) is { } tab) _vm?.PinTab(tab);
    }

    private void OnTabStripWheel(object sender, MouseWheelEventArgs e)
    {
        if (FindScrollViewer(TabStrip) is not { } sv) return;
        sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject d)
    {
        if (d is ScrollViewer sv) return sv;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
            if (FindScrollViewer(System.Windows.Media.VisualTreeHelper.GetChild(d, i)) is { } found) return found;
        return null;
    }

    private static TabViewModelBase? MenuTab(object sender) =>
        ((sender as MenuItem)?.Parent as ContextMenu)?.PlacementTarget is FrameworkElement { DataContext: TabViewModelBase t } ? t : null;

    private void OnTabMenuNew(object sender, RoutedEventArgs e) => _vm?.NewTab();

    private void OnTabMenuClose(object sender, RoutedEventArgs e)
    {
        if (MenuTab(sender) is { } t) _vm?.CloseTab(t);
    }

    private void OnTabMenuCloseOthers(object sender, RoutedEventArgs e)
    {
        if (MenuTab(sender) is { } t) _vm?.CloseOthers(t);
    }

    private void OnTabMenuCloseRight(object sender, RoutedEventArgs e)
    {
        if (MenuTab(sender) is { } t) _vm?.CloseToTheRight(t);
    }

    private void OnTabMenuCloseAll(object sender, RoutedEventArgs e) => _vm?.CloseAll();

    private void OnTabMenuRename(object sender, RoutedEventArgs e)
    {
        if (MenuTab(sender) is { } t) _vm?.RenameTab(t);
    }

    // ---- safe mode ----------------------------------------------------------------------------------------------

    private void OnSafeModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || _vm is null) return;
        var menu = (ContextMenu)FindResource("SafeModeMenu");
        foreach (var item in menu.Items.OfType<MenuItem>())
            item.IsChecked = item.Tag is SafeModeLevel l && l == _vm.SafeMode.Level;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnSafeModeLevelClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: SafeModeLevel level } && _vm is not null)
            _vm.SetSafeModeCommand.Execute(level);
    }
}
