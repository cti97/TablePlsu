using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TablePlsu.App.Features.TableData;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Commands;
using TablePlsu.Core.Data;

namespace TablePlsu.App.Features.Results;

/// <summary>What <see cref="EditableGridController"/> needs from a grid's view-model, beyond the staging host.</summary>
public interface IEditableGridModel : IGridEditHost
{
    /// <summary>The grid accepts edits (it may still be locked by a running commit).</summary>
    bool IsEditable { get; }

    /// <summary>Why the grid is read-only (shown in the info bar), or null.</summary>
    string? ReadOnlyReason { get; }

    /// <summary>The user tried to edit a read-only grid (double-click, F2, typing…): the view flashes the reason.</summary>
    void NotifyReadOnlyAttempt();
}

/// <summary>
/// The view half of an editable <see cref="ResultGrid"/>, shared by the table tab and the editor results (spec §6, §10):
/// cell edits staged through <see cref="GridEditing"/>, Enter to edit, staged-delete strike-through, the cell context menu
/// and the Set Value menu (Alt+Enter). On a read-only grid, double-click / typing into a cell reports a read-only attempt
/// (the view flashes the reason instead of silently doing nothing).
/// </summary>
public sealed class EditableGridController
{
    private static readonly ConditionalWeakTable<DataGridRow, StrikeThroughAdorner> Strikes = new();
    private readonly ResultGrid _grid;
    private readonly Func<IEditableGridModel?> _model;

    public EditableGridController(ResultGrid grid, Func<IEditableGridModel?> model)
    {
        _grid = grid;
        _model = model;
        grid.CellEditStarting += (_, e) => e.Cancel = _model() is not { } m || !GridEditing.CanEditCell(m, e.Row, e.ColumnIndex);
        grid.CellEditCommitted += (_, e) =>
        {
            if (_model() is not { } m) { e.Cancel = true; return; }
            e.NewValue = GridEditing.CommitCellEdit(m, e.Row, e.ColumnIndex, e.NewText);
        };
        var dg = grid.DataGrid;
        dg.PreviewKeyDown += OnPreviewKeyDown;
        dg.PreviewTextInput += OnPreviewTextInput;
        dg.MouseDoubleClick += OnMouseDoubleClick;
        dg.LoadingRow += (_, e) => grid.Dispatcher.BeginInvoke(() => UpdateStrike(e.Row), DispatcherPriority.Loaded);
    }

    private bool IsReadOnlyWithReason => _model() is { IsEditable: false, ReadOnlyReason: not null };

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Enter edits the current cell (spec §6 Inline edit); while editing, the grid commits as usual.
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && _model() is { IsEditable: true } && !_grid.DataGrid.IsEditing())
        {
            if (_grid.BeginEditCurrentCell()) e.Handled = true;
        }
    }

    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0]) || !IsReadOnlyWithReason) return;
        if (_grid.DataGrid.CurrentCell.Column is null) return;
        _model()!.NotifyReadOnlyAttempt();
    }

    private void OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsReadOnlyWithReason || FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject) is null) return;
        _model()!.NotifyReadOnlyAttempt();
    }

    /// <summary>Re-applies row states and strike-through to the realized rows.</summary>
    public void RefreshRowVisuals()
    {
        _grid.RefreshVisibleRows();
        foreach (var row in FindChildren<DataGridRow>(_grid.DataGrid)) UpdateStrike(row);
    }

    private void UpdateStrike(DataGridRow row)
    {
        var deleted = _model()?.Pending is { } p && row.Item is object?[] item && p.IsDeleted(item);
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

    /// <summary>Alt+Enter: the Set Value menu at the current cell.</summary>
    public void ShowSetValueMenu()
    {
        if (_model() is not { } m) return;
        var menu = new ContextMenu();
        foreach (var item in SetValueItems(m)) menu.Items.Add(item);
        menu.PlacementTarget = CurrentCellElement() ?? (UIElement)_grid.DataGrid;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>Right-click on a cell: selects it (unless already selected) and opens the cell menu. False when not on a cell.</summary>
    public bool ShowCellMenu(MouseButtonEventArgs e)
    {
        if (_model() is not { } m) return false;
        var cell = FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
        if (cell is null) return false;
        if (!cell.IsSelected)
        {
            _grid.DataGrid.SelectedCells.Clear();
            var info = new DataGridCellInfo(cell);
            _grid.DataGrid.CurrentCell = info;
            _grid.DataGrid.SelectedCells.Add(info);
        }
        var editable = m.IsEditable;
        var menu = new ContextMenu();
        var setValue = new MenuItem { Header = "Set Value", InputGestureText = ShortcutText.Gesture(CommandIds.GridSetValueMenu), IsEnabled = editable };
        foreach (var item in SetValueItems(m)) setValue.Items.Add(item);
        menu.Items.Add(setValue);
        menu.Items.Add(new Separator());
        menu.Items.Add(Cmd("Copy", CommandIds.GridCopy, () => _grid.CopyRows()));
        menu.Items.Add(Cmd("Copy Selected Cells", CommandIds.GridCopyCells, () => _grid.CopySelectedCells()));
        menu.Items.Add(Cmd("Paste as New Rows", CommandIds.GridPaste, () => GridEditing.PasteAsNewRows(m, GridEditing.ClipboardText()), editable));
        menu.Items.Add(Cmd("Paste into Cells", CommandIds.GridPasteCells, () => GridEditing.PasteIntoCells(m, GridEditing.ClipboardText()), editable));
        menu.Items.Add(new Separator());
        menu.Items.Add(Cmd("Insert Row", CommandIds.GridInsertRow, () => GridEditing.InsertRow(m), editable));
        menu.Items.Add(Cmd("Duplicate Rows", CommandIds.GridDuplicateRow, () => GridEditing.DuplicateRows(m, _grid.GetSelectedRows()), editable));
        menu.Items.Add(Cmd("Delete Rows", CommandIds.GridDeleteRow, () => GridEditing.DeleteSelectedRows(m), editable));
        menu.PlacementTarget = cell;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
        return true;
    }

    private static IEnumerable<MenuItem> SetValueItems(IEditableGridModel m)
    {
        yield return Item("NULL", () => GridEditing.SetSelectedCells(m, null));
        yield return Item("DEFAULT", () => GridEditing.SetSelectedCells(m, SqlDefault.Instance));
        yield return Item("EMPTY", () => GridEditing.SetSelectedCells(m, ""));
        if (GridEditing.CurrentColumnIsTemporal(m)) yield return Item("NOW()", () => GridEditing.SetSelectedCells(m, SqlExpression.Now));
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
        item.InputGestureText = ShortcutText.Gesture(commandId);
        return item;
    }

    private FrameworkElement? CurrentCellElement()
    {
        var info = _grid.DataGrid.CurrentCell;
        if (info.Column is null || info.Item is null) return null;
        return info.Column.GetCellContent(info.Item)?.Parent as FrameworkElement ?? info.Column.GetCellContent(info.Item);
    }

    /// <summary>Briefly pulses <paramref name="bar"/> (the read-only info bar) to draw the eye to the reason.</summary>
    public static void Flash(FrameworkElement bar)
    {
        var pulse = new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(180)) { AutoReverse = false, RepeatBehavior = new RepeatBehavior(3) };
        bar.BeginAnimation(UIElement.OpacityProperty, pulse);
    }

    internal static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null and not T)
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    internal static IEnumerable<T> FindChildren<T>(DependencyObject parent) where T : DependencyObject
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
