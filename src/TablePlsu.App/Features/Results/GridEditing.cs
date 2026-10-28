using System.Windows;
using TablePlsu.Core.Data;
using TablePlsu.Core.Db;
using TablePlsu.Core.Metadata;

namespace TablePlsu.App.Features.Results;

/// <summary>
/// The owner of an editable <see cref="ResultGrid"/>: a table tab (spec §6) or an editor result tab (spec §10 "Edit
/// results inline"). <see cref="GridEditing"/> implements the staging operations once on top of this.
/// </summary>
public interface IGridEditHost
{
    /// <summary>The staged changes (null while the grid is read-only).</summary>
    PendingChanges? Pending { get; }

    IReadOnlyList<ResultColumn>? Columns { get; }

    /// <summary>The grid's rows (loaded rows followed by staged inserted rows).</summary>
    BulkObservableCollection<object?[]> Rows { get; }

    /// <summary>Editable and not committing: staging is allowed.</summary>
    bool CanStage { get; }

    /// <summary>The grid while a view exists (null in unit tests).</summary>
    ResultGrid? EditGrid { get; }

    /// <summary>The source table column behind grid column <paramref name="columnIndex"/> (null = unknown).</summary>
    ColumnInfo? ColumnInfoAt(int columnIndex);

    /// <summary>The source table's primary key column names.</summary>
    IReadOnlyList<string> PrimaryKeyColumns { get; }

    /// <summary>Scrolls to (and focuses) a row (no-op without a view).</summary>
    void ScrollToRow(object?[] row);
}

/// <summary>
/// Staging operations shared by every editable grid (spec §6 Inline edit, Set Value, Row operations, Copy / paste):
/// typed cell edits, Set Value on the selection, insert / duplicate / delete rows, paste as new rows or into cells.
/// Everything goes through the host's <see cref="PendingChanges"/> (undo/redo, row states) and respects
/// <see cref="IGridEditHost.CanStage"/> (the commit lock).
/// </summary>
public static class GridEditing
{
    public static RowState RowStateOf(PendingChanges? pending, object?[] row) => pending?.GetRowState(row) switch
    {
        PendingRowState.New => RowState.New,
        PendingRowState.Modified => RowState.Modified,
        PendingRowState.Deleted => RowState.Deleted,
        _ => RowState.Unchanged,
    };

    /// <summary>A column that can't be written (generated, or unknown to the metadata).</summary>
    public static bool IsColumnReadOnly(IGridEditHost host, int columnIndex)
    {
        if (host.Columns is not { } columns || columnIndex < 0 || columnIndex >= columns.Count) return true;
        var col = host.ColumnInfoAt(columnIndex);
        return col is null || col.IsGenerated;
    }

    public static bool CanEditCell(IGridEditHost host, object?[] row, int columnIndex) =>
        host.CanStage && host.Pending is { } p && !p.IsDeleted(row) && !IsColumnReadOnly(host, columnIndex);

    /// <summary>Grid edit commit: stage the typed text. Returns the value the grid should show.</summary>
    public static object? CommitCellEdit(IGridEditHost host, object?[] row, int columnIndex, string text)
    {
        if (!CanEditCell(host, row, columnIndex)) return row[columnIndex];
        host.Pending!.SetCell(row, columnIndex, InputFor(host, columnIndex, text));
        return row[columnIndex];
    }

    /// <summary>Typed text for a column (a zone-less timestamptz is UTC, as displayed; see <see cref="PgInput"/>).</summary>
    private static string InputFor(IGridEditHost host, int columnIndex, string text) =>
        host.Columns is { } columns && columnIndex < columns.Count ? PgInput.ForColumn(columns[columnIndex].DataTypeName, text) : text;

    /// <summary>A pasted field: the text NULL is SQL NULL (what the grid shows and copies for NULL), anything else is typed text.</summary>
    private static object? PastedValue(IGridEditHost host, int columnIndex, string field) => field == "NULL" ? null : InputFor(host, columnIndex, field);

