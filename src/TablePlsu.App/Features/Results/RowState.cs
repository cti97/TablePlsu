namespace TablePlsu.App.Features.Results;

/// <summary>Staged-change state of a grid row (styled with c.grid.pendingNew/Modified/DeletedBackground).</summary>
public enum RowState { Unchanged, New, Modified, Deleted }

/// <summary>
/// Supplies per-row and per-cell staged state to an editable <see cref="ResultGrid"/>. Rows are identified by their
/// <c>object?[]</c> instance (reference identity, stable across client sorting). Column indexes are ResultColumn
/// indexes (0-based position in <see cref="ResultGrid.ResultColumns"/>). Called on the UI thread for visible rows only;
/// after changing state call <see cref="ResultGrid.RefreshRow"/> or <see cref="ResultGrid.RefreshRowStates"/>.
/// </summary>
public interface IRowStateProvider
{
    RowState GetRowState(object?[] row);

    bool IsCellModified(object?[] row, int columnIndex);
}

/// <summary>An <see cref="IRowStateProvider"/> from two delegates.</summary>
public sealed class DelegateRowStateProvider(Func<object?[], RowState> rowState, Func<object?[], int, bool>? cellModified = null) : IRowStateProvider
{
    public RowState GetRowState(object?[] row) => rowState(row);

    public bool IsCellModified(object?[] row, int columnIndex) => cellModified?.Invoke(row, columnIndex) ?? false;
}

public sealed class SortRequestedEventArgs(int columnIndex, string columnName, bool addToMultiSort) : EventArgs
{
    /// <summary>ResultColumn index (0-based).</summary>
    public int ColumnIndex { get; } = columnIndex;
    public string ColumnName { get; } = columnName;
    /// <summary>Shift was held: add to a multi-column sort instead of replacing it.</summary>
    public bool AddToMultiSort { get; } = addToMultiSort;
}

public sealed class CellEditStartingEventArgs(int rowIndex, int columnIndex, object?[] row) : EventArgs
{
    /// <summary>Index in the Rows list (source index, not the sorted view position).</summary>
    public int RowIndex { get; } = rowIndex;
    public int ColumnIndex { get; } = columnIndex;
    public object?[] Row { get; } = row;
    /// <summary>Set true to keep the cell read-only.</summary>
    public bool Cancel { get; set; }
}

public sealed class CellEditCommittedEventArgs(int rowIndex, int columnIndex, object?[] row, object? oldValue, string newText) : EventArgs
{
    /// <summary>Index in the Rows list (source index, not the sorted view position).</summary>
    public int RowIndex { get; } = rowIndex;
    public int ColumnIndex { get; } = columnIndex;
    public object?[] Row { get; } = row;
    public object? OldValue { get; } = oldValue;
    /// <summary>The text the user typed.</summary>
    public string NewText { get; } = newText;
    /// <summary>What gets written into <c>Row[ColumnIndex]</c>; defaults to <see cref="NewText"/>. Handlers may convert it.</summary>
    public object? NewValue { get; set; } = newText;
    /// <summary>Set true to reject the edit (the row keeps <see cref="OldValue"/>).</summary>
    public bool Cancel { get; set; }
}
