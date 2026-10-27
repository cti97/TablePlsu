using System.ComponentModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TablePlsu.App.Features.Results;
using TablePlsu.App.Features.Workspace;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Commands;
using TablePlsu.Core.Data;
using TablePlsu.Core.Db;
using TablePlsu.Core.History;
using TablePlsu.Core.Metadata;

namespace TablePlsu.App.Features.TableData;

/// <summary>The bottom tabs of a table tab.</summary>
public enum TableSubView { Data, Structure, Indexes, Ddl }

/// <summary>What the view-model needs from its view (set by <see cref="TableTabView"/>; null in unit tests).</summary>
public interface ITableDataView
{
    ResultGrid Grid { get; }
    void ShowSetValueMenu();
    void FocusFilterBar();
    void RefreshRowVisuals();
    void ScrollToRow(int rowIndex, int columnIndex = 0);
}

/// <summary>
/// A table / view / materialized view tab (spec §6, §8): server-side paging, sort and filters over
/// <see cref="TableQueryBuilder"/>; editing (tables with a primary key only) staged in <see cref="PendingChanges"/> with
/// undo/redo; Preview / Discard / Commit (one transaction, through Safe Mode); Data / Structure / Indexes bottom tabs.
/// </summary>
public sealed partial class TableTabViewModel : TabViewModelBase, ICommandTarget, IRowStateProvider
{
    private static readonly Dictionary<string, FilterBarState> FilterMemory = new(StringComparer.Ordinal);

    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _countCts;
    private List<object?[]> _pageRows = [];
    private List<string?[]>? _pageKeyTexts;
    private IReadOnlyList<string> _pageKeyColumns = [];
    private bool _loadWhenConnected;
    private bool _metadataLoading;
    private readonly Dictionary<TableSubView, FrameworkElement> _subViews = [];
    private ChangesPreviewWindow? _preview;

