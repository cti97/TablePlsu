namespace TablePlsu.Core.Data;

/// <summary>Staged state of a row.</summary>
public enum PendingRowState { Unchanged, New, Modified, Deleted }

/// <summary>A row identity: the original primary-key values (structural equality).</summary>
public sealed class RowKey : IEquatable<RowKey>
{
    private readonly object?[] _values;

    public RowKey(IEnumerable<object?> values) => _values = values.ToArray();

    public IReadOnlyList<object?> Values => _values;

    public bool Equals(RowKey? other)
    {
        if (other is null || other._values.Length != _values.Length) return false;
        for (var i = 0; i < _values.Length; i++)
            if (!Equals(_values[i], other._values[i])) return false;
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as RowKey);

    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (var v in _values) h.Add(v);
        return h.ToHashCode();
    }

    public override string ToString() => string.Join(", ", _values.Select(v => v?.ToString() ?? "NULL"));
}

/// <summary>A staged UPDATE: the original row values and the changed cells (column index → new value).</summary>
public sealed record StagedUpdate(RowKey Key, IReadOnlyList<object?> Original, IReadOnlyDictionary<int, object?> Cells);

/// <summary>A staged DELETE of the row with <see cref="Key"/>.</summary>
public sealed record StagedDelete(RowKey Key, IReadOnlyList<object?> Original);

/// <summary>A staged INSERT: <see cref="Row"/> is the grid row array; only <see cref="SetColumns"/> are sent (others use DEFAULT).</summary>
public sealed record StagedInsert(object?[] Row, IReadOnlySet<int> SetColumns);

/// <summary>
/// Staged grid edits for one table (spec §8 "Pending changes model"): cell edits of existing rows keyed by their original
/// primary-key values (so they survive paging and re-apply to reloaded rows), inserted rows, deleted rows, and an
/// undo/redo stack. The grid's row arrays are updated in place (an edited cell shows its new value; a cell set to DEFAULT
/// shows <see cref="SqlDefault.Instance"/>). Not thread-safe: use from the UI thread.
/// </summary>
public sealed class PendingChanges
{
    private sealed class UpdateEntry(object?[] original)
    {
        public object?[] Original { get; } = original;
        public Dictionary<int, object?> Cells { get; } = [];
    }

    private sealed class InsertEntry(object?[] row)
    {
        public object?[] Row { get; } = row;
        public HashSet<int> Set { get; } = [];
    }

    private interface IOp
    {
        void Do(PendingChanges p);
        void Undo(PendingChanges p);
    }

    private readonly int[] _keyColumns;
    private readonly OrderedDictionary<RowKey, UpdateEntry> _updates = new();
    private readonly OrderedDictionary<RowKey, object?[]> _deletes = new();
    private readonly List<InsertEntry> _inserts = [];
    private readonly Dictionary<RowKey, object?[]> _attached = [];
    private readonly Dictionary<object?[], RowKey> _keyOf = new(ReferenceEqualityComparer.Instance);
    private readonly List<IOp> _undo = [];
    private readonly List<IOp> _redo = [];
    private List<IOp>? _batch;
    private int _batchDepth;

    public PendingChanges(int columnCount, IReadOnlyList<int> keyColumns)
    {
        if (keyColumns.Count == 0) throw new ArgumentException("Editing requires a primary key.", nameof(keyColumns));
        ColumnCount = columnCount;
        _keyColumns = keyColumns.ToArray();
    }

    public int ColumnCount { get; }

    public IReadOnlyList<int> KeyColumns => _keyColumns;

    /// <summary>Raised after every change, undo, redo and clear.</summary>
    public event EventHandler? Changed;

    public bool IsDirty => _updates.Count > 0 || _deletes.Count > 0 || _inserts.Count > 0;

    /// <summary>Number of staged row operations (updates not hidden by a delete + deletes + inserts).</summary>
    public int Count => _updates.Keys.Count(k => !_deletes.ContainsKey(k)) + _deletes.Count + _inserts.Count;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public IReadOnlyList<StagedUpdate> Updates =>
        [.. _updates.Where(kv => !_deletes.ContainsKey(kv.Key)).Select(kv => new StagedUpdate(kv.Key, kv.Value.Original, new SortedDictionary<int, object?>(kv.Value.Cells)))];

    public IReadOnlyList<StagedDelete> Deletes => [.. _deletes.Select(kv => new StagedDelete(kv.Key, kv.Value))];

    public IReadOnlyList<StagedInsert> Inserts => [.. _inserts.Select(e => new StagedInsert(e.Row, new SortedSet<int>(e.Set)))];

    /// <summary>The inserted rows' arrays, in order (the view appends them after the loaded rows).</summary>
    public IReadOnlyList<object?[]> InsertedRows => [.. _inserts.Select(e => e.Row)];

