using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TablePlsu.App.Features.Results;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Commands;
using TablePlsu.Core.Data;

namespace TablePlsu.App.Features.TableData;

/// <summary>The table tab's view: filter bar, editable ResultGrid, bottom tabs, paging. Logic lives in <see cref="TableTabViewModel"/>.</summary>
public partial class TableTabView : UserControl, ITableDataView
{
    private static readonly ConditionalWeakTable<DataGridRow, StrikeThroughAdorner> Strikes = new();
    private TableTabViewModel? _vm;
    private Popup? _pagePopup;

    public TableTabView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as TableTabViewModel);
        DataGridView.SortRequested += (_, e) => _vm?.OnSortRequested(e.ColumnName, e.AddToMultiSort);
        DataGridView.CellEditStarting += (_, e) => e.Cancel = _vm is null || !_vm.CanEditCell(e.Row, e.ColumnIndex);
        DataGridView.CellEditCommitted += (_, e) =>
        {
            if (_vm is null) { e.Cancel = true; return; }
            e.NewValue = _vm.CommitCellEdit(e.Row, e.ColumnIndex, e.NewText);
        };
        DataGridView.DataGrid.PreviewKeyDown += OnGridPreviewKeyDown;
        DataGridView.DataGrid.LoadingRow += (_, e) => Dispatcher.BeginInvoke(() => UpdateStrike(e.Row), DispatcherPriority.Loaded);
        DataGridView.DataGrid.PreviewMouseRightButtonUp += OnGridRightClick;
    }

    public ResultGrid Grid => DataGridView;

    private void Attach(TableTabViewModel? vm)
    {
        if (_vm is not null && ReferenceEquals(_vm.View, this)) _vm.View = null;
        _vm = vm;
        if (vm is null) return;
        vm.View = this;
        Dispatcher.BeginInvoke(() => DataGridView.SetSortIndicators(vm.SortIndicators()), DispatcherPriority.Loaded);
    }

    // ---- ITableDataView -------------------------------------------------------------------------------------------

    public void FocusFilterBar()
    {
        Dispatcher.BeginInvoke(() =>
        {
            var row = _vm?.Filter.Current;
            if (row is null) return;
            if (FilterRows.ItemContainerGenerator.ContainerFromItem(row) is not DependencyObject container) return;
            var box = FindChildren<TextBox>(container).FirstOrDefault(t => t.IsVisible) ?? (Control?)FindChildren<ComboBox>(container).FirstOrDefault();
            box?.Focus();
        }, DispatcherPriority.Loaded);
    }

    public void RefreshRowVisuals()
    {
        DataGridView.RefreshVisibleRows();
        foreach (var row in FindChildren<DataGridRow>(DataGridView.DataGrid)) UpdateStrike(row);
    }

    public void ScrollToRow(int rowIndex, int columnIndex = 0)
    {
        Dispatcher.BeginInvoke(() =>
        {
            DataGridView.ScrollToRow(rowIndex, columnIndex);
            DataGridView.DataGrid.Focus();
        }, DispatcherPriority.Background);
    }

    public void ShowSetValueMenu()
    {
        var menu = BuildSetValueMenu();
        var target = CurrentCellElement() ?? (UIElement)DataGridView.DataGrid;
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // ---- grid input -----------------------------------------------------------------------------------------------

    private void OnGridPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Enter edits the current cell (spec §6 Inline edit); while editing, the grid commits as usual.
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && _vm is { IsEditable: true } && !DataGridView.DataGrid.IsEditing())
        {
            if (DataGridView.BeginEditCurrentCell()) e.Handled = true;
        }
    }

    private void UpdateStrike(DataGridRow row)
    {
        var deleted = _vm?.Pending is { } p && row.Item is object?[] item && p.IsDeleted(item);
        Strikes.TryGetValue(row, out var existing);
        if (deleted == (existing is not null)) return;
        var layer = AdornerLayer.GetAdornerLayer(row);
        if (layer is null) return;
        if (deleted)
        {
            var adorner = new StrikeThroughAdorner(row);
            layer.Add(adorner);
            Strikes.AddOrUpdate(row, adorner);
        }
        else
        {
            layer.Remove(existing);
            Strikes.Remove(row);
        }
    }

    private void OnGridRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null) return;
        var source = e.OriginalSource as DependencyObject;
        var header = FindAncestor<DataGridColumnHeader>(source);
        if (header?.Column is { } column)
        {
            var index = ResultGrid.ColumnIndexOf(column);
            if (index < 0 || _vm.Columns is null || index >= _vm.Columns.Count) return;
            ShowHeaderMenu(header, _vm.Columns[index].Name);
            e.Handled = true;
            return;
        }
        var cell = FindAncestor<DataGridCell>(source);
        if (cell is null) return;
        if (!cell.IsSelected)
        {
            DataGridView.DataGrid.SelectedCells.Clear();
            var info = new DataGridCellInfo(cell);
            DataGridView.DataGrid.CurrentCell = info;
            DataGridView.DataGrid.SelectedCells.Add(info);
        }
        var menu = BuildCellMenu();
        menu.PlacementTarget = cell;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void ShowHeaderMenu(FrameworkElement target, string column)
    {
        var vm = _vm!;
        var menu = new ContextMenu();
        menu.Items.Add(Item("Sort Ascending", () => vm.SetSort(column, SortDirection.Asc, false)));
        menu.Items.Add(Item("Sort Descending", () => vm.SetSort(column, SortDirection.Desc, false)));
        menu.Items.Add(Item("Remove Sort", () => vm.SetSort(column, null, false), vm.Sorts.Any(s => s.Column == column)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Add to Sort (Ascending)", () => vm.SetSort(column, SortDirection.Asc, true)));
        menu.Items.Add(Item("Add to Sort (Descending)", () => vm.SetSort(column, SortDirection.Desc, true)));
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private ContextMenu BuildCellMenu()
    {
        var vm = _vm!;
        var menu = new ContextMenu();
        var setValue = new MenuItem { Header = "Set Value", InputGestureText = Gesture(CommandIds.GridSetValueMenu), IsEnabled = vm.IsEditable };
        foreach (var item in SetValueItems()) setValue.Items.Add(item);
        menu.Items.Add(setValue);
        menu.Items.Add(new Separator());
        menu.Items.Add(Cmd("Copy", CommandIds.GridCopy, () => DataGridView.CopyRows()));
        menu.Items.Add(Cmd("Copy Selected Cells", CommandIds.GridCopyCells, () => DataGridView.CopySelectedCells()));
        menu.Items.Add(Cmd("Paste as New Rows", CommandIds.GridPaste, () => vm.TryExecuteCommand(CommandIds.GridPaste, Ctx(CommandIds.GridPaste)), vm.IsEditable));
        menu.Items.Add(Cmd("Paste into Cells", CommandIds.GridPasteCells, () => vm.TryExecuteCommand(CommandIds.GridPasteCells, Ctx(CommandIds.GridPasteCells)), vm.IsEditable));
        menu.Items.Add(new Separator());
        menu.Items.Add(Cmd("Insert Row", CommandIds.GridInsertRow, () => vm.InsertRow(), vm.IsEditable));
        menu.Items.Add(Cmd("Duplicate Rows", CommandIds.GridDuplicateRow, vm.DuplicateSelectedRows, vm.IsEditable));
        menu.Items.Add(Cmd("Delete Rows", CommandIds.GridDeleteRow, vm.DeleteSelectedRows, vm.IsEditable));
        return menu;
    }

    private CommandContext Ctx(string id) => CommandContext.For(id, DataGridView);

    private ContextMenu BuildSetValueMenu()
    {
        var menu = new ContextMenu();
        foreach (var item in SetValueItems()) menu.Items.Add(item);
        return menu;
    }

    private IEnumerable<MenuItem> SetValueItems()
    {
        var vm = _vm!;
        yield return Item("NULL", () => vm.SetSelectedCells(null));
        yield return Item("DEFAULT", () => vm.SetSelectedCells(SqlDefault.Instance));
        yield return Item("EMPTY", () => vm.SetSelectedCells(""));
        if (vm.CurrentColumnIsTemporal()) yield return Item("NOW()", () => vm.SetSelectedCells(SqlExpression.Now));
    }

    private static MenuItem Item(string header, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    private static MenuItem Cmd(string header, string commandId, Action action, bool enabled = true)
    {
        var item = Item(header, action, enabled);
        item.InputGestureText = Gesture(commandId);
        return item;
    }

    private static string Gesture(string commandId) => ShortcutText.Gesture(commandId);

    private FrameworkElement? CurrentCellElement()
    {
        var info = DataGridView.DataGrid.CurrentCell;
        if (info.Column is null || info.Item is null) return null;
        return info.Column.GetCellContent(info.Item)?.Parent as FrameworkElement ?? info.Column.GetCellContent(info.Item);
    }

    // ---- paging popover -------------------------------------------------------------------------------------------

    private void OnPageButtonClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var limit = new TextBox { Width = 90, Text = _vm.PageSize.ToString(CultureInfo.InvariantCulture), Margin = new Thickness(0, 0, 0, 6) };
        var offset = new TextBox { Width = 90, Text = _vm.Offset.ToString(CultureInfo.InvariantCulture), Margin = new Thickness(0, 0, 0, 10) };
        var go = new Button { Content = "Go", MinWidth = 70, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        go.SetResourceReference(StyleProperty, "AccentButton");
        var grid = new System.Windows.Controls.Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var i = 0; i < 3; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddCell(grid, new TextBlock { Text = "Limit", Margin = new Thickness(0, 3, 10, 0) }, 0, 0);
        AddCell(grid, limit, 0, 1);
        AddCell(grid, new TextBlock { Text = "Offset", Margin = new Thickness(0, 3, 10, 0) }, 1, 0);
        AddCell(grid, offset, 1, 1);
        AddCell(grid, go, 2, 1);
        var border = new Border { Child = grid };
        border.SetResourceReference(StyleProperty, "PopoverBorder");
        _pagePopup = new Popup
        {
            Child = border,
            PlacementTarget = PageButton,
            Placement = PlacementMode.Top,
            StaysOpen = false,
            AllowsTransparency = true,
        };
        void Apply()
        {
            if (int.TryParse(limit.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)
                && long.TryParse(offset.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var o))
            {
                _vm.GoTo(l, o);
                _pagePopup!.IsOpen = false;
            }
        }
        go.Click += (_, _) => Apply();
        border.PreviewKeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter) { Apply(); ke.Handled = true; }
            else if (ke.Key == Key.Escape) { _pagePopup!.IsOpen = false; ke.Handled = true; }
        };
        _pagePopup.Opened += (_, _) => { limit.Focus(); limit.SelectAll(); };
        _pagePopup.IsOpen = true;
    }

    private static void AddCell(System.Windows.Controls.Grid grid, UIElement element, int row, int col)
    {
        System.Windows.Controls.Grid.SetRow(element, row);
        System.Windows.Controls.Grid.SetColumn(element, col);
        grid.Children.Add(element);
    }

    // ---- filter bar -----------------------------------------------------------------------------------------------

    private void OnFilterBarPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (e.OriginalSource is ComboBox { IsDropDownOpen: true }) return;
            _vm.Filter.IsVisible = false;
            DataGridView.FocusGrid();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (e.OriginalSource is ComboBox { IsDropDownOpen: true }) return;
            var row = (e.OriginalSource as FrameworkElement)?.DataContext as FilterRowViewModel ?? _vm.Filter.Current;
            if (row is null) return;
            CommitFocusedText();
            _vm.Filter.ApplyRow(row);
            e.Handled = true;
        }
    }

    private static void CommitFocusedText()
    {
        if (Keyboard.FocusedElement is TextBox tb) BindingOperations.GetBindingExpression(tb, TextBox.TextProperty)?.UpdateSource();
    }

    private void OnFilterRowFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_vm is not null && sender is FrameworkElement { DataContext: FilterRowViewModel row }) _vm.Filter.Current = row;
    }

    private void OnAddFilterRowClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not FrameworkElement { DataContext: FilterRowViewModel row }) return;
        _vm.Filter.AddRow(row);
        FocusFilterBar();
    }

    private void OnRemoveFilterRowClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not FrameworkElement { DataContext: FilterRowViewModel row }) return;
        _vm.Filter.RemoveRow(row);
    }

    private void OnApplyAllClick(object sender, RoutedEventArgs e)
    {
        CommitFocusedText();
        _vm?.Filter.ApplyAll();
    }

    private void OnClearFiltersClick(object sender, RoutedEventArgs e) => _vm?.Filter.Clear();

    private void OnShowWhereClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        CommitFocusedText();
        var where = TableQueryBuilder.BuildWhere(_vm.BuildQuery() with { Filters = _vm.Filter.CheckedConditions(), Join = _vm.Filter.Join });
        var text = string.IsNullOrEmpty(where.LiteralSql) ? "-- no filter" : where.LiteralSql;
        Dialogs.Info("Generated WHERE clause for the checked filters:", "Filter SQL", detail: text);
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null and not T)
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    private static IEnumerable<T> FindChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        var n = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) yield return t;
            foreach (var c in FindChildren<T>(child)) yield return c;
        }
    }
}

