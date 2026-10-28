using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using TablePlsu.App.Features.Results;
using TablePlsu.App.Features.TableData;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Commands;
using TablePlsu.Core.Data;
using TablePlsu.Core.Db;
using TablePlsu.Core.Metadata;

namespace TablePlsu.App.Features.SqlEditor;

/// <summary>
/// One result tab under the SQL editor ("Query N", or the <c>-- name:</c> title): the streamed rows for the Data view and
/// the text of the Message view (command tag, affected rows, notices, error). Updated on the UI thread. When the finished
/// result comes from one table with its primary key (spec §10 "Edit results inline", decided by
/// <see cref="ResultEditability"/>), its grid is editable with the table tab's staging, preview and commit machinery.
/// </summary>
public sealed partial class ResultTabViewModel : ObservableObject, IEditableGridModel, IRowStateProvider, IStagedChangesSource, ICommandTarget
{
    private readonly StringBuilder _messages = new();

    public ResultTabViewModel(int statementIndex, string title, SqlStatementInfo statement)
    {
        StatementIndex = statementIndex;
        Title = title;
        Statement = statement;
    }

    /// <summary>Index of the statement in the run (0-based).</summary>
    public int StatementIndex { get; }

    public string Title { get; }

    /// <summary>The statement text and its offset in the editor.</summary>
    public SqlStatementInfo Statement { get; }

    [ObservableProperty]
    public partial IReadOnlyList<ResultColumn>? Columns { get; set; }