    // ---- loaded rows ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Registers a freshly loaded page (replacing the previous one): remembers each row's key from its loaded values and
    /// writes staged cell edits into the arrays so they show again after paging or reload.
    /// </summary>
    public void AttachRows(IEnumerable<object?[]> rows)
    {
        _attached.Clear();
        _keyOf.Clear();
        foreach (var row in rows)
        {
            var key = KeyFromValues(row);
            _keyOf[row] = key;
            _attached[key] = row;
            if (_updates.TryGetValue(key, out var u))
                foreach (var (col, value) in u.Cells) row[col] = value;
        }
    }

    public RowKey KeyFromValues(IReadOnlyList<object?> row) => new(_keyColumns.Select(c => row[c]));

    private RowKey KeyOf(object?[] row)
    {
        if (_keyOf.TryGetValue(row, out var key)) return key;
        key = KeyFromValues(row);
        _keyOf[row] = key;
        _attached.TryAdd(key, row);
        return key;
    }

    public bool IsInserted(object?[] row) => FindInsert(row) >= 0;

    public bool IsDeleted(object?[] row) => !IsInserted(row) && _deletes.ContainsKey(KeyOf(row));

    public PendingRowState GetRowState(object?[] row)
    {
        if (IsInserted(row)) return PendingRowState.New;
        var key = KeyOf(row);
        if (_deletes.ContainsKey(key)) return PendingRowState.Deleted;
        return _updates.ContainsKey(key) ? PendingRowState.Modified : PendingRowState.Unchanged;
    }

    public bool IsCellModified(object?[] row, int column)
    {
        var i = FindInsert(row);
        if (i >= 0) return _inserts[i].Set.Contains(column);
        return _updates.TryGetValue(KeyOf(row), out var u) && u.Cells.ContainsKey(column);
    }

    /// <summary>The value the cell had when loaded (for inserted rows: null).</summary>
    public object? OriginalValue(object?[] row, int column)
    {
        if (IsInserted(row)) return null;
        return _updates.TryGetValue(KeyOf(row), out var u) ? u.Original[column] : row[column];
    }

    // ---- edits ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Stages <paramref name="value"/> for a cell (null = NULL, <see cref="SqlDefault.Instance"/>, <see cref="SqlExpression"/>,
    /// or a value/string). Setting an existing row's cell back to its original value un-stages it. Returns false when
    /// nothing changed or the row is staged for deletion.
    /// </summary>
    public bool SetCell(object?[] row, int column, object? value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(column);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(column, ColumnCount);
        var ins = FindInsert(row);
        if (ins >= 0)
        {
            var e = _inserts[ins];
            if (e.Set.Contains(column) && Equals(e.Row[column], value)) return false;
            Execute(new InsertCellOp(e, column, e.Set.Contains(column), e.Row[column], true, value));
            return true;
        }
        var key = KeyOf(row);
        if (_deletes.ContainsKey(key)) return false;
        var had = _updates.TryGetValue(key, out var entry);
        var original = had ? entry!.Original[column] : row[column];
        var hadCell = had && entry!.Cells.ContainsKey(column);
        var prev = hadCell ? entry!.Cells[column] : original;
        if (ValueEquals(prev, value)) return false;
        var originalRow = had ? entry!.Original : (object?[])row.Clone();
        var unstage = ValueEquals(original, value) && value is not (SqlDefault or SqlExpression);
        Execute(new UpdateCellOp(key, originalRow, column, hadCell, prev, !unstage, value));
        return true;
    }

    /// <summary>Stages a new row. <paramref name="values"/> (copied) pre-fills it; only <paramref name="setColumns"/> are
    /// sent (default: none, i.e. DEFAULT VALUES) and unset cells show DEFAULT. Returns the new row array.</summary>
    public object?[] InsertRow(IReadOnlyList<object?>? values = null, IEnumerable<int>? setColumns = null, int? index = null)
    {
        var row = new object?[ColumnCount];
        var entry = new InsertEntry(row);
        if (setColumns is not null)
            foreach (var c in setColumns) entry.Set.Add(c);
        for (var c = 0; c < ColumnCount; c++)
            row[c] = entry.Set.Contains(c) ? values is not null && c < values.Count ? values[c] : null : SqlDefault.Instance;
        Execute(new InsertOp(entry, Math.Clamp(index ?? _inserts.Count, 0, _inserts.Count)));
        return row;
    }

    /// <summary>Stages deletes for <paramref name="rows"/> as one undo step (inserted rows are simply removed).</summary>
    public void DeleteRows(IEnumerable<object?[]> rows)
    {
        using (BeginBatch())
        {
            foreach (var row in rows.ToList())
            {
                var ins = FindInsert(row);
                if (ins >= 0)
                {
                    Execute(new RemoveInsertOp(_inserts[ins], ins));
                    continue;
                }
                var key = KeyOf(row);
                if (_deletes.ContainsKey(key)) continue;
                var original = _updates.TryGetValue(key, out var u) ? u.Original : (object?[])row.Clone();
                Execute(new DeleteOp(key, original));
            }
        }
    }

    public void DeleteRow(object?[] row) => DeleteRows([row]);

