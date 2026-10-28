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
    private readonly EditableGridController _editing;
    private TableTabViewModel? _vm;
    private Popup? _pagePopup;

    public TableTabView()
    {
        InitializeComponent();
        _editing = new EditableGridController(DataGridView, () => _vm);
        DataContextChanged += (_, _) => Attach(DataContext as TableTabViewModel);
        DataGridView.SortRequested += (_, e) => _vm?.OnSortRequested(e.ColumnName, e.AddToMultiSort);
        DataGridView.DataGrid.PreviewMouseRightButtonUp += OnGridRightClick;
    }

    public ResultGrid Grid => DataGridView;

    private void Attach(TableTabViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.ReadOnlyAttempted -= OnReadOnlyAttempted;
            if (ReferenceEquals(_vm.View, this)) _vm.View = null;
        }
        _vm = vm;
        if (vm is null) return;
        vm.View = this;
        vm.ReadOnlyAttempted += OnReadOnlyAttempted;
        Dispatcher.BeginInvoke(() => DataGridView.SetSortIndicators(vm.SortIndicators()), DispatcherPriority.Loaded);
    }

    private void OnReadOnlyAttempted(object? sender, EventArgs e) => EditableGridController.Flash(ReadOnlyBanner);

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

    public void RefreshRowVisuals() => _editing.RefreshRowVisuals();

    public void ScrollToRow(int rowIndex, int columnIndex = 0)
    {
        Dispatcher.BeginInvoke(() =>
        {
            DataGridView.ScrollToRow(rowIndex, columnIndex);
            DataGridView.DataGrid.Focus();
        }, DispatcherPriority.Background);
    }

    public void ShowSetValueMenu() => _editing.ShowSetValueMenu();

    // ---- grid input -----------------------------------------------------------------------------------------------

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
        _editing.ShowCellMenu(e);
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

    private static MenuItem Item(string header, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
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
        string text;
        try
        {
            var where = TableQueryBuilder.BuildWhere(_vm.BuildQuery() with { Filters = _vm.Filter.CheckedConditions(), Join = _vm.Filter.Join });
            text = string.IsNullOrEmpty(where.LiteralSql) ? "-- no filter" : where.LiteralSql;
        }
        catch (ArgumentException ex)
        {
            text = "-- " + ex.Message;
        }
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
