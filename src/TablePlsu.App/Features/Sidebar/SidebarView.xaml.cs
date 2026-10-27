using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TablePlsu.App.Infrastructure;

namespace TablePlsu.App.Features.Sidebar;

/// <summary>
/// Sidebar view: Items/History tabs, search box, flattened virtualized tree (ListBox, extended selection), schema
/// selector popup. Click = preview, double-click = pinned, Enter/Space (sidebar.openItem) open, Left/Right collapse/expand.
/// </summary>
public partial class SidebarView : UserControl
{
    private SidebarViewModel? _vm;
    private bool _restoring;

    public SidebarView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        ItemsSearch.TextChanged += (_, _) => UpdateHint();
        HistorySearch.TextChanged += (_, _) => UpdateHint();
        ItemsList.PreviewKeyDown += OnItemsKeyDown;
        ItemsList.PreviewMouseRightButtonDown += OnListRightButtonDown;
        HistoryList.PreviewMouseRightButtonDown += OnListRightButtonDown;
        ItemsTabButton.IsChecked = true;
    }

    public SidebarViewModel? ViewModel => _vm;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.NodesRebuilt -= OnNodesRebuilt;
            _vm.FocusSearchRequested -= FocusSearch;
            _vm.RevealRequested -= Reveal;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }
        _vm = e.NewValue as SidebarViewModel;
        if (_vm is null) return;
        _vm.NodesRebuilt += OnNodesRebuilt;
        _vm.FocusSearchRequested += FocusSearch;
        _vm.RevealRequested += Reveal;
        _vm.PropertyChanged += OnVmPropertyChanged;
        ApplyTab();
        UpdateStatus();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SidebarViewModel.SelectedTabIndex):
                ApplyTab();
                break;
            case nameof(SidebarViewModel.StatusText):
                UpdateStatus();
                break;
        }
    }

    private bool HistoryActive => _vm?.SelectedTabIndex == SidebarViewModel.HistoryTab;

    private void ApplyTab()
    {
        var history = HistoryActive;
        ItemsTabButton.IsChecked = !history;
        HistoryTabButton.IsChecked = history;
        ItemsList.Visibility = ItemsSearch.Visibility = SchemaBar.Visibility = NewButton.Visibility = history ? Visibility.Collapsed : Visibility.Visible;
        HistoryList.Visibility = HistorySearch.Visibility = history ? Visibility.Visible : Visibility.Collapsed;
        UpdateHint();
        UpdateStatus();
    }

    private void UpdateHint()
    {
        var box = HistoryActive ? HistorySearch : ItemsSearch;
        SearchHint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchHint.Text = HistoryActive ? "Search history" : "Search items";
    }

    private void UpdateStatus() =>
        ItemsStatus.Visibility = !HistoryActive && !string.IsNullOrEmpty(_vm?.StatusText) ? Visibility.Visible : Visibility.Collapsed;

    private void OnItemsTabChecked(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) _vm.SelectedTabIndex = SidebarViewModel.ItemsTab;
    }

    private void OnHistoryTabChecked(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) _vm.SelectedTabIndex = SidebarViewModel.HistoryTab;
    }

    /// <summary>Ctrl+Shift+F: focus the active tab's search box.</summary>
    public void FocusSearch()
    {
        var box = HistoryActive ? HistorySearch : ItemsSearch;
        Dispatcher.BeginInvoke(() =>
        {
            box.Focus();
            Keyboard.Focus(box);
            box.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        var list = box == HistorySearch ? HistoryList : ItemsList;
        switch (e.Key)
        {
            case Key.Escape when box.Text.Length > 0:
                box.Clear();
                e.Handled = true;
                break;
            case Key.Down:
            case Key.Enter:
                if (list.Items.Count == 0) return;
                var first = list.SelectedIndex >= 0 ? list.SelectedIndex : FirstSelectable(list);
                list.SelectedIndex = first;
                FocusItem(list, first);
                if (e.Key == Key.Enter && list == ItemsList && list.SelectedItem is SidebarNode { IsItem: true } n) _vm?.Activate(n);
                e.Handled = true;
                break;
        }
    }

    private static int FirstSelectable(ListBox list)
    {
        for (var i = 0; i < list.Items.Count; i++)
            if (list.Items[i] is SidebarNode { IsItem: true } or HistoryRow) return i;
        return 0;
    }

    private static void FocusItem(ListBox list, int index)
    {
        if (index < 0 || index >= list.Items.Count) return;
        list.ScrollIntoView(list.Items[index]);
        list.UpdateLayout();
        if (list.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem item) item.Focus();
        else list.Focus();
    }

    // ---- Items tree ---------------------------------------------------------------------------------------------

    private void OnItemsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || _restoring) return;
        _vm.SelectedNodes = [.. ItemsList.SelectedItems.OfType<SidebarNode>()];
    }

    private void OnNodesRebuilt(IReadOnlyList<string> selectedKeys)
    {
        if (_vm is null) return;
        var hadFocus = ItemsList.IsKeyboardFocusWithin;
        _restoring = true;
        try
        {
            ItemsList.SelectedItems.Clear();
            if (selectedKeys.Count > 0)
            {
                var keys = new HashSet<string>(selectedKeys, StringComparer.Ordinal);
                foreach (var n in _vm.Nodes)
                    if (keys.Contains(n.Key)) ItemsList.SelectedItems.Add(n);
            }
        }
        finally
        {
            _restoring = false;
        }
        _vm.SelectedNodes = [.. ItemsList.SelectedItems.OfType<SidebarNode>()];
        if (hadFocus && ItemsList.SelectedItem is { } sel)
        {
            var index = ItemsList.Items.IndexOf(sel);
            Dispatcher.BeginInvoke(() => FocusItem(ItemsList, index), DispatcherPriority.Background);
        }
    }

    private void Reveal(string key)
    {
        if (_vm?.Nodes.FirstOrDefault(n => n.Key == key) is not { } node) return;
        ItemsList.SelectedItem = node;
        FocusItem(ItemsList, ItemsList.Items.IndexOf(node));
    }

    private static T? ContainerAt<T>(object source) where T : DependencyObject
    {
        for (var d = source as DependencyObject; d is not null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is T t) return t;
            if (d is ListBox) return null;
        }
        return null;
    }

    private void OnItemsMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null || Keyboard.Modifiers != ModifierKeys.None) return;
        if (ContainerAt<ListBoxItem>(e.OriginalSource)?.DataContext is not SidebarNode node) return;
        if (node.IsHeader) _vm.Toggle(node);
        else if (node.IsItem) _vm.Activate(node);
    }

    private void OnItemsDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null || e.ChangedButton != MouseButton.Left) return;
        if (ContainerAt<ListBoxItem>(e.OriginalSource)?.DataContext is SidebarNode { IsItem: true } node)
        {
            _vm.Activate(node, pinned: true);
            e.Handled = true;
        }
    }

    private void OnItemsKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null || Keyboard.Modifiers != ModifierKeys.None) return;
        if (ItemsList.SelectedItems.Count != 1 || ItemsList.SelectedItem is not SidebarNode node) return;
        switch (e.Key)
        {
            case Key.Right when node.IsExpandable && !node.IsExpanded:
                _vm.SetExpanded(node.Key, true);
                e.Handled = true;
                break;
            case Key.Left when node.IsExpandable && node.IsExpanded:
                _vm.SetExpanded(node.Key, false);
                e.Handled = true;
                break;
        }
    }

    private void OnListRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list || ContainerAt<ListBoxItem>(e.OriginalSource) is not { } item) return;
        if (!item.IsSelected)
        {
            list.SelectedItems.Clear();
            item.IsSelected = true;
        }
        item.Focus();
    }

    private void OnItemsContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_vm is null || ItemsList.ContextMenu is not { } menu) return;
        var refs = _vm.SelectedNodes.Select(n => n.ToRef()).OfType<SidebarItemRef>().ToList();
        if (refs.Count == 0 || !AppServices.IsInitialized)
        {
            e.Handled = true;
            return;
        }
        var items = AppServices.Features.SidebarMenu.Build(new SidebarMenuContext(_vm.Workspace, refs));
        if (items.Count == 0)
        {
            e.Handled = true;
            return;
        }
        menu.Items.Clear();
        foreach (var i in items) menu.Items.Add(i);
    }

    private void OnNewClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var menu = new ContextMenu { PlacementTarget = NewButton, Placement = PlacementMode.Bottom };
        var table = new MenuItem { Header = "New Table…" };
        table.Click += (_, _) => _vm.NewTable();
        menu.Items.Add(table);
        menu.IsOpen = true;
    }

    // ---- schema selector ----------------------------------------------------------------------------------------

    private void OnSchemaButtonClick(object sender, RoutedEventArgs e) => SchemaPopup.IsOpen = !SchemaPopup.IsOpen;

    private void OnSchemasAll(object sender, RoutedEventArgs e) => _vm?.ShowAllSchemas();

    private void OnSchemasNone(object sender, RoutedEventArgs e) => _vm?.SetVisibleSchemas([]);

    // ---- History ------------------------------------------------------------------------------------------------

    private IReadOnlyList<HistoryRow> SelectedHistory => [.. HistoryList.SelectedItems.OfType<HistoryRow>()];

    private void OnHistorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm is not null) _vm.SelectedHistory = SelectedHistory;
    }

    private void OnHistoryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && ContainerAt<ListBoxItem>(e.OriginalSource)?.DataContext is HistoryRow row)
        {
            _vm?.OpenHistory(row);
            e.Handled = true;
        }
    }

    private void OnHistoryOpen(object sender, RoutedEventArgs e)
    {
        foreach (var r in SelectedHistory) _vm?.OpenHistory(r);
    }

    private void OnHistoryRun(object sender, RoutedEventArgs e)
    {
        if (SelectedHistory.FirstOrDefault() is { } r) _vm?.RunHistory(r);
    }

    private void OnHistoryCopy(object sender, RoutedEventArgs e) => _vm?.CopyHistory(SelectedHistory);

    private void OnHistoryDelete(object sender, RoutedEventArgs e) => _vm?.DeleteHistoryAsync(SelectedHistory).Forget();

    private void OnHistoryClear(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (Dialogs.Confirm("Delete the whole query history of this connection?", "Clear History", "Clear All", danger: true))
            _vm.ClearHistoryAsync().Forget();
    }

    private void OnHistoryRefresh(object sender, RoutedEventArgs e) => _vm?.LoadHistoryAsync().Forget();
}