/// <summary>Draws a line through a staged-deleted row (spec §6 Row operations: "shown struck through").</summary>
public sealed class StrikeThroughAdorner : Adorner
{
    public StrikeThroughAdorner(UIElement adorned) : base(adorned) => IsHitTestVisible = false;

    protected override void OnRender(DrawingContext dc)
    {
        var brush = TryFindResource("c.grid.foreground") as Brush ?? Brushes.Gray;
        var pen = new Pen(brush, 1);
        pen.Freeze();
        var size = AdornedElement.RenderSize;
        var header = AdornedElement is DataGridRow row && FindGrid(row) is { } g ? g.RowHeaderActualWidth : 0;
        var y = Math.Round(size.Height / 2) + 0.5;
        dc.DrawLine(pen, new Point(header + 2, y), new Point(Math.Max(header + 2, size.Width - 2), y));
    }

    private static DataGrid? FindGrid(DependencyObject d)
    {
        for (var p = VisualTreeHelper.GetParent(d); p is not null; p = VisualTreeHelper.GetParent(p))
            if (p is DataGrid g) return g;
        return null;
    }
}

/// <summary>Visible when every bound value is true.</summary>
public sealed class AllTrueToVisibilityConverter : IMultiValueConverter
{
    public static AllTrueToVisibilityConverter Instance { get; } = new();

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.All(v => v is true) ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>FilterJoin ↔ ComboBox index (0 = AND, 1 = OR).</summary>
public sealed class JoinIndexConverter : IValueConverter
{
    public static JoinIndexConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is FilterJoin.Or ? 1 : 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is 1 ? FilterJoin.Or : FilterJoin.And;
}
