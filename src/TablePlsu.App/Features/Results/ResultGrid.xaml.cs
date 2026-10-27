using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using TablePlsu.Core.Db;

namespace TablePlsu.App.Features.Results;

/// <summary>
/// Virtualized result grid over <c>IList</c> of <c>object?[]</c> rows (row + column virtualization, recycling, fixed
/// row height). Columns come from <see cref="ResultColumns"/> and bind with <c>Binding("[i]")</c> + the shared
/// <see cref="CellValueConverter"/>. Use a <see cref="BulkObservableCollection{T}"/> for <see cref="Rows"/> to append
/// streamed batches cheaply. Read-only by default; with <see cref="IsReadOnly"/> false, cell edits are reported through
/// <see cref="CellEditCommitted"/> and staged state is drawn from <see cref="RowStateProvider"/>.
/// <para>Column indexes in this API are ResultColumn indexes (position in <see cref="ResultColumns"/>), independent of
/// user column reordering. Row indexes are indexes into <see cref="Rows"/>, independent of client sorting.</para>
/// </summary>
public partial class ResultGrid : UserControl
{
    private INotifyCollectionChanged? _observedRows;
    private int _rowHeaderDigits;
    private bool _widthsPending;
    private int _clientSortColumn = -1;
    private ListSortDirection? _clientSortDirection;

