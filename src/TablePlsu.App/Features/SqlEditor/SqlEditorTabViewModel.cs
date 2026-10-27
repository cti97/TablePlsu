using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ICSharpCode.AvalonEdit.Document;
using TablePlsu.App.Features.Workspace;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Db;
using TablePlsu.Core.Editors;
using TablePlsu.Core.History;
using TablePlsu.Core.IO;
using TablePlsu.Core.Settings;
using TablePlsu.Core.Sql;

namespace TablePlsu.App.Features.SqlEditor;

/// <summary>A row-limit dropdown entry (0 = no limit).</summary>
public sealed record RowLimitOption(int Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>UI operations the view performs for the view-model (find panel, comments, zoom).</summary>
public interface ISqlEditorView
{
    void FocusEditor();
    void OpenFind(bool replace);
    void FindNext(bool backwards);
    void ToggleLineComment();
    void ToggleBlockComment();
    void ZoomFont(int delta);
}

/// <summary>
/// A SQL editor tab (spec §9/§10): the AvalonEdit <see cref="TextDocument"/>, run current/all with streaming result tabs,
/// cancel, the per-connection row limit, error location, history + console logging and autosave through
/// <see cref="EditorSessionManager"/>. Run logic is UI-free so it can be driven from tests.
/// </summary>
public sealed partial class SqlEditorTabViewModel : TabViewModelBase
{
    /// <summary>With more result tabs than this, a run collapses into one Messages tab (spec §10).</summary>
    public const int MaxResultTabs = 100;

    public static IReadOnlyList<RowLimitOption> RowLimitOptions { get; } =
    [
        new(0, "No limit"), new(100, "100 rows"), new(300, "300 rows"), new(500, "500 rows"), new(1000, "1,000 rows"),
        new(5000, "5,000 rows"), new(10000, "10,000 rows"), new(50000, "50,000 rows"), new(100000, "100,000 rows"),
        new(500000, "500,000 rows"),
    ];

    private readonly EditorSessionManager? _sessions;
    private DispatcherTimer? _saveTimer;
    private bool _textDirty;
    private bool _closed;
    private int _runNumber;

    public SqlEditorTabViewModel(string id, string text, EditorSessionManager? sessions, EditorTabState? state = null)
        : base(TabKinds.SqlEditor, id)
    {
        _sessions = sessions;
        Document = new TextDocument(text);
        Document.UndoStack.ClearAll();
        Document.TextChanged += OnTextChanged;
        if (state is not null)
        {
            Title = state.Title ?? "Query";
            CustomTitle = state.CustomTitle;
            CaretOffset = state.CaretOffset;
            SelectionStart = state.SelectionStart;
            SelectionLength = state.SelectionLength;
            ScrollX = state.ScrollOffsetX;
            ScrollY = state.ScrollOffsetY;
            if (state.LinkedFilePath is { Length: > 0 } p) SetLinkedFile(p);
        }
        RowLimit = sessions?.RowLimit ?? (AppServices.IsInitialized ? AppServices.Current.Editor.DefaultRowLimit : 1000);
        if (sessions is not null) sessions.RowLimitChanged += OnSharedRowLimitChanged;
    }

    public TextDocument Document { get; }

    /// <summary>The view's UI operations (set by the view while it exists).</summary>
    public ISqlEditorView? View { get; set; }

    // ---- view state (pushed by the view; persisted in session.json) ----------------------------------------------

    public int CaretOffset { get; private set; }
    public int SelectionStart { get; private set; }
    public int SelectionLength { get; private set; }
    public double ScrollX { get; private set; }
    public double ScrollY { get; private set; }

    public void UpdateViewState(int caret, int selectionStart, int selectionLength, double scrollX, double scrollY)
    {
        if (caret == CaretOffset && selectionStart == SelectionStart && selectionLength == SelectionLength
            && scrollX.Equals(ScrollX) && scrollY.Equals(ScrollY)) return;
        CaretOffset = caret;
        SelectionStart = selectionStart;
        SelectionLength = selectionLength;
        ScrollX = scrollX;
        ScrollY = scrollY;
        _sessions?.ScheduleSessionSave();
    }

    /// <summary>Sets caret/selection without a view (tests, programmatic runs).</summary>
    public void SetSelection(int caret, int selectionStart = 0, int selectionLength = 0) =>
        UpdateViewState(caret, selectionStart, selectionLength, ScrollX, ScrollY);

    // ---- linked file -------------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial string? LinkedFilePath { get; private set; }

    internal void SetLinkedFile(string? path)
    {
        LinkedFilePath = path;
        ToolTip = path;
        _sessions?.LinkFile(Id, path);
    }

    // ---- run state ---------------------------------------------------------------------------------------------

    public ObservableCollection<ResultTabViewModel> Results { get; } = [];

    [ObservableProperty]
    public partial ResultTabViewModel? SelectedResult { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    public partial bool IsResultsVisible { get; set; } = true;

    [ObservableProperty]
    public partial int RowLimit { get; set; }

    partial void OnRowLimitChanged(int value) => _sessions?.SetRowLimit(value);

    private void OnSharedRowLimitChanged(object? sender, int value) => UiDispatch.Run(() => RowLimit = value);

    /// <summary>What the Run button does: false = Run Current, true = Run All ("Default ▸" in the Run menu).</summary>
    [ObservableProperty]
    public partial bool RunButtonRunsAll { get; set; }

    /// <summary>Text for the overall run status ("3 statements · 12 ms").</summary>
    [ObservableProperty]
    public partial string? RunStatus { get; private set; }

    /// <summary>The error underline in editor offsets (null = none).</summary>
    public (int Start, int Length)? ErrorSpan { get; private set; }

    /// <summary>Raised (UI thread) when a run reports an error at a known place, or when the underline is cleared (null).</summary>
    public event EventHandler<(int Start, int Length)?>? ErrorSpanChanged;

    private void SetErrorSpan((int Start, int Length)? span)
    {
        if (ErrorSpan == span) return;
        ErrorSpan = span;
        ErrorSpanChanged?.Invoke(this, span);
    }

    // ---- running -----------------------------------------------------------------------------------------------

    /// <summary>Ctrl+Enter: the selection, or the statement under the caret.</summary>
    public Task RunCurrentAsync()
    {
        var target = SqlTextOps.CurrentTarget(Document.Text, SelectionStart, SelectionLength, CaretOffset);
        return target is { } t ? RunAsync(t) : Task.CompletedTask;
    }

    /// <summary>Ctrl+Shift+Enter: every statement in sequence.</summary>
    public Task RunAllAsync()
    {
        var target = SqlTextOps.AllTarget(Document.Text);
        return target is { } t ? RunAsync(t) : Task.CompletedTask;
    }

    /// <summary>The Run button: Run Current or Run All (per <see cref="RunButtonRunsAll"/>), or Cancel while running.</summary>
    public Task RunOrCancelAsync()
    {
        if (IsRunning)
        {
            Cancel();
            return Task.CompletedTask;
        }
        return RunButtonRunsAll ? RunAllAsync() : RunCurrentAsync();
    }

    /// <summary>Ctrl+Shift+. / Esc in results / Cancel button: protocol cancel; rows already loaded stay.</summary>
    public void Cancel()
    {
        if (!IsRunning) return;
        Workspace.Session.Cancel();
        Workspace.Log("Cancel requested.");
    }

    /// <summary>
    /// Runs <paramref name="target"/>: Safe Mode check, then streams every statement into its own result tab. History
    /// and console lines are written per statement. Completes after the UI has applied the last batch.
    /// </summary>
    public async Task RunAsync(RunTarget target)
    {
        if (IsRunning || _closed) return;
        var ws = Workspace;
        PushText();
        _sessions?.FlushInBackground();
        SetErrorSpan(null);

        var statements = StatementSplitter.Split(target.Sql);
        if (statements.Count == 0) return;
        var settings = AppServices.IsInitialized ? AppServices.Current : new AppSettings();
        var append = settings.Editor.ResultViewMode == ResultViewMode.Append;
        if (!append)
        {
            Results.Clear();
            SelectedResult = null;
        }
        IsResultsVisible = true;

        if (ws.State != DbSessionState.Connected)
        {
            ShowNotice(ws.State == DbSessionState.Connecting ? "Still connecting… run again in a moment." : "Not connected. Reconnect (Ctrl+Shift+R) and run again.");
            return;
        }

        IsRunning = true;
        IsBusy = true;
        RunStatus = "running…";
        var runNumber = ++_runNumber;
        QueryResult? result = null;
        Exception? failure = null;
        var sw = Stopwatch.StartNew();
        try
        {
            if (!await ws.SafeMode.ConfirmAsync(statements.Select(s => s.Text).ToList()).ConfigureAwait(true))
            {
                ws.Log("Run cancelled by Safe Mode.");
                RunStatus = "Cancelled by Safe Mode";
                return;
            }

            var options = new ExecuteOptions
            {
                RowLimit = RowLimit > 0 ? RowLimit : null,
                ReturnOnError = settings.Editor.ReturnOnError,
                CommandTimeoutSeconds = settings.Connections.CommandTimeoutSeconds,
            };
            var run = new RunState(this, ws, target, statements.Count > MaxResultTabs, append ? Results.Count : 0, RowLimit, Document.TextLength);
            var progress = new InlineProgress<ResultBatch>(run.OnBatch);
            try
            {
                result = await Task.Run(() => ws.Session.ExecuteAsync(target.Sql, options, progress)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            await UiDispatch.InvokeAsync(() => Finish(run, result, failure, sw.Elapsed), DispatcherPriority.Background).ConfigureAwait(true);
        }
        finally
        {
            if (runNumber == _runNumber && IsRunning && result is null && failure is null)
            {
                IsRunning = false;
                IsBusy = false;
            }
        }
    }

    private void Finish(RunState run, QueryResult? result, Exception? failure, TimeSpan elapsed)
    {
        IsRunning = false;
        IsBusy = false;
        if (failure is not null)
        {
            Workspace.ConsoleLog.Add("Query failed: " + failure.Message, ConsoleEntryKind.Error);
            ShowNotice("Query failed: " + failure.Message);
            RunStatus = "Failed";
            return;
        }
        var count = result?.Statements.Count ?? 0;
        var errors = result?.Statements.Count(s => s.Error is not null) ?? 0;
        RunStatus = $"{count} statement{(count == 1 ? "" : "s")}"
                    + (errors > 0 ? $" · {errors} error{(errors == 1 ? "" : "s")}" : "")
                    + (result?.Cancelled == true ? " · cancelled" : "")
                    + " · " + ResultTabViewModel.FormatElapsed(result?.Elapsed ?? elapsed);
        if (SelectedResult is null && Results.Count > 0) SelectedResult = run.FirstError ?? Results[^1];
    }

    /// <summary>A Messages-only result tab with one line (not connected, failures).</summary>
    private void ShowNotice(string message)
    {
        var tab = new ResultTabViewModel(-1, "Messages", new SqlStatementInfo("", 0)) { ShowMessages = true, IsDone = true };
        tab.AppendMessage(message);
        Results.Add(tab);
        SelectedResult = tab;
    }

    /// <summary>Per-run streaming state. <see cref="OnBatch"/> runs on the query thread; UI work is posted in order.</summary>
    private sealed class RunState(SqlEditorTabViewModel owner, IWorkspace ws, RunTarget target, bool collapse, int numberOffset, int rowLimit, int documentLength)
    {
        private readonly Dictionary<int, ResultTabViewModel> _tabs = [];
        private ResultTabViewModel? _collapsed;

        public ResultTabViewModel? FirstError { get; private set; }

        public void OnBatch(ResultBatch batch)
        {
            if (batch is { IsFinal: true, Result: { } r }) RecordHistory(batch, r);
            UiDispatch.PostBackground(() => Apply(batch));
        }

        private void RecordHistory(ResultBatch batch, StatementResult r)
        {
            var title = TitleOf(batch);
            var rows = r.HasResultSet ? batch.TotalRows : r.AffectedRows;
            try
            {
                ws.History.Append(ws.Profile.Id, new HistoryEntry
                {
                    Database = ws.Database,
                    Sql = batch.Statement.Text,
                    DurationMs = r.Elapsed.TotalMilliseconds,
                    Rows = rows,
                    Error = r.Error is not null,
                    Source = "editor",
                });
            }
            catch (Exception ex)
            {
                ws.ConsoleLog.Add("History write failed: " + ex.Message, ConsoleEntryKind.Error);
            }
            var summary = r.Error is { } e ? "ERROR " + e
                : r.Cancelled ? "cancelled"
                : r.HasResultSet ? $"{batch.TotalRows:N0} row{(batch.TotalRows == 1 ? "" : "s")}{(r.Truncated ? " (limited)" : "")}"
                : r.AffectedRows is { } a ? $"{r.CommandTag} {a:N0}" : r.CommandTag ?? "OK";
            ws.ConsoleLog.Add($"{title}: {summary} in {ResultTabViewModel.FormatElapsed(r.Elapsed)}",
                r.Error is null ? ConsoleEntryKind.Info : ConsoleEntryKind.Error);
        }

        private string TitleOf(ResultBatch batch) =>
            TabNameDirective.Parse(batch.Statement.Text) ?? $"Query {numberOffset + batch.StatementIndex + 1}";

        private void Apply(ResultBatch batch)
        {
            if (owner._closed) return;
            var tab = TabFor(batch);
            if (!collapse) tab.AddBatch(batch);
            if (batch is not { IsFinal: true, Result: { } r }) return;

            if (collapse)
            {
                tab.AppendMessage($"-- {TitleOf(batch)}");
                foreach (var line in ResultTabViewModel.MessageLines(r, batch.TotalRows)) tab.AppendMessage(line);
                tab.RowCount++;
            }
            else
            {
                tab.Complete(r);
            }

            if (r.Error is { } error && FirstError is null)
            {
                FirstError = tab;
                owner.SelectedResult = tab;
                var position = error.Position ?? batch.Statement.Start;
                var (start, length) = SqlTextOps.ErrorSpan(target.Sql, Math.Clamp(position, 0, Math.Max(0, target.Sql.Length - 1)));
                var docStart = target.BaseOffset + start;
                if (docStart + length <= documentLength) owner.SetErrorSpan((docStart, length));
            }
        }

        private ResultTabViewModel TabFor(ResultBatch batch)
        {
            if (collapse)
            {
                if (_collapsed is null)
                {
                    _collapsed = new ResultTabViewModel(0, "Messages", new SqlStatementInfo(target.Sql, target.BaseOffset)) { ShowMessages = true, RowLimit = rowLimit };
                    owner.Results.Add(_collapsed);
                    owner.SelectedResult ??= _collapsed;
                }
                return _collapsed;
            }
            if (_tabs.TryGetValue(batch.StatementIndex, out var tab)) return tab;
            tab = new ResultTabViewModel(batch.StatementIndex, TitleOf(batch),
                new SqlStatementInfo(batch.Statement.Text, target.BaseOffset + batch.Statement.Start)) { RowLimit = rowLimit };
            _tabs[batch.StatementIndex] = tab;
            owner.Results.Add(tab);
            owner.SelectedResult ??= tab;
            return tab;
        }
    }

    /// <summary>Reports synchronously on the caller's thread (keeps batch order; DbSession requires an ordered IProgress).</summary>
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    // ---- autosave ----------------------------------------------------------------------------------------------

    private void OnTextChanged(object? sender, EventArgs e)
    {
        _textDirty = true;
        SetErrorSpan(null);
        var dispatcher = UiDispatch.Dispatcher;
        if (dispatcher is null || !dispatcher.CheckAccess())
        {
            PushText();
            return;
        }
        // editor.autoSaveDebounceMs is read on every keystroke so a settings change applies to open tabs too.
        var ms = AppServices.IsInitialized ? AppServices.Current.Editor.AutoSaveDebounceMs : 500;
        var interval = TimeSpan.FromMilliseconds(Math.Max(20, ms - 20));
        if (_saveTimer is not null && _saveTimer.Interval != interval) _saveTimer.Interval = interval;
        if (_saveTimer is null)
        {
            _saveTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = interval };
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer!.Stop();
                PushText();
            };
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>The current autosave debounce (null before the first keystroke). For tests.</summary>
    internal TimeSpan? AutoSaveDelay => _saveTimer?.Interval;

    /// <summary>Hands the current text to the autosave store if it changed (UI thread).</summary>
    public void PushText()
    {
        _saveTimer?.Stop();
        if (!_textDirty) return;
        _textDirty = false;
        _sessions?.SaveText(Id, Document.Text);
    }

    /// <summary>The persisted state of this tab for session.json.</summary>
    public EditorTabState ToState() => new()
    {
        Id = Id,
        Title = Title,
        CustomTitle = CustomTitle,
        CaretOffset = CaretOffset,
        SelectionStart = SelectionStart,
        SelectionLength = SelectionLength,
        ScrollOffsetX = ScrollX,
        ScrollOffsetY = ScrollY,
        LinkedFilePath = LinkedFilePath,
    };

    public override void Flush()
    {
        PushText();
        _sessions?.FlushNow();
    }

    /// <summary>Ctrl+S: flush autosave and write the linked .sql file (if any).</summary>
    public override void Save()
    {
        PushText();
        _sessions?.FlushNow();
        if (LinkedFilePath is not { } path) return;
        try
        {
            AtomicFile.WriteAllText(path, Document.Text);
            Workspace.Log($"Saved {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Error(ex.Message, "Could not save the file");
        }
    }

    /// <summary>Save SQL As… (Ctrl+Shift+S): writes the text to <paramref name="path"/> and links the tab to it.</summary>
    public void SaveAs(string path)
    {
        AtomicFile.WriteAllText(path, Document.Text);
        SetLinkedFile(path);
        Title = Path.GetFileName(path);
        PushText();
        Workspace.Log($"Saved {path}");
    }

    public override void OnDeactivated() => PushText();

    public override void OnClosed(TabCloseReason reason)
    {
        if (_closed) return;
        PushText();
        _closed = true;
        if (IsRunning) Workspace.Session.Cancel();
        if (_sessions is not null)
        {
            _sessions.RowLimitChanged -= OnSharedRowLimitChanged;
            if (reason == TabCloseReason.User) _sessions.TabClosed(this);
        }
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised when the tab is closed (the view detaches app-wide event handlers).</summary>
    public event EventHandler? Closed;

    public override FrameworkElement CreateView() => new SqlEditorView { DataContext = this };
}