    /// <summary>Set Value menu: applies <paramref name="value"/> to every selected cell (one undo step).</summary>
    public static void SetSelectedCells(IGridEditHost host, object? value)
    {
        if (!host.CanStage || host.EditGrid is not { } grid) return;
        var cells = grid.GetSelectedCells();
        if (cells.Count == 0 && grid.CurrentCellIndex is { } cur) cells = [cur];
        using (host.Pending!.BeginBatch())
        {
            foreach (var (r, c) in cells)
            {
                if (r < 0 || r >= host.Rows.Count) continue;
                var row = host.Rows[r];
                if (CanEditCell(host, row, c)) host.Pending.SetCell(row, c, value);
            }
        }
    }

    /// <summary>Whether the selection's current column is a date/time column (offers NOW()).</summary>
    public static bool CurrentColumnIsTemporal(IGridEditHost host)
    {
        if (host.EditGrid?.CurrentCellIndex is not { } cur || host.Columns is not { } columns || cur.ColumnIndex >= columns.Count) return false;
        var t = columns[cur.ColumnIndex].DataTypeName;
        return t.StartsWith("timestamp", StringComparison.Ordinal) || t.StartsWith("date", StringComparison.Ordinal) || t.StartsWith("time", StringComparison.Ordinal);
    }

    /// <summary>Ctrl+I: a new row with every column DEFAULT.</summary>
    public static object?[]? InsertRow(IGridEditHost host)
    {
        if (!host.CanStage) return null;
        var row = host.Pending!.InsertRow();
        host.ScrollToRow(row);
        return row;
    }

    /// <summary>
    /// Ctrl+D: stages copies of <paramref name="rows"/> (PK / identity / generated columns left to DEFAULT when they have
    /// one). Values the grid only holds as a lossy preview (a truncated bytea, an unreadable value) are not copied: those
    /// cells stay DEFAULT instead of inserting the preview text.
    /// </summary>
    public static void DuplicateRows(IGridEditHost host, IReadOnlyList<object?[]> rows)
    {
        if (!host.CanStage || host.Columns is null || rows.Count == 0) return;
        var setColumns = DuplicateColumns(host);
        object?[]? last = null;
        using (host.Pending!.BeginBatch())
        {
            foreach (var row in rows)
            {
                var cols = setColumns.Where(c => row[c] is not SqlDefault && !DbValueConverter.IsLossyPreview(row[c])).ToList();
                last = host.Pending.InsertRow(row, cols);
            }
        }
        if (last is not null) host.ScrollToRow(last);
    }

    private static List<int> DuplicateColumns(IGridEditHost host)
    {
        var result = new List<int>();
        var pk = host.PrimaryKeyColumns;
        for (var i = 0; i < host.Columns!.Count; i++)
        {
            var col = host.ColumnInfoAt(i);
            if (col is null || col.IsGenerated || col.Identity is not null) continue;
            if (pk.Contains(col.Name) && col.Default is not null) continue;
            result.Add(i);
        }
        return result;
    }

    /// <summary>Delete: stage deletes of the selected rows (restores them when all are already deleted).</summary>
    public static void DeleteSelectedRows(IGridEditHost host)
    {
        if (!host.CanStage || host.EditGrid is not { } grid) return;
        var rows = grid.GetSelectedRows();
        if (rows.Count == 0) return;
        if (rows.All(host.Pending!.IsDeleted)) host.Pending.RestoreRows(rows);
        else host.Pending.DeleteRows(rows);
    }