    /// <summary>Rows appended in batches while streaming.</summary>
    public BulkObservableCollection<object?[]> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowData), nameof(ShowMessageView))]
    public partial bool HasResultSet { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial int RowCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial TimeSpan Elapsed { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial bool Truncated { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial bool IsDone { get; set; }

    [ObservableProperty]
    public partial bool Cancelled { get; set; }

    [ObservableProperty]
    public partial QueryError? Error { get; set; }

    public bool HasError => Error is not null;

    partial void OnErrorChanged(QueryError? value) => OnPropertyChanged(nameof(HasError));

    [ObservableProperty]
    public partial string? CommandTag { get; set; }

    [ObservableProperty]
    public partial long? AffectedRows { get; set; }

    /// <summary>The row limit in force when the statement ran (for the truncated indicator).</summary>
    public int RowLimit { get; init; }

    /// <summary>The Message view is shown (instead of the Data view).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowData), nameof(ShowMessageView))]
    public partial bool ShowMessages { get; set; }

    public bool ShowData => HasResultSet && !ShowMessages;

    public bool ShowMessageView => !ShowData;

    [ObservableProperty]
    public partial string MessagesText { get; private set; } = "";

    /// <summary>Result bar text: "12 rows · 4 ms", "limited to 1,000 rows", "running…".</summary>
    public string Summary
    {
        get
        {
            var sb = new StringBuilder();
            if (HasResultSet) sb.Append(RowCount.ToString("N0", CultureInfo.CurrentCulture)).Append(RowCount == 1 ? " row" : " rows");
            else if (AffectedRows is { } a && IsDone) sb.Append(a.ToString("N0", CultureInfo.CurrentCulture)).Append(a == 1 ? " row affected" : " rows affected");
            if (Truncated) sb.Append(sb.Length > 0 ? " · " : "").Append("limited to ").Append(RowLimit.ToString("N0", CultureInfo.CurrentCulture));
            if (IsDone) sb.Append(sb.Length > 0 ? " · " : "").Append(FormatElapsed(Elapsed));
            else sb.Append(sb.Length > 0 ? " · " : "").Append("running…");
            return sb.ToString();
        }
    }

    public void AppendMessage(string line)
    {
        if (_messages.Length > 0) _messages.AppendLine();
        _messages.Append(line);
        MessagesText = _messages.ToString();
    }

    /// <summary>Appends a streamed batch.</summary>
    public void AddBatch(ResultBatch batch) => AddRows(batch.Columns, batch.Rows);

    /// <summary>Appends streamed rows (one or more coalesced batches of the statement) with one collection update.</summary>
    public void AddRows(IReadOnlyList<ResultColumn> columns, IReadOnlyList<object?[]> rows)
    {
        if (Columns is null && columns.Count > 0) Columns = columns;
        if (columns.Count > 0) HasResultSet = true;
        if (rows.Count > 0) Rows.AddRange(rows);
        RowCount = Rows.Count;
    }

    /// <summary>Applies the statement's final outcome (from the last batch).</summary>
    public void Complete(StatementResult result)
    {
        if (Columns is null && result.Columns.Count > 0) Columns = result.Columns;
        HasResultSet |= result.HasResultSet;
        CommandTag = result.CommandTag;
        AffectedRows = result.AffectedRows;
        Elapsed = result.Elapsed;
        Truncated = result.Truncated;
        Cancelled = result.Cancelled;
        Error = result.Error;
        RowCount = Rows.Count;
        foreach (var line in MessageLines(result, Rows.Count)) AppendMessage(line);
        IsDone = true;
        if (!HasResultSet || Error is not null) ShowMessages = true;
        else AnalyzeEditabilityAsync(result).Forget(ex => ReadOnlyReason = "couldn't check the source table (" + ex.Message + ")");
    }

    /// <summary>The Message view lines for a finished statement.</summary>
    public static IEnumerable<string> MessageLines(StatementResult result, int loadedRows)
    {
        foreach (var n in result.Notices)
        {
            yield return $"{n.Severity}: {n.Message}";
            if (!string.IsNullOrEmpty(n.Detail)) yield return "  DETAIL: " + n.Detail;
            if (!string.IsNullOrEmpty(n.Hint)) yield return "  HINT: " + n.Hint;
        }
        if (result.Error is { } e)
        {
            yield return "ERROR: " + e;
            if (e.Line is { } line) yield return $"  at line {line}" + (e.Column is { } col ? $", column {col}" : "");
            if (!string.IsNullOrEmpty(e.Detail)) yield return "  DETAIL: " + e.Detail;
            if (!string.IsNullOrEmpty(e.Hint)) yield return "  HINT: " + e.Hint;
        }
        else if (result.Cancelled)
        {
            yield return "Cancelled.";
        }
        else
        {
            var tag = result.CommandTag ?? "OK";
            if (result.HasResultSet) yield return $"{tag}: {loadedRows:N0} row{(loadedRows == 1 ? "" : "s")}" + (result.Truncated ? " (row limit reached, more rows exist)" : "");
            else if (result.AffectedRows is { } a) yield return $"{tag}: {a:N0} row{(a == 1 ? "" : "s")} affected";
            else yield return tag;
        }
        yield return "Elapsed: " + FormatElapsed(result.Elapsed);
    }

    public static string FormatElapsed(TimeSpan t) =>
        t.TotalMilliseconds < 1000 ? $"{t.TotalMilliseconds:0} ms"
        : t.TotalSeconds < 60 ? $"{t.TotalSeconds:0.00} s"
        : $"{(int)t.TotalMinutes} min {t.Seconds} s";

    // ---- inline editing (spec §10 "Edit results inline") ----------------------------------------------------------

    private List<object?[]> _loadedRows = [];
    private ChangesPreviewWindow? _preview;
    private CancellationTokenSource? _analysisCts;

    /// <summary>The workspace the statement ran in (set by the editor; null in unit tests without one).</summary>
    public IWorkspace? Workspace { get; init; }

    /// <summary>Set by the editor view while it shows this result.</summary>
    public ResultGrid? Grid { get; set; }

    /// <summary>The editability verdict (null until the finished result has been analyzed).</summary>
    public ResultEditInfo? EditInfo { get; private set; }

    public PendingChanges? Pending { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadOnlyGrid))]
    public partial bool IsEditable { get; private set; }

    /// <summary>A commit is being confirmed or sent: staging is locked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadOnlyGrid))]
    public partial bool IsCommitting { get; private set; }

    public bool IsReadOnlyGrid => !IsEditable || IsCommitting;

    /// <summary>Why the result is read-only ("result combines columns from several tables"), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReadOnlyReason), nameof(ReadOnlyText))]
    public partial string? ReadOnlyReason { get; private set; }

    public bool HasReadOnlyReason => ReadOnlyReason is not null;

    /// <summary>The info bar text: "Read-only — reason".</summary>
    public string? ReadOnlyText => ReadOnlyReason is null ? null : "Read-only — " + ReadOnlyReason;

    [ObservableProperty]
    public partial bool HasPendingChanges { get; private set; }

    public int PendingCount => Pending?.Count ?? 0;

    public string? LastCommitError { get; private set; }

    public int LastFailedStatement { get; private set; } = -1;

    /// <summary>Raised when the user tries to edit a read-only result (the view flashes the info bar).</summary>
    public event EventHandler? ReadOnlyAttempted;

    /// <summary>Raised after staged changes change (the view refreshes row visuals).</summary>
    public event EventHandler? PendingChanged;

    /// <summary>How many read-only attempts were reported (tests).</summary>
    public int ReadOnlyAttempts { get; private set; }

    public void NotifyReadOnlyAttempt()
    {
        ReadOnlyAttempts++;
        ReadOnlyAttempted?.Invoke(this, EventArgs.Empty);
    }

    ResultGrid? IGridEditHost.EditGrid => Grid;

    ColumnInfo? IGridEditHost.ColumnInfoAt(int columnIndex) =>
        EditInfo is { ColumnInfos: var infos } && columnIndex >= 0 && columnIndex < infos.Count ? infos[columnIndex] : null;

    IReadOnlyList<string> IGridEditHost.PrimaryKeyColumns => EditInfo?.Table?.PrimaryKey ?? [];

    void IGridEditHost.ScrollToRow(object?[] row)
    {
        var index = Rows.IndexOf(row);
        if (index < 0 || Grid is not { } grid) return;
        grid.Dispatcher.BeginInvoke(() =>
        {
            grid.ScrollToRow(index, grid.CurrentCellIndex?.ColumnIndex ?? 0);
            grid.DataGrid.Focus();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    public bool CanStage => IsEditable && Pending is not null && !IsCommitting;

    public RowState GetRowState(object?[] row) => GridEditing.RowStateOf(Pending, row);

    public bool IsCellModified(object?[] row, int columnIndex) => Pending?.IsCellModified(row, columnIndex) == true;

    public bool CanEditCell(object?[] row, int columnIndex) => GridEditing.CanEditCell(this, row, columnIndex);

    /// <summary>Grid edit commit: stage the typed text. Returns the value the grid should show.</summary>
    public object? CommitCellEdit(object?[] row, int columnIndex, string text) => GridEditing.CommitCellEdit(this, row, columnIndex, text);

    public object?[]? InsertRow() => GridEditing.InsertRow(this);

    public void DuplicateRows(IReadOnlyList<object?[]> rows) => GridEditing.DuplicateRows(this, rows);

    public int PasteAsNewRows(string? text, int? startColumn = null) => GridEditing.PasteAsNewRows(this, text, startColumn);

    public bool Undo() => !IsCommitting && Pending?.Undo() == true;

    public bool Redo() => !IsCommitting && Pending?.Redo() == true;

    /// <summary>
    /// Decides whether the finished result can be edited (provenance → source table → primary key) and, when it can,
    /// starts staging. Until it completes the grid stays read-only.
    /// </summary>
    public async Task AnalyzeEditabilityAsync(StatementResult result)
    {
        if (Workspace is not { } ws || !HasResultSet || result.Error is not null || Columns is not { } columns) return;
        _analysisCts?.Cancel();
        var cts = new CancellationTokenSource();
        _analysisCts = cts;
        ResultEditInfo info;
        var rows = Rows.ToList();
        try
        {
            info = await ResultEditability.AnalyzeResultAsync(result.CommandTag, columns, rows, ws.Metadata, ws.Session, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            info = ResultEditInfo.ReadOnly("couldn't check the source table (" + ex.Message + ")");
        }
        if (cts.IsCancellationRequested) return;
        _loadedRows = rows;
        ApplyEditInfo(info);
    }

    /// <summary>Applies an editability verdict (tests may call it with a verdict computed elsewhere).</summary>
    public void ApplyEditInfo(ResultEditInfo info)
    {
        EditInfo = info;
        DetachPending();
        if (info.Target is { } target)
        {
            if (_loadedRows.Count == 0 && Rows.Count > 0) _loadedRows = [.. Rows];
            Pending = new PendingChanges(target.Columns.Count, target.KeyColumns);
            Pending.AttachRows(_loadedRows);
            Pending.Changed += OnPendingChanged;
        }
        ReadOnlyReason = info.Reason;
        IsEditable = info.IsEditable;
    }

    private void DetachPending()
    {
        if (Pending is null) return;
        Pending.Changed -= OnPendingChanged;
        Pending = null;
        HasPendingChanges = false;
    }

    private void OnPendingChanged(object? sender, EventArgs e)
    {
        HasPendingChanges = Pending?.IsDirty == true;
        GridEditing.SyncInsertedRows(this, _loadedRows);
        RowCount = Rows.Count;
        OnPropertyChanged(nameof(PendingCount));
        PendingChanged?.Invoke(this, EventArgs.Empty);
        _preview?.Refresh();
    }

    string IStagedChangesSource.ChangesTitle => EditInfo?.Table?.QualifiedName ?? Title;

    /// <summary>The statements Commit would send (UPDATE/INSERT return the committed rows).</summary>
    public IReadOnlyList<SqlCommandSpec> GenerateStatements() =>
        EditInfo?.Target is { } t && Pending is not null ? ChangeSqlGenerator.Generate(t, Pending, returnRows: true) : [];

    /// <summary>
    /// Sends every staged statement in one transaction (through Safe Mode). On success the edited and inserted rows show
    /// the values the server stored (read back with RETURNING inside the same transaction) and deleted rows disappear.
    /// </summary>
    public async Task<bool> CommitCoreAsync(bool showErrors = true)
    {
        if (IsCommitting || Workspace is not { } ws || Pending is not { IsDirty: true } pending) return false;
        IsCommitting = true;
        try
        {
            // Snapshot the staged operations in statement order (updates, deletes, inserts; see ChangeSqlGenerator).
            var updates = pending.Updates;
            var deletes = pending.Deletes;
            var inserts = pending.Inserts;
            var statements = GenerateStatements();
            if (statements.Count == 0) return false;
            var result = await ChangeCommitter.CommitAsync(ws, statements).ConfigureAwait(true);
            if (result is null) return false;
            if (!result.Success)
            {
                LastCommitError = result.Error;
                LastFailedStatement = result.FailedIndex;
                _preview?.Refresh();
                if (showErrors) ChangeCommitter.ShowFailure(statements, result);
                return false;
            }
            LastCommitError = null;
            LastFailedStatement = -1;
            ApplyCommitted(pending, updates, deletes, inserts, result);
            _preview?.Close();
            return true;
        }
        finally
        {
            IsCommitting = false;
        }
    }

    private void ApplyCommitted(PendingChanges pending, IReadOnlyList<StagedUpdate> updates, IReadOnlyList<StagedDelete> deletes,
        IReadOnlyList<StagedInsert> inserts, CommitResult result)
    {
        var updatedRows = updates.Select(u => pending.AttachedRow(u.Key)).ToList();
        var deletedRows = new HashSet<object?[]>(deletes.Select(d => pending.AttachedRow(d.Key)).OfType<object?[]>(), ReferenceEqualityComparer.Instance);
        pending.Clear(); // restores the original values of updated rows and drops the inserted rows from Rows
        for (var i = 0; i < updatedRows.Count; i++)
            if (updatedRows[i] is { } row && Returned(result, i) is { } values) Array.Copy(values, row, Math.Min(values.Length, row.Length));
        _loadedRows.RemoveAll(deletedRows.Contains);
        for (var i = Rows.Count - 1; i >= 0; i--)
            if (deletedRows.Contains(Rows[i])) Rows.RemoveAt(i);
        var offset = updates.Count + deletes.Count;
        for (var i = 0; i < inserts.Count; i++)
        {
            if (Returned(result, offset + i) is not { } values) continue;
            var row = inserts[i].Row;
            Array.Copy(values, row, Math.Min(values.Length, row.Length));
            _loadedRows.Add(row);
            Rows.Add(row);
        }
        pending.AttachRows(_loadedRows); // re-key: an edited key or a new row is addressed by its committed value
        RowCount = Rows.Count;
        PendingChanged?.Invoke(this, EventArgs.Empty);
    }

    private static object?[]? Returned(CommitResult result, int statement) =>
        statement < result.Returned.Count && result.Returned[statement] is [var single] ? single : null;

    public void PreviewChanges()
    {
        if (_preview is not null)
        {
            _preview.Activate();
            return;
        }
        _preview = new ChangesPreviewWindow(this);
        _preview.Closed += (_, _) => _preview = null;
        _preview.Show();
    }

    /// <summary>Drops every staged change (edited cells show their loaded values again, inserted rows go away).</summary>
    public void DiscardChanges()
    {
        if (Pending is null || IsCommitting) return;
        Pending.Clear();
        LastCommitError = null;
        LastFailedStatement = -1;
        _preview?.Close();
    }

    /// <summary>The result tab is going away (re-run or editor closed).</summary>
    public void Close()
    {
        _analysisCts?.Cancel();
        _preview?.Close();
        DetachPending();
    }

    /// <summary>Grid-scope commands while this result's grid has focus.</summary>
    public bool TryExecuteCommand(string commandId, CommandContext context)
    {
        if (!TableTabViewModel.IsEditCommand(commandId)) return false;
        if (!IsEditable)
        {
            if (ReadOnlyReason is null || !ShowData) return false;
            NotifyReadOnlyAttempt();
            return true;
        }
        switch (commandId)
        {
            case CommandIds.GridEditCell:
                Grid?.BeginEditCurrentCell();
                return true;
            case CommandIds.GridInsertRow:
                InsertRow();
                return true;
            case CommandIds.GridDuplicateRow:
                if (Grid is not null) DuplicateRows(Grid.GetSelectedRows());
                return true;
            case CommandIds.GridDeleteRow:
                GridEditing.DeleteSelectedRows(this);
                return true;
            case CommandIds.GridSetValueMenu:
                SetValueMenuRequested?.Invoke(this, EventArgs.Empty);
                return true;
            case CommandIds.GridPaste:
                PasteAsNewRows(GridEditing.ClipboardText());
                return true;
            case CommandIds.GridPasteCells:
                GridEditing.PasteIntoCells(this, GridEditing.ClipboardText());
                return true;
        }
        return false;
    }

    /// <summary>Alt+Enter: the view opens the Set Value menu.</summary>
    public event EventHandler? SetValueMenuRequested;
}

/// <summary>A statement of a run: its text and absolute offset in the editor document.</summary>
public readonly record struct SqlStatementInfo(string Text, int EditorOffset);