    public TableTabViewModel(TabRequest request) : base(request.Kind)
    {
        Schema = request.Schema ?? "public";
        Name = request.Name ?? throw new ArgumentException("A table tab needs a name.", nameof(request));
        ObjectKind = request.ObjectKind ?? request.Kind;
        Title = Name;
        ToolTip = Schema + "." + Name;
        IsPreview = !request.Pinned;
        PageSize = AppServices.IsInitialized ? AppServices.Current.Table.PageSize : 300;
        if (AppServices.IsInitialized) AppServices.SettingsChanged += OnSettingsChanged;
        Filter.AppliedChanged += (_, _) =>
        {
            Offset = 0;
            RememberFilters();
            Load(recount: true);
        };
        Filter.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FilterBarViewModel.IsVisible)) RememberFilters();
        };
        if (request.Parameter is FilterCondition initial)
        {
            Filter.Restore(new FilterBarState([initial], [initial], FilterJoin.And, true));
        }
    }

    /// <summary>Set by the view; used for menus, focus and visual refreshes.</summary>
    public ITableDataView? View { get; set; }

    public bool IsTableKind => Kind == TabKinds.Table;

    public bool IsViewKind => Kind is TabKinds.View or TabKinds.MaterializedView;

    public FilterBarViewModel Filter { get; } = new();

    public BulkObservableCollection<object?[]> Rows { get; } = new();

    [ObservableProperty]
    public partial IReadOnlyList<ResultColumn>? Columns { get; private set; }

    public TableInfo? Info { get; private set; }

    public PendingChanges? Pending { get; private set; }

    public IReadOnlyList<SortSpec> Sorts { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadOnlyGrid))]
    public partial bool IsEditable { get; private set; }

    public bool IsReadOnlyGrid => !IsEditable || IsCommitting;

    /// <summary>A commit is being confirmed or sent: staging is locked (see <see cref="CanStage"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadOnlyGrid))]
    public partial bool IsCommitting { get; private set; }

    /// <summary>Why the grid is read-only (shown as a banner), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReadOnlyReason))]
    public partial string? ReadOnlyReason { get; private set; }

    public bool HasReadOnlyReason => ReadOnlyReason is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool HasError => ErrorText is not null;

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText))]
    public partial int PageSize { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText), nameof(CanPrevPage))]
    public partial long Offset { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText))]
    public partial int LoadedCount { get; private set; }

    [ObservableProperty]
    public partial bool CanNextPage { get; private set; }

    public bool CanPrevPage => Offset > 0;

    public string PageText => LoadedCount == 0
        ? $"{Offset.ToString("N0", CultureInfo.InvariantCulture)}+"
        : $"{(Offset + 1).ToString("N0", CultureInfo.InvariantCulture)}–{(Offset + LoadedCount).ToString("N0", CultureInfo.InvariantCulture)}";

    [ObservableProperty]
    public partial string RowCountText { get; private set; } = "";

    /// <summary>True when <see cref="RowCountText"/> is an estimate or a "Count" prompt (click runs count(*)).</summary>
    [ObservableProperty]
    public partial bool RowCountClickable { get; private set; }

    public long? TotalRows { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDataView), nameof(SubViewContent))]
    public partial TableSubView SelectedSubView { get; set; } = TableSubView.Data;

    public bool IsDataView => SelectedSubView == TableSubView.Data;

    /// <summary>Content of the selected non-Data bottom tab (registry view or placeholder).</summary>
    public FrameworkElement? SubViewContent => SelectedSubView == TableSubView.Data ? null : GetSubView(SelectedSubView);

    public string? LastCommitError { get; private set; }

    public int LastFailedStatement { get; private set; } = -1;

    public int PendingCount => Pending?.Count ?? 0;

    public bool IsPreviewOpen => _preview is not null;

    public bool CanUndo => Pending?.CanUndo == true;

    public bool CanRedo => Pending?.CanRedo == true;

    public override FrameworkElement CreateView() => new TableTabView { DataContext = this };

    // ---- lifecycle ------------------------------------------------------------------------------------------------

    protected override void OnAttached()
    {
        if (FilterMemory.TryGetValue(MemoryKey, out var state) && Filter.Applied.Count == 0) Filter.Restore(state);
        Workspace.StateChanged += OnStateChanged;
        Workspace.ReloadRequested += OnReloadRequested;
        Load(recount: true);
    }

    private string MemoryKey => $"{Workspace.Profile.Id}/{Workspace.Database}/{Schema}.{Name}";

    private void RememberFilters()
    {
        if (Workspace is null) return;
        FilterMemory[MemoryKey] = Filter.Save();
    }

    private void OnStateChanged(object? sender, DbSessionState state)
    {
        if (state == DbSessionState.Connected && _loadWhenConnected) Load(recount: true);
    }

    private void OnReloadRequested(object? sender, EventArgs e)
    {
        if (IsActive) Reload();
    }

    /// <summary>table.pageSize changed: follow it (from the next page load) unless the user set this tab's limit.</summary>
    private void OnSettingsChanged(object? sender, TablePlsu.Core.Settings.SettingsChangedEventArgs e)
    {
        if (!_pageSizeCustomized) PageSize = e.Settings.Table.PageSize;
    }

    private bool _pageSizeCustomized;

    public override void OnClosed(TabCloseReason reason)
    {
        AppServices.SettingsChanged -= OnSettingsChanged;
        Workspace.StateChanged -= OnStateChanged;
        Workspace.ReloadRequested -= OnReloadRequested;
        _loadCts?.Cancel();
        _countCts?.Cancel();
        _preview?.Close();
        RememberFilters();
    }

    public override bool CanCloseNow(TabCloseReason reason)
    {
        if (Pending?.IsDirty != true || reason == TabCloseReason.Replaced) return true;
        return Dialogs.Confirm($"{Schema}.{Name} has {Pending.Count} uncommitted change(s). Close and discard them?", "Discard changes?", "Discard", danger: true);
    }

    public override void Reload() => Load(recount: true, refreshMetadata: true);

    // ---- loading --------------------------------------------------------------------------------------------------

    /// <summary>Starts loading the current page (fire and forget; errors show in <see cref="ErrorText"/>).</summary>
    public void Load(bool recount = false, bool refreshMetadata = false) => LoadAsync(recount, refreshMetadata).Forget();

    public async Task LoadAsync(bool recount = false, bool refreshMetadata = false)
    {
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var ct = cts.Token;
        if (Workspace.State != DbSessionState.Connected)
        {
            _loadWhenConnected = true;
            StatusText = "Waiting for the connection…";
            return;
        }
        _loadWhenConnected = false;
        IsBusy = true;
        ErrorText = null;
        StatusText = "Loading…";
        try
        {
            // The page never waits for metadata: use the cached snapshot now and load the schema in the background
            // (it enables editing and the filter column list when it arrives).
            if (Info is null && !refreshMetadata) Info = Workspace.Metadata.FindTable(Schema, Name!);
            if (Info is null || refreshMetadata) LoadMetadataAsync(refreshMetadata).Forget();
            else Filter.SetColumns(Info.Columns.Select(c => c.Name));
            ct.ThrowIfCancellationRequested();
            var query = BuildQuery();
            var spec = TableQueryBuilder.BuildSelect(query);
            Workspace.Log(spec.LiteralSql);
            var session = Workspace.Session;
            var timeout = AppServices.IsInitialized ? AppServices.Current.Connections.CommandTimeoutSeconds : 0;
            var page = await Task.Run(async () =>
            {
                await using var conn = await session.OpenConnectionAsync(ct).ConfigureAwait(false);
                return await TableDataAccess.LoadPageAsync(conn, spec, timeout, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;
            ApplyPage(page, query.KeyColumns);
            if (recount || TotalRows is null) UpdateRowCountAsync(query, force: false).Forget();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            ErrorText = ex.Message;
            StatusText = "";
            Workspace.Log($"error loading {Schema}.{Name}: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts)) IsBusy = false;
        }
    }

    private async Task LoadMetadataAsync(bool force)
    {
        _metadataLoading = true;
        try
        {
            // No token: a schema load is shared between callers, and cancelling a page load must not cancel it.
            var schema = await Workspace.Metadata.EnsureSchemaLoadedAsync(Schema!, force).ConfigureAwait(true);
            Info = schema?.FindTable(Name!) ?? Workspace.Metadata.FindTable(Schema, Name!);
        }
        catch (Exception ex)
        {
            Workspace.Log($"metadata for {Schema}.{Name} unavailable: {ex.Message}");
            Info ??= Workspace.Metadata.FindTable(Schema, Name!);
        }
        finally
        {
            _metadataLoading = false;
        }
        Filter.SetColumns(Info?.Columns.Select(c => c.Name) ?? Columns?.Select(c => c.Name) ?? []);
        if (Columns is null) return;
        SetupEditing(Columns);
        View?.RefreshRowVisuals();
    }

    /// <summary>The query for the current page, sort and applied filters.</summary>
    public TableQuery BuildQuery() => new()
    {
        Schema = Schema!,
        Table = Name!,
        Columns = Info?.Columns.Select(c => new QueryColumn(c.Name, c.DataType)).ToList()
                  ?? Columns?.Select(c => new QueryColumn(c.Name, c.DataTypeName)).ToList() ?? [],
        SelectColumns = [], // SELECT *: the page shows the table as it is now, even if the metadata cache is stale
        Filters = Filter.Applied,
        Join = Filter.Join,
        Sorts = Sorts,
        Limit = PageSize,
        Offset = Offset,
        KeyColumns = EditKeyColumns,
    };

    /// <summary>The primary key the page query fetches as exact text (rows are identified by it), or none.</summary>
    private IReadOnlyList<string> EditKeyColumns => IsTableKind && Info is { HasPrimaryKey: true } info ? info.PrimaryKey : [];

    private void ApplyPage(TablePage page, IReadOnlyList<string> keyColumns)
    {
        if (Columns is null || !SameColumns(Columns, page.Columns)) Columns = page.Columns;
        if (Info is null) Filter.SetColumns(page.Columns.Select(c => c.Name));
        _pageRows = page.Rows;
        _pageKeyTexts = page.KeyTexts;
        _pageKeyColumns = page.KeyTexts is null ? [] : keyColumns;
        SetupEditing(page.Columns);
        AttachPendingRows();
        Rows.ReplaceAll(Pending is null ? page.Rows : page.Rows.Concat(Pending.InsertedRows));
        LoadedCount = page.Rows.Count;
        CanNextPage = page.Rows.Count >= PageSize && (TotalRows is null || Offset + PageSize < TotalRows);
        StatusText = $"{page.Rows.Count.ToString("N0", CultureInfo.InvariantCulture)} rows · {page.Elapsed.TotalMilliseconds:0} ms";
        View?.Grid.SetSortIndicators(SortIndicators());
        View?.RefreshRowVisuals();
    }

    private static bool SameColumns(IReadOnlyList<ResultColumn> a, IReadOnlyList<ResultColumn> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.Name == p.Second.Name && p.First.DataTypeName == p.Second.DataTypeName);

    public IEnumerable<(int ColumnIndex, ListSortDirection Direction)> SortIndicators()
    {
        if (Columns is null) yield break;
        foreach (var s in Sorts)
        {
            var i = IndexOfColumn(s.Column);
            if (i >= 0) yield return (i, s.Direction == SortDirection.Asc ? ListSortDirection.Ascending : ListSortDirection.Descending);
        }
    }

    private int IndexOfColumn(string name)
    {
        if (Columns is null) return -1;
        for (var i = 0; i < Columns.Count; i++)
            if (Columns[i].Name == name) return i;
        return -1;
    }

    private void SetupEditing(IReadOnlyList<ResultColumn> columns)
    {
        string? reason = null;
        int[] keys = [];
        if (!IsTableKind) reason = Kind == TabKinds.MaterializedView ? "Materialized views are read-only." : "Views are read-only.";
        else if (Info is null) reason = _metadataLoading ? "Loading table metadata…" : "Table metadata is not available, so editing is disabled.";
        else if (!Info.HasPrimaryKey) reason = "This table has no primary key. Rows can't be edited because TablePlsu identifies rows by primary key.";
        else
        {
            keys = [.. Info.PrimaryKey.Select(IndexOfColumnIn(columns))];
            if (keys.Any(k => k < 0)) reason = "The primary key columns are not in the result, so editing is disabled.";
            else if (!PageHasExactKeys)
            {
                // The page was fetched before the primary key was known: rows can only be addressed by their exact key
                // text, so refetch it (with the keys) before allowing edits.
                reason = "Loading row keys…";
                if (!_pageKeyColumns.SequenceEqual(Info.PrimaryKey, StringComparer.Ordinal)) Load();
            }
        }
        ReadOnlyReason = reason;
        IsEditable = reason is null;
        if (!IsEditable)
        {
            if (Pending is { IsDirty: false }) DetachPending();
            return;
        }
        if (Pending is not null && Pending.ColumnCount == columns.Count && Pending.KeyColumns.SequenceEqual(keys)) return;
        if (Pending is { IsDirty: true }) Workspace.Log($"{Schema}.{Name}: the table's columns changed; staged changes were dropped.");
        DetachPending();
        Pending = new PendingChanges(columns.Count, keys);
        Pending.Changed += OnPendingChanged;
        AttachPendingRows();
    }

    /// <summary>True when the loaded rows carry the exact text of the current primary key (or there are no rows).</summary>
    private bool PageHasExactKeys =>
        _pageRows.Count == 0 || (_pageKeyTexts is not null && Info is not null && _pageKeyColumns.SequenceEqual(Info.PrimaryKey, StringComparer.Ordinal));

    /// <summary>Registers the page rows with <see cref="Pending"/> by their exact key text (never by the lossy CLR values).</summary>
    private void AttachPendingRows()
    {
        if (Pending is null) return;
        if (PageHasExactKeys && _pageKeyTexts is not null && _pageKeyTexts.Count == _pageRows.Count && _pageKeyColumns.Count == Pending.KeyColumns.Count)
            Pending.AttachRows(_pageRows, _pageKeyTexts);
        else
            Pending.AttachRows([], []);
    }

    private static Func<string, int> IndexOfColumnIn(IReadOnlyList<ResultColumn> columns) => name =>
    {
        for (var i = 0; i < columns.Count; i++)
            if (columns[i].Name == name) return i;
        return -1;
    };

    private void DetachPending()
    {
        if (Pending is null) return;
        Pending.Changed -= OnPendingChanged;
        Pending = null;
        HasPendingChanges = false;
    }

    private async Task UpdateRowCountAsync(TableQuery query, bool force)
    {
        _countCts?.Cancel();
        var cts = new CancellationTokenSource();
        _countCts = cts;
        var ct = cts.Token;
        var session = Workspace.Session;
        try
        {
            var hasFilters = query.Filters.Any(TableQueryBuilder.IsApplicable);
            if (!force)
            {
                long? estimate = Info?.EstimatedRows;
                if (IsTableKind || Kind == TabKinds.MaterializedView)
                {
                    estimate = await Task.Run(async () =>
                    {
                        await using var conn = await session.OpenConnectionAsync(ct).ConfigureAwait(false);
                        return await TableDataAccess.EstimateRowsAsync(conn, query.Schema, query.Table, ct).ConfigureAwait(false);
                    }, ct).ConfigureAwait(true) ?? estimate;
                }
                var threshold = AppServices.IsInitialized ? AppServices.Current.Table.EstimateAboveRows : 500_000;
                if (Kind == TabKinds.View || estimate > threshold)
                {
                    TotalRows = null;
                    RowCountClickable = true;
                    RowCountText = !hasFilters && estimate is { } e && Kind != TabKinds.View
                        ? $"≈{e.ToString("N0", CultureInfo.InvariantCulture)} rows"
                        : "Count rows";
                    return;
                }
            }
            RowCountText = "Counting…";
            var spec = TableQueryBuilder.BuildCount(query);
            Workspace.Log(spec.LiteralSql);
            var count = await Task.Run(async () =>
            {
                await using var conn = await session.OpenConnectionAsync(ct).ConfigureAwait(false);
                return await TableDataAccess.CountAsync(conn, spec, 0, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;
            TotalRows = count;
            RowCountClickable = false;
            RowCountText = $"{count.ToString("N0", CultureInfo.InvariantCulture)} rows";
            CanNextPage = Offset + PageSize < count;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RowCountText = "count failed";
            RowCountClickable = true;
            Workspace.Log($"count {Schema}.{Name} failed: {ex.Message}");
        }
    }

    /// <summary>Click on the estimate: exact count(*).</summary>
    [RelayCommand]
    public void CountExact() => UpdateRowCountAsync(BuildQuery(), force: true).Forget();

    // ---- paging and sort -----------------------------------------------------------------------------------------

    [RelayCommand]
    public void NextPage()
    {
        if (!CanNextPage) return;
        Offset += PageSize;
        Load();
    }

    [RelayCommand]
    public void PrevPage()
    {
        if (Offset == 0) return;
        Offset = Math.Max(0, Offset - PageSize);
        Load();
    }

    /// <summary>The Limit/Offset popover's Go.</summary>
    public void GoTo(int limit, long offset)
    {
        PageSize = Math.Clamp(limit, 1, 1_000_000);
        _pageSizeCustomized = true;
        Offset = Math.Max(0, offset);
        Load();
    }

    /// <summary>Header click: DESC → ASC → none (Shift adds to a multi-column sort).</summary>
    public void OnSortRequested(string column, bool addToMulti)
    {
        Sorts = TableQueryBuilder.CycleSort(Sorts, column, addToMulti);
        Offset = 0;
        Load();
    }

    /// <summary>Header menu: set one column's direction (null removes it).</summary>
    public void SetSort(string column, SortDirection? direction, bool addToMulti)
    {
        var list = addToMulti ? Sorts.Where(s => s.Column != column).ToList() : [];
        if (direction is { } d)
        {
            var existing = Sorts.ToList().FindIndex(s => s.Column == column);
            if (addToMulti && existing >= 0) list.Insert(Math.Min(existing, list.Count), new SortSpec(column, d));
            else list.Add(new SortSpec(column, d));
        }
        else if (!addToMulti)
        {
            list = Sorts.Where(s => s.Column != column).ToList();
        }
        Sorts = list;
        Offset = 0;
        Load();
    }

    // ---- editing --------------------------------------------------------------------------------------------------

    public RowState GetRowState(object?[] row) => Pending?.GetRowState(row) switch
    {
        PendingRowState.New => RowState.New,
        PendingRowState.Modified => RowState.Modified,
        PendingRowState.Deleted => RowState.Deleted,
        _ => RowState.Unchanged,
    };

    public bool IsCellModified(object?[] row, int columnIndex) => Pending?.IsCellModified(row, columnIndex) == true;

    /// <summary>A column that can't be written (generated, or unknown to the metadata).</summary>
    public bool IsColumnReadOnly(int columnIndex)
    {
        if (Columns is null || columnIndex < 0 || columnIndex >= Columns.Count) return true;
        var col = Info?.FindColumn(Columns[columnIndex].Name);
        return col is null || col.IsGenerated;
    }

    /// <summary>
    /// Staged changes can be modified: an editable table that is not committing. While a commit runs nothing may be
    /// staged, because a successful commit clears everything that is staged (an edit made meanwhile would be lost).
    /// </summary>
    private bool CanStage => IsEditable && Pending is not null && !IsCommitting;

    public bool CanEditCell(object?[] row, int columnIndex) =>
        CanStage && !Pending!.IsDeleted(row) && !IsColumnReadOnly(columnIndex);

    /// <summary>Grid edit commit: stage the typed text. Returns the value the grid should show.</summary>
    public object? CommitCellEdit(object?[] row, int columnIndex, string text)
    {
        if (!CanEditCell(row, columnIndex)) return row[columnIndex];
        Pending!.SetCell(row, columnIndex, InputFor(columnIndex, text));
        return row[columnIndex];
    }

    /// <summary>Typed text for a column (a zone-less timestamptz is UTC, as displayed; see <see cref="PgInput"/>).</summary>
    private string InputFor(int columnIndex, string text) =>
        Columns is not null && columnIndex < Columns.Count ? PgInput.ForColumn(Columns[columnIndex].DataTypeName, text) : text;

    /// <summary>A pasted field: the text NULL is SQL NULL (what the grid shows and copies for NULL), anything else is typed text.</summary>
    private object? PastedValue(int columnIndex, string field) => field == "NULL" ? null : InputFor(columnIndex, field);

    /// <summary>Set Value menu: applies <paramref name="value"/> to every selected cell (one undo step).</summary>
    public void SetSelectedCells(object? value)
    {
        if (!CanStage || View is null) return;
        var cells = View.Grid.GetSelectedCells();
        if (cells.Count == 0 && View.Grid.CurrentCellIndex is { } cur) cells = [cur];
        using (Pending!.BeginBatch())
        {
            foreach (var (r, c) in cells)
            {
                if (r < 0 || r >= Rows.Count) continue;
                var row = Rows[r];
                if (CanEditCell(row, c)) Pending.SetCell(row, c, value);
            }
        }
    }

    /// <summary>Whether the selection's current column is a date/time column (offers NOW()).</summary>
    public bool CurrentColumnIsTemporal()
    {
        if (View?.Grid.CurrentCellIndex is not { } cur || Columns is null || cur.ColumnIndex >= Columns.Count) return false;
        var t = Columns[cur.ColumnIndex].DataTypeName;
        return t.StartsWith("timestamp", StringComparison.Ordinal) || t.StartsWith("date", StringComparison.Ordinal) || t.StartsWith("time", StringComparison.Ordinal);
    }

    /// <summary>Ctrl+I: a new row with every column DEFAULT.</summary>
    public object?[]? InsertRow()
    {
        if (!CanStage) return null;
        var row = Pending!.InsertRow();
        ScrollTo(row);
        return row;
    }

    /// <summary>Ctrl+D: copies of the selected rows (PK / identity / generated columns left to DEFAULT when they have one).</summary>
    public void DuplicateSelectedRows()
    {
        if (!CanStage || View is null || Columns is null) return;
        DuplicateRows(View.Grid.GetSelectedRows());
    }

    /// <summary>
    /// Stages copies of <paramref name="rows"/>. Values the grid only holds as a lossy preview (a truncated bytea,
    /// an unreadable value) are not copied: those cells stay DEFAULT instead of inserting the preview text.
    /// </summary>
    public void DuplicateRows(IReadOnlyList<object?[]> rows)
    {
        if (!CanStage || Columns is null || rows.Count == 0) return;
        var setColumns = DuplicateColumns();
        object?[]? last = null;
        using (Pending!.BeginBatch())
        {
            foreach (var row in rows)
            {
                var cols = setColumns.Where(c => row[c] is not SqlDefault && !DbValueConverter.IsLossyPreview(row[c])).ToList();
                last = Pending.InsertRow(row, cols);
            }
        }
        if (last is not null) ScrollTo(last);
    }

    private List<int> DuplicateColumns()
    {
        var result = new List<int>();
        var pk = Info?.PrimaryKey ?? [];
        for (var i = 0; i < Columns!.Count; i++)
        {
            var col = Info?.FindColumn(Columns[i].Name);
            if (col is null || col.IsGenerated || col.Identity is not null) continue;
            if (pk.Contains(col.Name) && col.Default is not null) continue;
            result.Add(i);
        }
        return result;
    }

    /// <summary>Delete: stage deletes of the selected rows (restores them when all are already deleted).</summary>
    public void DeleteSelectedRows()
    {
        if (!CanStage || View is null) return;
        var rows = View.Grid.GetSelectedRows();
        if (rows.Count == 0) return;
        if (rows.All(Pending!.IsDeleted)) Pending.RestoreRows(rows);
        else Pending.DeleteRows(rows);
    }

    /// <summary>
    /// Ctrl+V: clipboard TSV as new rows. Fields map to columns from <paramref name="startColumn"/> (default: the current
    /// cell's column), except whole rows (at least one field per column), which map from the first column. Empty fields
    /// stay DEFAULT; the text NULL is SQL NULL.
    /// </summary>
    public int PasteAsNewRows(string? text, int? startColumn = null)
    {
        if (!CanStage || Columns is null) return 0;
        var lines = TsvParser.Parse(text);
        if (lines.Count == 0) return 0;
        var start = startColumn ?? View?.Grid.CurrentCellIndex?.ColumnIndex ?? 0;
        if (start < 0 || start >= Columns.Count || lines.Max(l => l.Length) >= Columns.Count) start = 0;
        object?[]? last = null;
        using (Pending!.BeginBatch())
        {
            foreach (var fields in lines)
            {
                var values = new object?[Columns.Count];
                var set = new List<int>();
                for (var j = 0; j < fields.Length && start + j < Columns.Count; j++)
                {
                    var c = start + j;
                    if (fields[j].Length == 0 || IsColumnReadOnly(c)) continue;
                    values[c] = PastedValue(c, fields[j]);
                    set.Add(c);
                }
                last = Pending.InsertRow(values, set);
            }
        }
        if (last is not null) ScrollTo(last);
        return lines.Count;
    }

    /// <summary>Ctrl+Shift+V: clipboard TSV into the cells from the current cell (one value fills every selected cell).</summary>
    public int PasteIntoCells(string? text)
    {
        if (!CanStage || View is null || Columns is null) return 0;
        var selected = View.Grid.GetSelectedCells();
        var start = View.Grid.CurrentCellIndex ?? (selected.Count > 0 ? selected.MinBy(s => (s.RowIndex, s.ColumnIndex)) : (0, 0));
        return PasteIntoCells(text, start, selected);
    }

    /// <summary>Pastes TSV into cells from <paramref name="start"/>; a single value fills every cell of <paramref name="selected"/>.</summary>
    public int PasteIntoCells(string? text, (int RowIndex, int ColumnIndex) start, IReadOnlyList<(int RowIndex, int ColumnIndex)> selected)
    {
        if (!CanStage || Columns is null) return 0;
        var lines = TsvParser.Parse(text);
        if (lines.Count == 0) return 0;
        var changed = 0;
        using (Pending!.BeginBatch())
        {
            if (lines.Count == 1 && lines[0].Length == 1 && selected.Count > 1)
            {
                foreach (var (r, c) in selected)
                    if (r < Rows.Count && CanEditCell(Rows[r], c) && Pending.SetCell(Rows[r], c, PastedValue(c, lines[0][0]))) changed++;
                return changed;
            }
            for (var i = 0; i < lines.Count; i++)
            {
                var r = start.RowIndex + i;
                if (r < 0 || r >= Rows.Count) break;
                for (var j = 0; j < lines[i].Length; j++)
                {
                    var c = start.ColumnIndex + j;
                    if (c < 0 || c >= Columns.Count) break;
                    if (CanEditCell(Rows[r], c) && Pending.SetCell(Rows[r], c, PastedValue(c, lines[i][j]))) changed++;
                }
            }
        }
        return changed;
    }

    private void ScrollTo(object?[] row)
    {
        var index = Rows.IndexOf(row);
        if (index >= 0) View?.ScrollToRow(index);
    }

    public bool Undo() => !IsCommitting && Pending?.Undo() == true;

    public bool Redo() => !IsCommitting && Pending?.Redo() == true;

    private void OnPendingChanged(object? sender, EventArgs e)
    {
        HasPendingChanges = Pending?.IsDirty == true;
        SyncInsertedRows();
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        View?.RefreshRowVisuals();
        _preview?.Refresh();
    }

    /// <summary>Keeps <see cref="Rows"/> = loaded page rows + staged inserted rows (incrementally, keeping scroll).</summary>
    private void SyncInsertedRows()
    {
        var inserted = Pending?.InsertedRows ?? [];
        var desired = new HashSet<object?[]>(_pageRows.Concat(inserted), ReferenceEqualityComparer.Instance);
        for (var i = Rows.Count - 1; i >= 0; i--)
            if (!desired.Contains(Rows[i])) Rows.RemoveAt(i);
        var present = new HashSet<object?[]>(Rows, ReferenceEqualityComparer.Instance);
        foreach (var row in inserted)
            if (!present.Contains(row)) Rows.Add(row);
    }

    // ---- commit / preview / discard -------------------------------------------------------------------------------

    public ChangeTarget? ChangeTarget => Pending is null || Columns is null
        ? null
        : new ChangeTarget(Schema!, Name!, [.. Columns.Select(c => c.Name)], Pending.KeyColumns);

    /// <summary>The statements Commit would send.</summary>
    public IReadOnlyList<SqlCommandSpec> GenerateStatements() =>
        ChangeTarget is { } t && Pending is not null ? ChangeSqlGenerator.Generate(t, Pending) : [];

    public override Task CommitAsync() => CommitCoreAsync();

    /// <summary>Sends every staged statement in one transaction (through Safe Mode). True when committed.</summary>
    public async Task<bool> CommitCoreAsync(bool showErrors = true)
    {
        if (IsCommitting || Pending is not { IsDirty: true } pending) return false;
        // Staging is locked from here until the outcome is known: the statements confirmed are exactly the ones sent, and
        // nothing staged meanwhile can be wiped by the Clear() that follows a successful commit.
        IsCommitting = true;
        try
        {
            var statements = GenerateStatements();
            if (statements.Count == 0) return false;
            if (!await Workspace.SafeMode.ConfirmAsync([.. statements.Select(s => s.LiteralSql)]).ConfigureAwait(true)) return false;
            IsBusy = true;
            StatusText = "Committing…";
            var session = Workspace.Session;
            var timeout = AppServices.IsInitialized ? AppServices.Current.Connections.CommandTimeoutSeconds : 0;
            foreach (var s in statements) Workspace.Log(s.LiteralSql);
            CommitResult result;
            try
            {
                result = await Task.Run(async () =>
                {
                    await using var conn = await session.OpenConnectionAsync().ConfigureAwait(false);
                    return await TableDataAccess.CommitAsync(conn, statements, timeout).ConfigureAwait(false);
                }).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = new CommitResult { Success = false, FailedIndex = 0, Error = ex.Message };
            }
            AppendHistory(statements, result);
            if (!result.Success)
            {
                LastCommitError = result.Error;
                LastFailedStatement = result.FailedIndex;
                StatusText = "Commit failed; changes are still staged.";
                Workspace.Log($"commit failed (rolled back) at statement {result.FailedIndex + 1}: {result.Error}");
                _preview?.Refresh();
                if (showErrors)
                {
                    var failing = result.FailedIndex >= 0 && result.FailedIndex < statements.Count ? statements[result.FailedIndex].LiteralSql : null;
                    Dialogs.Error($"Commit failed at statement {result.FailedIndex + 1} of {statements.Count}. Nothing was committed; your changes are still staged.\n\n{result.Error}",
                        "Commit failed", detail: failing);
                }
                return false;
            }
            LastCommitError = null;
            LastFailedStatement = -1;
            Workspace.Log($"committed {statements.Count} statement(s) in {result.Elapsed.TotalMilliseconds:0} ms");
            pending.Clear();
            IsCommitting = false;
            _preview?.Close();
            await LoadAsync(recount: true).ConfigureAwait(true);
            return true;
        }
        finally
        {
            IsCommitting = false;
            IsBusy = false;
        }
    }

    private void AppendHistory(IReadOnlyList<SqlCommandSpec> statements, CommitResult result)
    {
        if (!AppServices.IsInitialized) return;
        for (var i = 0; i < statements.Count; i++)
        {
            if (!result.Success && i > result.FailedIndex) break;
            Workspace.History.Append(Workspace.Profile.Id, new HistoryEntry
            {
                Database = Workspace.Database,
                Sql = statements[i].LiteralSql,
                DurationMs = i == 0 ? result.Elapsed.TotalMilliseconds : 0,
                Rows = i < result.Affected.Count ? result.Affected[i] : null,
                Error = !result.Success && i == result.FailedIndex,
                Source = "commit",
            });
        }
    }

    public override void PreviewChanges()
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

    public override void DiscardChanges()
    {
        if (Pending is null || IsCommitting) return;
        Pending.Clear();
        LastCommitError = null;
        LastFailedStatement = -1;
        _preview?.Close();
        Load(recount: true);
    }

    // ---- bottom tabs ----------------------------------------------------------------------------------------------

    private FrameworkElement GetSubView(TableSubView view)
    {
        if (_subViews.TryGetValue(view, out var existing)) return existing;
        var kind = view switch
        {
            TableSubView.Structure => TableSubViewRegistry.Structure,
            TableSubView.Indexes => TableSubViewRegistry.Indexes,
            _ => TableSubViewRegistry.Ddl,
        };
        FrameworkElement element;
        try
        {
            element = TableSubViewRegistry.Create(kind, Workspace, Schema!, Name!) ?? Placeholder($"{view} view is not available yet.");
        }
        catch (Exception ex)
        {
            element = Placeholder($"{view} view failed: {ex.Message}");
        }
        _subViews[view] = element;
        return element;
    }

    private static FrameworkElement Placeholder(string text)
    {
        var tb = new System.Windows.Controls.TextBlock
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        tb.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "c.app.mutedForeground");
        return tb;
    }

    [RelayCommand]
    private void ShowSubView(string view) => SelectedSubView = Enum.Parse<TableSubView>(view);

    // ---- commands -------------------------------------------------------------------------------------------------

    public bool TryExecuteCommand(string commandId, CommandContext context)
    {
        switch (commandId)
        {
            case CommandIds.TableShowData:
                SelectedSubView = TableSubView.Data;
                View?.Grid.FocusGrid();
                return true;
            case CommandIds.TableShowStructure:
                SelectedSubView = TableSubView.Structure;
                return true;
            case CommandIds.TableShowIndexes:
                SelectedSubView = IsViewKind ? TableSubView.Ddl : TableSubView.Indexes;
                return true;
            case CommandIds.EditUndo:
                return !context.IsTextInputFocused && Undo();
            case CommandIds.EditRedo:
                return !context.IsTextInputFocused && Redo();
            case CommandIds.TableFilterToggle:
                Filter.IsVisible = !Filter.IsVisible;
                if (Filter.IsVisible) View?.FocusFilterBar();
                else View?.Grid.FocusGrid();
                return true;
            case CommandIds.FilterAddRow:
                Filter.AddRow();
                View?.FocusFilterBar();
                return true;
            case CommandIds.FilterRemoveRow:
                Filter.RemoveRow();
                View?.FocusFilterBar();
                return true;
            case CommandIds.FilterToggleRow:
                Filter.ToggleRow();
                return true;
            case CommandIds.FilterApplyAll:
                Filter.ApplyAll();
                return true;
        }

        if (!IsDataView) return false;
        switch (commandId)
        {
            case CommandIds.PageNext:
                NextPage();
                return true;
            case CommandIds.PagePrev:
                PrevPage();
                return true;
            case CommandIds.GridFirstRow:
                if (Rows.Count > 0) View?.ScrollToRow(0, View.Grid.CurrentCellIndex?.ColumnIndex ?? 0);
                return true;
            case CommandIds.GridLastRow:
                if (Rows.Count > 0) View?.ScrollToRow(Rows.Count - 1, View.Grid.CurrentCellIndex?.ColumnIndex ?? 0);
                return true;
            case CommandIds.GridEditCell:
                View?.Grid.BeginEditCurrentCell();
                return IsEditable;
            case CommandIds.GridInsertRow:
                return InsertRow() is not null;
            case CommandIds.GridDuplicateRow:
                DuplicateSelectedRows();
                return IsEditable;
            case CommandIds.GridDeleteRow:
                DeleteSelectedRows();
                return IsEditable;
            case CommandIds.GridSetValueMenu:
                if (!IsEditable) return false;
                View?.ShowSetValueMenu();
                return true;
            case CommandIds.GridPaste:
                if (!IsEditable) return false;
                PasteAsNewRows(ClipboardText());
                return true;
            case CommandIds.GridPasteCells:
                if (!IsEditable) return false;
                PasteIntoCells(ClipboardText());
                return true;
        }
        return false;
    }

    private static string? ClipboardText()
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
