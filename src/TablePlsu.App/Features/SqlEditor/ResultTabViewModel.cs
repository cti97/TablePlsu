using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using TablePlsu.App.Features.Results;
using TablePlsu.Core.Db;

namespace TablePlsu.App.Features.SqlEditor;

/// <summary>
/// One result tab under the SQL editor ("Query N", or the <c>-- name:</c> title): the streamed rows for the Data view and
/// the text of the Message view (command tag, affected rows, notices, error). Updated on the UI thread.
/// </summary>
public sealed partial class ResultTabViewModel : ObservableObject
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
}

/// <summary>A statement of a run: its text and absolute offset in the editor document.</summary>
public readonly record struct SqlStatementInfo(string Text, int EditorOffset);