    /// <summary>
    /// Ctrl+V: clipboard TSV as new rows. Fields map to columns from <paramref name="startColumn"/> (default: the current
    /// cell's column), except whole rows (at least one field per column), which map from the first column. Empty fields
    /// stay DEFAULT; the text NULL is SQL NULL.
    /// </summary>
    public static int PasteAsNewRows(IGridEditHost host, string? text, int? startColumn = null)
    {
        if (!host.CanStage || host.Columns is not { } columns) return 0;
        var lines = TsvParser.Parse(text);
        if (lines.Count == 0) return 0;
        var start = startColumn ?? host.EditGrid?.CurrentCellIndex?.ColumnIndex ?? 0;
        if (start < 0 || start >= columns.Count || lines.Max(l => l.Length) >= columns.Count) start = 0;
        object?[]? last = null;
        using (host.Pending!.BeginBatch())
        {
            foreach (var fields in lines)
            {
                var values = new object?[columns.Count];
                var set = new List<int>();
                for (var j = 0; j < fields.Length && start + j < columns.Count; j++)
                {
                    var c = start + j;
                    if (fields[j].Length == 0 || IsColumnReadOnly(host, c)) continue;
                    values[c] = PastedValue(host, c, fields[j]);
                    set.Add(c);
                }
                last = host.Pending.InsertRow(values, set);
            }
        }
        if (last is not null) host.ScrollToRow(last);
        return lines.Count;
    }

    /// <summary>Ctrl+Shift+V: clipboard TSV into the cells from the current cell (one value fills every selected cell).</summary>
    public static int PasteIntoCells(IGridEditHost host, string? text)
    {
        if (!host.CanStage || host.EditGrid is not { } grid || host.Columns is null) return 0;
        var selected = grid.GetSelectedCells();
        var start = grid.CurrentCellIndex ?? (selected.Count > 0 ? selected.MinBy(s => (s.RowIndex, s.ColumnIndex)) : (0, 0));
        return PasteIntoCells(host, text, start, selected);
    }

    /// <summary>Pastes TSV into cells from <paramref name="start"/>; a single value fills every cell of <paramref name="selected"/>.</summary>
    public static int PasteIntoCells(IGridEditHost host, string? text, (int RowIndex, int ColumnIndex) start, IReadOnlyList<(int RowIndex, int ColumnIndex)> selected)
    {
        if (!host.CanStage || host.Columns is not { } columns) return 0;
        var lines = TsvParser.Parse(text);
        if (lines.Count == 0) return 0;
        var rows = host.Rows;
        var pending = host.Pending!;
        var changed = 0;
        using (pending.BeginBatch())
        {
            if (lines.Count == 1 && lines[0].Length == 1 && selected.Count > 1)
            {
                foreach (var (r, c) in selected)
                    if (r < rows.Count && CanEditCell(host, rows[r], c) && pending.SetCell(rows[r], c, PastedValue(host, c, lines[0][0]))) changed++;
                return changed;
            }
            for (var i = 0; i < lines.Count; i++)
            {
                var r = start.RowIndex + i;
                if (r < 0 || r >= rows.Count) break;
                for (var j = 0; j < lines[i].Length; j++)
                {
                    var c = start.ColumnIndex + j;
                    if (c < 0 || c >= columns.Count) break;
                    if (CanEditCell(host, rows[r], c) && pending.SetCell(rows[r], c, PastedValue(host, c, lines[i][j]))) changed++;
                }
            }
        }
        return changed;
    }

    /// <summary>
    /// Keeps <see cref="IGridEditHost.Rows"/> = <paramref name="loadedRows"/> + staged inserted rows (incrementally, keeping
    /// scroll position and selection).
    /// </summary>
    public static void SyncInsertedRows(IGridEditHost host, IReadOnlyCollection<object?[]> loadedRows)
    {
        var rows = host.Rows;
        var inserted = host.Pending?.InsertedRows ?? [];
        var desired = new HashSet<object?[]>(loadedRows.Concat(inserted), ReferenceEqualityComparer.Instance);
        for (var i = rows.Count - 1; i >= 0; i--)
            if (!desired.Contains(rows[i])) rows.RemoveAt(i);
        var present = new HashSet<object?[]>(rows, ReferenceEqualityComparer.Instance);
        foreach (var row in inserted)
            if (!present.Contains(row)) rows.Add(row);
    }

    public static string? ClipboardText()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