    /// <summary>Un-stages deletes of <paramref name="rows"/> (one undo step).</summary>
    public void RestoreRows(IEnumerable<object?[]> rows)
    {
        using (BeginBatch())
        {
            foreach (var row in rows.ToList())
            {
                if (IsInserted(row)) continue;
                var key = KeyOf(row);
                if (_deletes.TryGetValue(key, out var original)) Execute(new UndeleteOp(key, original));
            }
        }
    }

    /// <summary>Groups every change until disposal into one undo step. Nestable.</summary>
    public IDisposable BeginBatch()
    {
        if (_batchDepth++ == 0) _batch = [];
        return new BatchScope(this);
    }

    private sealed class BatchScope(PendingChanges owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner.EndBatch();
        }
    }

    private void EndBatch()
    {
        if (--_batchDepth > 0) return;
        var ops = _batch!;
        _batch = null;
        if (ops.Count == 0) return;
        _undo.Add(ops.Count == 1 ? ops[0] : new CompositeOp(ops));
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Undo()
    {
        if (_undo.Count == 0 || _batchDepth > 0) return false;
        var op = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        op.Undo(this);
        _redo.Add(op);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0 || _batchDepth > 0) return false;
        var op = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        op.Do(this);
        _undo.Add(op);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Drops everything staged (restoring attached rows' original values) and clears undo/redo.</summary>
    public void Clear()
    {
        foreach (var (key, u) in _updates)
            if (_attached.TryGetValue(key, out var row))
                foreach (var col in u.Cells.Keys) row[col] = u.Original[col];
        _updates.Clear();
        _deletes.Clear();
        _inserts.Clear();
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Execute(IOp op)
    {
        op.Do(this);
        if (_batch is not null)
        {
            _batch.Add(op);
            return;
        }
        _undo.Add(op);
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private int FindInsert(object?[] row)
    {
        for (var i = 0; i < _inserts.Count; i++)
            if (ReferenceEquals(_inserts[i].Row, row)) return i;
        return -1;
    }

    private static bool ValueEquals(object? a, object? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (Equals(a, b)) return true;
        // A typed value and the text the user typed for it ("42" vs 42) are not equal on purpose: the server decides.
        return false;
    }

    // ---- primitive state changes (used by ops) ---------------------------------------------------------------------

    private void ApplyUpdateCell(RowKey key, object?[] originalRow, int column, bool staged, object? value)
    {
        if (!_updates.TryGetValue(key, out var entry))
        {
            if (!staged) return;
            entry = new UpdateEntry(originalRow);
            _updates.Add(key, entry);
        }
        if (staged) entry.Cells[column] = value;
        else entry.Cells.Remove(column);
        if (entry.Cells.Count == 0) _updates.Remove(key);
        if (_attached.TryGetValue(key, out var row)) row[column] = staged ? value : entry.Original[column];
    }

    private sealed class UpdateCellOp(RowKey key, object?[] originalRow, int column, bool hadBefore, object? before, bool stagedAfter, object? after) : IOp
    {
        public void Do(PendingChanges p) => p.ApplyUpdateCell(key, originalRow, column, stagedAfter, after);
        public void Undo(PendingChanges p) => p.ApplyUpdateCell(key, originalRow, column, hadBefore, before);
    }

    private sealed class InsertCellOp(InsertEntry entry, int column, bool wasSet, object? before, bool isSet, object? after) : IOp
    {
        public void Do(PendingChanges p) => Apply(isSet, after);
        public void Undo(PendingChanges p) => Apply(wasSet, before);

        private void Apply(bool set, object? value)
        {
            if (set) entry.Set.Add(column);
            else entry.Set.Remove(column);
            entry.Row[column] = value;
        }
    }

    private sealed class InsertOp(InsertEntry entry, int index) : IOp
    {
        public void Do(PendingChanges p) => p._inserts.Insert(Math.Min(index, p._inserts.Count), entry);
        public void Undo(PendingChanges p) => p._inserts.Remove(entry);
    }

    private sealed class RemoveInsertOp(InsertEntry entry, int index) : IOp
    {
        public void Do(PendingChanges p) => p._inserts.Remove(entry);
        public void Undo(PendingChanges p) => p._inserts.Insert(Math.Min(index, p._inserts.Count), entry);
    }

    private sealed class DeleteOp(RowKey key, object?[] original) : IOp
    {
        public void Do(PendingChanges p) => p._deletes[key] = original;
        public void Undo(PendingChanges p) => p._deletes.Remove(key);
    }

    private sealed class UndeleteOp(RowKey key, object?[] original) : IOp
    {
        public void Do(PendingChanges p) => p._deletes.Remove(key);
        public void Undo(PendingChanges p) => p._deletes[key] = original;
    }

    private sealed class CompositeOp(List<IOp> ops) : IOp
    {
        public void Do(PendingChanges p)
        {
            foreach (var op in ops) op.Do(p);
        }

        public void Undo(PendingChanges p)
        {
            for (var i = ops.Count - 1; i >= 0; i--) ops[i].Undo(p);
        }
    }
}