    public ResultGrid()
    {
        GridResources.EnsureInitialized();
        InitializeComponent();
        InnerGrid.LoadingRow += OnLoadingRow;
        InnerGrid.Sorting += OnSorting;
        InnerGrid.BeginningEdit += OnBeginningEdit;
        InnerGrid.CellEditEnding += OnCellEditEnding;
        InnerGrid.SelectedCellsChanged += (_, _) => SelectedCellsChanged?.Invoke(this, EventArgs.Empty);
        InnerGrid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy,
            (_, e) => { CopyRows(); e.Handled = true; },
            (_, e) => { e.CanExecute = HasSelection; e.Handled = true; }));
        Loaded += (_, _) => GridResources.Changed += OnGridResourcesChanged;
        Unloaded += (_, _) => GridResources.Changed -= OnGridResourcesChanged;
    }

    // ---- dependency properties --------------------------------------------------------------------------------

    public static readonly DependencyProperty ResultColumnsProperty = DependencyProperty.Register(
        nameof(ResultColumns), typeof(IReadOnlyList<ResultColumn>), typeof(ResultGrid),
        new PropertyMetadata(null, (d, _) => ((ResultGrid)d).RebuildColumns()));

    /// <summary>The result-set columns. Setting it rebuilds the grid columns (and clears client sort).</summary>
    public IReadOnlyList<ResultColumn>? ResultColumns
    {
        get => (IReadOnlyList<ResultColumn>?)GetValue(ResultColumnsProperty);
        set => SetValue(ResultColumnsProperty, value);
    }

    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(
        nameof(Rows), typeof(IList), typeof(ResultGrid),
        new PropertyMetadata(null, (d, e) => ((ResultGrid)d).OnRowsChanged((IList?)e.OldValue, (IList?)e.NewValue)));

    /// <summary>The rows: an <c>IList</c> whose items are <c>object?[]</c> (ideally a <see cref="BulkObservableCollection{T}"/> of <c>object?[]</c>).</summary>
    public IList? Rows
    {
        get => (IList?)GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public static readonly DependencyProperty IsReadOnlyProperty = DependencyProperty.Register(
        nameof(IsReadOnly), typeof(bool), typeof(ResultGrid),
        new PropertyMetadata(true, (d, e) =>
        {
            var g = (ResultGrid)d;
            g.InnerGrid.IsReadOnly = (bool)e.NewValue;
            foreach (var c in g.InnerGrid.Columns.OfType<ResultGridColumn>())
                c.Binding = CellBinding(c.ColumnIndex, c.Source.DataTypeName, editable: !(bool)e.NewValue);
        }));

    /// <summary>True (default): no editing. False: F2 / double-click / typing edits a cell and raises <see cref="CellEditCommitted"/>.</summary>
    public bool IsReadOnly
    {
        get => (bool)GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    public static readonly DependencyProperty ServerSortProperty = DependencyProperty.Register(
        nameof(ServerSort), typeof(bool), typeof(ResultGrid), new PropertyMetadata(false));

    /// <summary>
    /// True: header clicks only raise <see cref="SortRequested"/> (no client sorting); show the server order with
    /// <see cref="SetSortIndicators"/>. False (default): header clicks sort loaded rows client-side (asc, desc, none)
    /// and still raise <see cref="SortRequested"/> for information.
    /// </summary>
    public bool ServerSort
    {
        get => (bool)GetValue(ServerSortProperty);
        set => SetValue(ServerSortProperty, value);
    }

    public static readonly DependencyProperty RowStateProviderProperty = DependencyProperty.Register(
        nameof(RowStateProvider), typeof(IRowStateProvider), typeof(ResultGrid),
        new PropertyMetadata(null, (d, _) => ((ResultGrid)d).OnRowStateProviderChanged()));

    /// <summary>Staged row/cell state for pending-change styling (c.grid.pendingNew/Modified/DeletedBackground). Null = none.</summary>
    public IRowStateProvider? RowStateProvider
    {
        get => (IRowStateProvider?)GetValue(RowStateProviderProperty);
        set => SetValue(RowStateProviderProperty, value);
    }

    public static readonly DependencyProperty CopyIncludesHeaderProperty = DependencyProperty.Register(
        nameof(CopyIncludesHeader), typeof(bool), typeof(ResultGrid), new PropertyMetadata(false));

    /// <summary>Default for Ctrl+C / Ctrl+Shift+C: prepend a header row of column names.</summary>
    public bool CopyIncludesHeader
    {
        get => (bool)GetValue(CopyIncludesHeaderProperty);
        set => SetValue(CopyIncludesHeaderProperty, value);
    }

    public static readonly DependencyProperty CopyNullTextProperty = DependencyProperty.Register(
        nameof(CopyNullText), typeof(string), typeof(ResultGrid), new PropertyMetadata(""));

    /// <summary>Text copied for NULL cells (default empty, which Excel treats as a blank cell).</summary>
    public string CopyNullText
    {
        get => (string)GetValue(CopyNullTextProperty);
        set => SetValue(CopyNullTextProperty, value);
    }

    public static readonly DependencyProperty ShowRowNumbersProperty = DependencyProperty.Register(
        nameof(ShowRowNumbers), typeof(bool), typeof(ResultGrid),
        new PropertyMetadata(true, (d, e) => ((ResultGrid)d).InnerGrid.HeadersVisibility =
            (bool)e.NewValue ? DataGridHeadersVisibility.All : DataGridHeadersVisibility.Column));

    public bool ShowRowNumbers
    {
        get => (bool)GetValue(ShowRowNumbersProperty);
        set => SetValue(ShowRowNumbersProperty, value);
    }

    public static readonly DependencyProperty StateVersionProperty = DependencyProperty.Register(
        nameof(StateVersion), typeof(int), typeof(ResultGrid), new PropertyMetadata(0));

    /// <summary>Bumped by <see cref="RefreshRowStates"/>; cell styles bind to it to re-query <see cref="RowStateProvider"/>.</summary>
    public int StateVersion
    {
        get => (int)GetValue(StateVersionProperty);
        private set => SetValue(StateVersionProperty, value);
    }

    // ---- events -----------------------------------------------------------------------------------------------

    /// <summary>A column header was clicked (Shift = add to multi-sort).</summary>
    public event EventHandler<SortRequestedEventArgs>? SortRequested;

    /// <summary>A cell is about to enter edit mode (editable mode only). Set Cancel to refuse.</summary>
    public event EventHandler<CellEditStartingEventArgs>? CellEditStarting;

    /// <summary>
    /// A cell edit was committed with changed text. Unless a handler sets Cancel, the grid then writes
    /// <see cref="CellEditCommittedEventArgs.NewValue"/> into the row array and re-reads the row's staged state.
    /// </summary>
    public event EventHandler<CellEditCommittedEventArgs>? CellEditCommitted;

    public event EventHandler? SelectedCellsChanged;

    // ---- public helpers ---------------------------------------------------------------------------------------

    /// <summary>The inner WPF DataGrid (escape hatch for context menus, extra input bindings, etc.).</summary>
    public DataGrid DataGrid => InnerGrid;

    public bool HasSelection => InnerGrid.SelectedCells.Count > 0;

    /// <summary>ResultColumn index of a grid column (-1 when it is not one of ours).</summary>
    public static int ColumnIndexOf(DataGridColumn? column) => column is ResultGridColumn c ? c.ColumnIndex : -1;

    /// <summary>The current (focused) cell as (row index in <see cref="Rows"/>, ResultColumn index), or null.</summary>
    public (int RowIndex, int ColumnIndex)? CurrentCellIndex
    {
        get
        {
            var cell = InnerGrid.CurrentCell;
            var col = ColumnIndexOf(cell.Column);
            if (cell.Item is not object?[] row || col < 0) return null;
            var index = SourceIndexOf(row, null);
            return index < 0 ? null : (index, col);
        }
    }

    public void FocusGrid()
    {
        if (InnerGrid.CurrentCell.Column is null && InnerGrid.Items.Count > 0 && InnerGrid.Columns.Count > 0)
            InnerGrid.CurrentCell = new DataGridCellInfo(InnerGrid.Items[0], InnerGrid.Columns[0]);
        InnerGrid.Focus();
    }

    /// <summary>Scrolls <paramref name="rowIndex"/> (index in <see cref="Rows"/>) into view and makes it current.</summary>
    public void ScrollToRow(int rowIndex, int columnIndex = 0, bool select = true)
    {
        if (Rows is not { } rows || rowIndex < 0 || rowIndex >= rows.Count) return;
        var item = rows[rowIndex];
        var column = GridColumn(columnIndex) ?? InnerGrid.Columns.FirstOrDefault();
        InnerGrid.ScrollIntoView(item, column);
        if (column is null) return;
        var info = new DataGridCellInfo(item, column);
        InnerGrid.CurrentCell = info;
        if (select)
        {
            InnerGrid.SelectedCells.Clear();
            InnerGrid.SelectedCells.Add(info);
        }
    }

    /// <summary>Starts editing the current cell (F2). False when read-only or nothing is current.</summary>
    public bool BeginEditCurrentCell() => !IsReadOnly && InnerGrid.BeginEdit();

    /// <summary>Selected rows (each once), in on-screen order.</summary>
    public IReadOnlyList<object?[]> GetSelectedRows() => CollectSelection().Rows;

    /// <summary>Indexes in <see cref="Rows"/> of the selected rows, in on-screen order.</summary>
    public IReadOnlyList<int> GetSelectedRowIndices()
    {
        var rows = CollectSelection().Rows;
        if (rows.Count == 0 || Rows is not { } list) return [];
        var map = SourceIndexMap(list);
        return rows.Select(r => map.TryGetValue(r, out var i) ? i : -1).Where(i => i >= 0).ToList();
    }

    /// <summary>Selected cells as (row index in <see cref="Rows"/>, ResultColumn index), in on-screen order.</summary>
    public IReadOnlyList<(int RowIndex, int ColumnIndex)> GetSelectedCells()
    {
        var sel = CollectSelection();
        if (sel.Rows.Count == 0 || Rows is not { } list) return [];
        var map = SourceIndexMap(list);
        var order = DisplayColumnOrder();
        var result = new List<(int, int)>();
        foreach (var row in sel.Rows)
        {
            if (!map.TryGetValue(row, out var ri)) continue;
            var cols = sel.Cells[row];
            foreach (var c in order) if (cols is null || cols.Contains(c)) result.Add((ri, c));
        }
        return result;
    }

    /// <summary>TSV of every selected row (all columns, display order). Null when nothing is selected.</summary>
    public string? GetRowsTsv(bool withHeader)
    {
        var sel = CollectSelection();
        if (sel.Rows.Count == 0) return null;
        return GridCopy.Build(ResultColumns ?? [], DisplayColumnOrder(), sel.Rows, null, withHeader, CellFormatter.Current, CopyNullText);
    }

    /// <summary>TSV of the selected cells only: the selected columns of the selected rows; unselected cells in between are empty.</summary>
    public string? GetSelectedCellsTsv(bool withHeader)
    {
        var sel = CollectSelection();
        if (sel.Rows.Count == 0) return null;
        var used = new HashSet<int>();
        foreach (var set in sel.Cells.Values)
        {
            if (set is null) { used = null; break; }
            used.UnionWith(set);
        }
        var order = DisplayColumnOrder().Where(c => used is null || used.Contains(c)).ToList();
        return GridCopy.Build(ResultColumns ?? [], order, sel.Rows, (row, col) => sel.Cells[row] is not { } s || s.Contains(col),
            withHeader, CellFormatter.Current, CopyNullText);
    }

    /// <summary>Ctrl+C: copies the selected rows as TSV. False when nothing is selected.</summary>
    public bool CopyRows(bool? withHeader = null) => SetClipboard(GetRowsTsv(withHeader ?? CopyIncludesHeader));

    /// <summary>Ctrl+Shift+C: copies only the selected cells as TSV. False when nothing is selected.</summary>
    public bool CopySelectedCells(bool? withHeader = null) => SetClipboard(GetSelectedCellsTsv(withHeader ?? CopyIncludesHeader));

    /// <summary>Writes <paramref name="value"/> into the row array and redraws the row (e.g. Set Value → NULL).</summary>
    public void SetCellValue(object?[] row, int columnIndex, object? value)
    {
        row[columnIndex] = value;
        RefreshRow(row);
    }

    /// <summary>Re-reads one row's values and staged state (row arrays do not raise change notifications).</summary>
    public void RefreshRow(object?[] row)
    {
        if (InnerGrid.ItemContainerGenerator.ContainerFromItem(row) is DataGridRow container)
        {
            foreach (var column in InnerGrid.Columns)
            {
                switch (column.GetCellContent(container))
                {
                    case CellText text:
                        text.Refresh();
                        break;
                    case TextBox box:
                        BindingOperations.GetBindingExpression(box, TextBox.TextProperty)?.UpdateTarget();
                        break;
                }
            }
            ApplyRowState(container);
        }
        StateVersion++;
    }

    /// <summary>Re-queries <see cref="RowStateProvider"/> for every visible row and cell.</summary>
    public void RefreshRowStates()
    {
        foreach (var row in RealizedRows()) ApplyRowState(row);
        StateVersion++;
    }

    /// <summary>Re-reads every visible row's values (after changing row arrays in place).</summary>
    public void RefreshVisibleRows()
    {
        foreach (var container in RealizedRows())
            if (container.Item is object?[] row) RefreshRow(row);
    }

    /// <summary>Shows sort glyphs for a server-side ORDER BY (ResultColumn index → direction). Others are cleared.</summary>
    public void SetSortIndicators(IEnumerable<(int ColumnIndex, ListSortDirection Direction)> sorts)
    {
        var map = sorts.ToDictionary(s => s.ColumnIndex, s => s.Direction);
        foreach (var column in InnerGrid.Columns)
            column.SortDirection = map.TryGetValue(ColumnIndexOf(column), out var d) ? d : null;
    }

    public void ClearSortIndicators() => SetSortIndicators([]);

    /// <summary>Sizes columns from the header and the first <paramref name="sampleRows"/> rows (cheap estimate, no measuring).</summary>
    public void AutoSizeColumns(int sampleRows = 200)
    {
        var cols = ResultColumns;
        if (cols is null) return;
        var rows = Rows;
        var fontSize = TryFindResource(GridResources.FontSizeKey) is double fs ? fs : 12;
        var charWidth = fontSize * 0.58;
        var fmt = CellFormatter.Current;
        foreach (var column in InnerGrid.Columns.OfType<ResultGridColumn>())
        {
            var i = column.ColumnIndex;
            var chars = cols[i].Name.Length + 2;
            if (rows is not null)
            {
                var n = Math.Min(rows.Count, sampleRows);
                for (var r = 0; r < n; r++)
                {
                    if (rows[r] is not object?[] row || i >= row.Length) continue;
                    var len = row[i] is null ? fmt.NullDisplay.Length : Math.Min(fmt.Display(row[i], cols[i].DataTypeName).Length, 60);
                    if (len > chars) chars = len;
                }
            }
            column.Width = new DataGridLength(Math.Clamp(chars * charWidth + 14, 44, 420));
        }
    }

    // ---- columns ------------------------------------------------------------------------------------------------

    private void RebuildColumns()
    {
        if (InnerGrid.IsEditing()) InnerGrid.CancelEdit();
        InnerGrid.Columns.Clear();
        ClearClientSort();
        var cols = ResultColumns;
        if (cols is null) return;
        for (var i = 0; i < cols.Count; i++)
            InnerGrid.Columns.Add(CreateColumn(cols[i], i));
        if (Rows is { Count: > 0 }) AutoSizeColumns();
        else _widthsPending = true;
    }

    private ResultGridColumn CreateColumn(ResultColumn rc, int index)
    {
        var column = new ResultGridColumn(index, rc)
        {
            Header = new TextBlock { Text = rc.Name, ToolTip = rc.Name + " : " + rc.DataTypeName },
            Binding = CellBinding(index, rc.DataTypeName, editable: !IsReadOnly),
            SortMemberPath = rc.Name,
            Width = new DataGridLength(Math.Clamp(rc.Name.Length * 7.5 + 24, 60, 300)),
        };
        if (RowStateProvider is not null) column.CellStyle = CreateTrackedCellStyle(index);
        return column;
    }

    /// <summary>
    /// The column binding. The display element (CellText) reads the row directly; this binding feeds the editing TextBox
    /// and, TwoWay when editable, keeps DataGridBoundColumn from coercing the column to read-only.
    /// </summary>
    private static Binding CellBinding(int index, string dataType, bool editable) => new()
    {
        Path = new PropertyPath("[" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]"),
        Mode = editable ? BindingMode.TwoWay : BindingMode.OneWay,
        Converter = CellValueConverter.Instance,
        ConverterParameter = dataType,
    };

    private Style CreateTrackedCellStyle(int columnIndex)
    {
        var style = new Style(typeof(DataGridCell), (Style)Resources["ResultCellStyle"]);
        var binding = new MultiBinding { Converter = new CellModifiedConverter(this, columnIndex), Mode = BindingMode.OneWay };
        binding.Bindings.Add(new Binding { Mode = BindingMode.OneWay });
        binding.Bindings.Add(new Binding(nameof(StateVersion)) { Source = this, Mode = BindingMode.OneWay });
        var trigger = new DataTrigger { Binding = binding, Value = true };
        trigger.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("c.grid.pendingModifiedBackground")));
        style.Triggers.Add(trigger);
        // Added after the modified trigger so the selection colour wins.
        var selected = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("c.grid.cellSelectionBackground")));
        style.Triggers.Add(selected);
        style.Seal();
        return style;
    }

    private static bool IsNumeric(string type) => type is "smallint" or "integer" or "bigint" or "real"
        or "double precision" or "money" or "oid" or "int2" or "int4" or "int8" or "float4" or "float8"
        || type.StartsWith("numeric", StringComparison.Ordinal) || type.StartsWith("decimal", StringComparison.Ordinal);

    private DataGridColumn? GridColumn(int columnIndex) =>
        InnerGrid.Columns.FirstOrDefault(c => ColumnIndexOf(c) == columnIndex);

    private List<int> DisplayColumnOrder() =>
        InnerGrid.Columns.Where(c => c.Visibility == Visibility.Visible).OrderBy(c => c.DisplayIndex)
            .Select(ColumnIndexOf).Where(i => i >= 0).ToList();

    private void OnRowStateProviderChanged()
    {
        foreach (var column in InnerGrid.Columns.OfType<ResultGridColumn>())
            column.CellStyle = RowStateProvider is null ? null : CreateTrackedCellStyle(column.ColumnIndex);
        RefreshRowStates();
    }

    private void OnGridResourcesChanged(object? sender, EventArgs e)
    {
        // Formatting settings changed (null text, date format): re-format visible cells; recycled ones refresh on reuse.
        RefreshVisibleRows();
        _rowHeaderDigits = 0;
        UpdateRowHeaderWidth();
    }

    // ---- rows ---------------------------------------------------------------------------------------------------

    private void OnRowsChanged(IList? oldRows, IList? newRows)
    {
        if (_observedRows is not null) _observedRows.CollectionChanged -= OnRowsCollectionChanged;
        _observedRows = newRows as INotifyCollectionChanged;
        if (_observedRows is not null) _observedRows.CollectionChanged += OnRowsCollectionChanged;
        ClearClientSort();
        InnerGrid.ItemsSource = newRows;
        UpdateRowHeaderWidth();
        if (newRows is { Count: > 0 } && InnerGrid.Columns.Count > 0) AutoSizeColumns();
        else _widthsPending = true;
    }

    private void OnRowsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateRowHeaderWidth();
        if (_widthsPending && Rows is { Count: > 0 } && InnerGrid.Columns.Count > 0)
        {
            _widthsPending = false;
            AutoSizeColumns();
        }
    }

    private void UpdateRowHeaderWidth()
    {
        var count = Rows?.Count ?? 0;
        var digits = Math.Max(2, (int)Math.Floor(Math.Log10(Math.Max(1, count))) + 1);
        if (digits == _rowHeaderDigits) return;
        _rowHeaderDigits = digits;
        var fontSize = TryFindResource(GridResources.FontSizeKey) is double fs ? fs : 12;
        InnerGrid.RowHeaderWidth = Math.Ceiling(digits * fontSize * 0.62 + 14);
    }

    private void OnLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        e.Row.Header = BoxedRowNumber(e.Row.GetIndex() + 1);
        ApplyRowState(e.Row);
    }

    private static readonly object[] SmallNumbers = Enumerable.Range(0, 4096).Select(i => (object)i).ToArray();

    private static object BoxedRowNumber(int n) => n >= 0 && n < SmallNumbers.Length ? SmallNumbers[n] : n;

    private void ApplyRowState(DataGridRow row)
    {
        var state = RowStateProvider is { } p && row.Item is object?[] item ? p.GetRowState(item) : RowState.Unchanged;
        var key = state switch
        {
            RowState.New => "c.grid.pendingNewBackground",
            RowState.Modified => "c.grid.pendingModifiedBackground",
            RowState.Deleted => "c.grid.pendingDeletedBackground",
            _ => null,
        };
        if (key is null)
        {
            if (row.ReadLocalValue(BackgroundProperty) != DependencyProperty.UnsetValue) row.ClearValue(BackgroundProperty);
        }
        else
        {
            row.SetResourceReference(BackgroundProperty, key);
        }
    }

    private IEnumerable<DataGridRow> RealizedRows()
    {
        var presenter = FindChild<System.Windows.Controls.Primitives.DataGridRowsPresenter>(InnerGrid);
        if (presenter is null) yield break;
        foreach (var child in presenter.Children)
            if (child is DataGridRow { Item: object?[] } row) yield return row;
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        var n = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            if (FindChild<T>(child) is { } found) return found;
        }
        return null;
    }

    /// <summary>Index of <paramref name="row"/> in <see cref="Rows"/>. Uses the container index when the view is unsorted.</summary>
    private int SourceIndexOf(object?[] row, DataGridRow? container)
    {
        if (Rows is not { } list) return -1;
        if (container is not null && _clientSortDirection is null)
        {
            var i = container.GetIndex();
            if (i >= 0 && i < list.Count && ReferenceEquals(list[i], row)) return i;
        }
        for (var i = 0; i < list.Count; i++)
            if (ReferenceEquals(list[i], row)) return i;
        return -1;
    }

    private static Dictionary<object, int> SourceIndexMap(IList list)
    {
        var map = new Dictionary<object, int>(list.Count, ReferenceEqualityComparer.Instance);
        for (var i = 0; i < list.Count; i++)
            if (list[i] is { } item) map[item] = i;
        return map;
    }

    // ---- selection ----------------------------------------------------------------------------------------------

    private sealed record Selection(List<object?[]> Rows, Dictionary<object?[], HashSet<int>?> Cells);

    /// <summary>Selected rows in on-screen order with their selected ResultColumn indexes (null set = every column).</summary>
    private Selection CollectSelection()
    {
        var selected = InnerGrid.SelectedCells;
        var empty = new Selection([], new Dictionary<object?[], HashSet<int>?>(ReferenceEqualityComparer.Instance));
        if (selected.Count == 0) return empty;

        var items = InnerGrid.Items;
        var columnCount = InnerGrid.Columns.Count;
        if (columnCount > 0 && selected.Count >= (long)items.Count * columnCount)
        {
            // Select-all: avoid enumerating millions of cell infos.
            var all = new Selection(new List<object?[]>(items.Count), new Dictionary<object?[], HashSet<int>?>(items.Count, ReferenceEqualityComparer.Instance));
            foreach (var item in items)
            {
                if (item is not object?[] row) continue;
                all.Rows.Add(row);
                all.Cells[row] = null;
            }
            return all;
        }

        var cells = new Dictionary<object?[], HashSet<int>?>(ReferenceEqualityComparer.Instance);
        foreach (var info in selected)
        {
            if (info.Item is not object?[] row) continue;
            var col = ColumnIndexOf(info.Column);
            if (col < 0) continue;
            ref var set = ref CollectionsMarshal.GetValueRefOrAddDefault(cells, row, out _);
            (set ??= []).Add(col);
        }
        if (cells.Count == 0) return empty;

        List<object?[]> ordered;
        if (cells.Count == 1) ordered = [.. cells.Keys];
        else
        {
            ordered = new List<object?[]>(cells.Count);
            foreach (var item in items)
                if (item is object?[] row && cells.ContainsKey(row)) ordered.Add(row);
        }
        return new Selection(ordered, cells);
    }

    private static bool SetClipboard(string? text)
    {
        if (text is null) return false;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, true);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(20);
            }
        }
        return false;
    }

    // ---- sorting ------------------------------------------------------------------------------------------------

    private void OnSorting(object? sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var index = ColumnIndexOf(e.Column);
        if (index < 0) return;
        var add = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        SortRequested?.Invoke(this, new SortRequestedEventArgs(index, ResultColumns![index].Name, add));
        if (ServerSort) return;

        ListSortDirection? next = _clientSortColumn != index ? ListSortDirection.Ascending
            : _clientSortDirection switch
            {
                ListSortDirection.Ascending => ListSortDirection.Descending,
                ListSortDirection.Descending => null,
                _ => ListSortDirection.Ascending,
            };
        ApplyClientSort(index, next);
    }

    private void ApplyClientSort(int columnIndex, ListSortDirection? direction)
    {
        if (CollectionViewSource.GetDefaultView(InnerGrid.ItemsSource) is not ListCollectionView view) return;
        _clientSortColumn = direction is null ? -1 : columnIndex;
        _clientSortDirection = direction;
        view.CustomSort = direction is null ? null : new RowComparer(columnIndex, direction.Value);
        SetSortIndicators(direction is null ? [] : [(columnIndex, direction.Value)]);
    }

    private void ClearClientSort()
    {
        if (_clientSortDirection is null) return;
        _clientSortColumn = -1;
        _clientSortDirection = null;
        if (InnerGrid.ItemsSource is not null && CollectionViewSource.GetDefaultView(InnerGrid.ItemsSource) is ListCollectionView view)
            view.CustomSort = null;
        foreach (var column in InnerGrid.Columns) column.SortDirection = null;
    }

    // ---- editing ------------------------------------------------------------------------------------------------

    private void OnBeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        var col = ColumnIndexOf(e.Column);
        if (IsReadOnly || e.Row.Item is not object?[] row || col < 0)
        {
            e.Cancel = true;
            return;
        }
        if (CellEditStarting is null) return;
        var args = new CellEditStartingEventArgs(SourceIndexOf(row, e.Row), col, row);
        CellEditStarting(this, args);
        e.Cancel = args.Cancel;
    }

    private void OnCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.EditingElement is not TextBox box || e.Row.Item is not object?[] row) return;
        var col = ColumnIndexOf(e.Column);
        if (col < 0 || col >= row.Length) return;
        var old = row[col];
        var type = ResultColumns is { } rc && col < rc.Count ? rc[col].DataTypeName : null;
        var newText = box.Text;
        if (newText == CellFormatter.Current.Full(old, "", type)) return; // unchanged (NULL + empty text counts as unchanged)

        var args = new CellEditCommittedEventArgs(SourceIndexOf(row, e.Row), col, row, old, newText);
        CellEditCommitted?.Invoke(this, args);
        if (args.Cancel) return;
        row[col] = args.NewValue;
        ApplyRowState(e.Row);
        StateVersion++;
    }

    // ---- nested types -------------------------------------------------------------------------------------------

    /// <summary>A text column bound to one ResultColumn. Editing shows the full value (empty for NULL).</summary>
    private sealed class ResultGridColumn(int columnIndex, ResultColumn source) : DataGridTextColumn
    {
        public int ColumnIndex { get; } = columnIndex;
        public ResultColumn Source { get; } = source;

        /// <summary>
        /// Always null: we copy through <see cref="GridCopy"/>, and DataGrid's UI Automation value holders (created
        /// only while a UIA client listens) re-bind this on the cell and crash with a NullReferenceException on commit.
        /// </summary>
        public override BindingBase? ClipboardContentBinding
        {
            get => null;
            set { }
        }

        protected override FrameworkElement GenerateElement(DataGridCell cell, object dataItem) =>
            new CellText(ColumnIndex, Source.DataTypeName, IsNumeric(Source.DataTypeName));

        protected override FrameworkElement GenerateEditingElement(DataGridCell cell, object dataItem)
        {
            var element = base.GenerateEditingElement(cell, dataItem);
            if (element is TextBox box)
            {
                box.SetBinding(TextBox.TextProperty, new Binding("[" + ColumnIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]")
                {
                    Mode = BindingMode.OneWay,
                    Converter = CellEditConverter.Instance,
                    ConverterParameter = Source.DataTypeName,
                });
                box.Padding = new Thickness(2, 0, 2, 0);
            }
            return element;
        }
    }

    private sealed class CellModifiedConverter(ResultGrid owner, int columnIndex) : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            values.Length > 0 && values[0] is object?[] row && owner.RowStateProvider is { } p && p.IsCellModified(row, columnIndex);

        public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>Client sort on one column: nulls last, same-type values via IComparable, mixed types by text.</summary>
    internal sealed class RowComparer(int column, ListSortDirection direction) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            var a = x is object?[] ra && column < ra.Length ? ra[column] : null;
            var b = y is object?[] rb && column < rb.Length ? rb[column] : null;
            if (a is null && b is null) return 0;
            if (a is null) return 1;  // nulls last in both directions
            if (b is null) return -1;
            int c;
            if (a.GetType() == b.GetType() && a is IComparable ca) c = ca.CompareTo(b);
            else c = string.CompareOrdinal(a.ToString(), b.ToString());
            return direction == ListSortDirection.Ascending ? c : -c;
        }
    }
}

internal static class DataGridExtensions
{
    public static bool IsEditing(this DataGrid grid) =>
        grid.CurrentCell.Column is not null && grid.CurrentCell.Item is not null &&
        grid.ItemContainerGenerator.ContainerFromItem(grid.CurrentCell.Item) is DataGridRow { IsEditing: true };
}
