using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using TablePlsu.App.Features.Results;
using TablePlsu.App.Features.Workspace;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Commands;
using TablePlsu.Core.Db;
using TablePlsu.Core.History;
using TablePlsu.Core.Metadata;
using TablePlsu.Core.Settings;

namespace TablePlsu.App.Features.Sidebar;

/// <summary>A schema in the schema selector.</summary>
public sealed partial class SchemaChoice(string name, bool isChecked, Action changed) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    public partial bool IsChecked { get; set; } = isChecked;

    partial void OnIsCheckedChanged(bool value) => changed();
}

/// <summary>One row of the History tab.</summary>
public sealed class HistoryRow(HistoryEntry entry)
{
    public HistoryEntry Entry { get; } = entry;
    public string Sql => Entry.Sql;
    public string FirstLine { get; } = FirstLineOf(entry.Sql);
    public string Meta { get; } = string.Create(CultureInfo.InvariantCulture,
        $"{entry.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {entry.Database} · {entry.DurationMs:0} ms{(entry.Rows is { } r ? $" · {r} rows" : "")}");
    public bool IsError => Entry.Error;
    public string ToolTip => Entry.Sql.Length > 2000 ? Entry.Sql[..2000] + "…" : Entry.Sql;

    private static string FirstLineOf(string sql)
    {
        var s = sql.TrimStart();
        var nl = s.IndexOfAny(['\r', '\n']);
        var line = nl < 0 ? s : s[..nl] + " …";
        return line.Length > 200 ? line[..200] + "…" : line;
    }
}

/// <summary>
/// The left sidebar of one workspace: Items tree (flattened, built by <see cref="SidebarTree"/> from
/// <see cref="IWorkspace.Metadata"/>), schema selector, search, and the History tab. Lives in <c>ws.Services</c>.
/// UI-thread only; metadata events are marshalled with <see cref="UiDispatch"/>.
/// </summary>
public sealed partial class SidebarViewModel : ObservableObject, ICommandTarget, IDisposable
{
    public const int ItemsTab = 0;
    public const int HistoryTab = 1;

    private readonly SidebarSchemaStore? _store;
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loading = new(StringComparer.Ordinal);
    private HashSet<string>? _visibleSchemas;
    private bool _expansionInitialized;
    private bool _updatingChoices;
    private bool _disposed;
    private List<HistoryRow> _historyAll = [];
    private int _historyVersion;

    // Search: the flat candidate index of the current metadata snapshot, the pending (debounced, off-thread) search and
    // the selection to bring back when the query is cleared.
    private SidebarIndex? _index;
    private CancellationTokenSource? _searchCts;
    private int _searchVersion;
    private string _appliedFilter = "";
    private IReadOnlyList<string> _preSearchSelection = [];
    private CancellationTokenSource? _historyCts;
    private int _historyFilterVersion;
    private int _debounceMs = 120;

    /// <summary>
    /// Below this many candidates (objects in the index, rows in History) a search filters synchronously on the
    /// keystroke: it costs well under a millisecond, and small databases see no delay at all.
    /// </summary>
    public const int ImmediateSearchBelow = 1000;

    public SidebarViewModel(IWorkspace workspace, SidebarSchemaStore? store = null)
    {
        Workspace = workspace;
        _store = store;
        if (_store?.Load(workspace.Profile.Id, workspace.Database) is { } saved) _visibleSchemas = new HashSet<string>(saved, StringComparer.Ordinal);
        ApplySettings(AppServices.IsInitialized ? AppServices.Current : new AppSettings());
        workspace.Metadata.Changed += OnMetadataChanged;
        workspace.StateChanged += OnStateChanged;
        if (AppServices.IsInitialized) AppServices.SettingsChanged += OnSettingsChanged;
        Rebuild();
    }

    public IWorkspace Workspace { get; }

    /// <summary>The flattened Items tree.</summary>
    public BulkObservableCollection<SidebarNode> Nodes { get; } = new();

    public ObservableCollection<SchemaChoice> SchemaChoices { get; } = [];

    public BulkObservableCollection<HistoryRow> HistoryItems { get; } = new();

    /// <summary>Selected tree rows (pushed by the view; the ListBox owns selection).</summary>
    public IReadOnlyList<SidebarNode> SelectedNodes { get; set; } = [];

    /// <summary>Selected history rows (pushed by the view).</summary>
    public IReadOnlyList<HistoryRow> SelectedHistory { get; set; } = [];

    /// <summary>Raised after <see cref="Nodes"/> was rebuilt, with the keys that were selected before.</summary>
    public event Action<IReadOnlyList<string>>? NodesRebuilt;

    /// <summary>Ctrl+Shift+F: the view focuses the active tab's search box.</summary>
    public event Action? FocusSearchRequested;

    /// <summary>The view scrolls to and selects the node with this key.</summary>
    public event Action<string>? RevealRequested;

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    partial void OnSearchTextChanged(string value)
    {
        Refresh(debounce: true);
        // Lazy schemas: searching needs the contents of every visible schema, so load the ones not loaded yet.
        if (value.Trim().Length > 0 && Workspace.State == DbSessionState.Connected)
        {
            var md = Workspace.Metadata.Current;
            var visible = EffectiveVisible(md);
            foreach (var s in md.Schemas)
                if (!s.IsLoaded && (visible is null || visible.Contains(s.Name))) LoadSchema(s.Name);
        }
    }

    [ObservableProperty]
    public partial string HistorySearchText { get; set; } = "";

    partial void OnHistorySearchTextChanged(string value) => RefilterHistory();

    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; }

    partial void OnSelectedTabIndexChanged(int value)
    {
        if (value == HistoryTab) LoadHistoryAsync().Forget();
    }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "";

    [ObservableProperty]
    public partial string SchemaSelectorText { get; private set; } = "All schemas";

    [ObservableProperty]
    public partial FontFamily SidebarFontFamily { get; private set; } = new("Segoe UI");

    [ObservableProperty]
    public partial double SidebarFontSize { get; private set; } = 12;

    [ObservableProperty]
    public partial Thickness RowPadding { get; private set; } = new(4, 2, 4, 2);

    [ObservableProperty]
    public partial SidebarMatching Matching { get; private set; }

    public IReadOnlySet<string> ExpandedKeys => _expanded;

    public IReadOnlySet<string>? VisibleSchemas => _visibleSchemas;

    /// <summary>A debounced/background search has not been applied yet.</summary>
    public bool IsSearchPending => _searchCts is not null || _historyCts is not null;

    /// <summary>True during the <see cref="NodesRebuilt"/> that follows clearing the search query.</summary>
    public bool QueryJustCleared { get; private set; }

    /// <summary>Times <see cref="Nodes"/> was replaced (diagnostics / perf tests).</summary>
    public int NodesReplacedCount { get; private set; }

    // ---- tree -------------------------------------------------------------------------------------------------------

    /// <summary>Rebuilds <see cref="Nodes"/> from the current metadata snapshot, keeping expansion and selection (synchronous).</summary>
    public void Rebuild() => Refresh(debounce: false, synchronous: true);

    /// <summary>
    /// Re-filters. Unfiltered views and small indexes are built right here; a query over a large index runs off the UI
    /// thread (after the <c>sidebar.searchDebounceMs</c> typing pause when <paramref name="debounce"/>), cancelling the
    /// previous one, and only the newest result is applied.
    /// </summary>
    private void Refresh(bool debounce, bool synchronous = false)
    {
        if (_disposed) return;
        CancelSearch();
        var md = Workspace.Metadata.Current;
        if (!_expansionInitialized && !md.IsEmpty)
        {
            _expansionInitialized = true;
            foreach (var s in md.EffectiveSearchPath)
            {
                if (md.FindSchema(s) is null) continue;
                _expanded.Add(s);
                _expanded.Add(SidebarTree.SectionKey(s, SidebarSection.Tables));
            }
            if (_expanded.Count == 0 && md.Schemas.Count > 0) _expanded.Add(md.Schemas[0].Name);
        }
        UpdateSchemaChoices(md);

        var index = _index = SidebarIndex.Create(md, _index);
        var filter = SearchText.Trim();
        var visible = EffectiveVisible(md);
        var options = new SidebarTreeOptions
        {
            Filter = filter,
            Matching = Matching,
            VisibleSchemas = visible,
            Expanded = _expanded,
            NotLoadedText = Workspace.State == DbSessionState.Connected ? "Loading…" : "Not loaded (connect to load)",
        };
        if (synchronous || filter.Length == 0 || index.CandidateCount < ImmediateSearchBelow)
        {
            Apply(md, SidebarTree.Build(index, options), filter);
            return;
        }

        // Background: snapshot the mutable inputs (filtering expands everything, so the expansion set is not read).
        options = options with { VisibleSchemas = visible is null ? null : new HashSet<string>(visible, StringComparer.Ordinal), Expanded = new HashSet<string>() };
        var cts = _searchCts = new CancellationTokenSource();
        var version = _searchVersion;
        var delay = debounce ? _debounceMs : 0;
        _ = Task.Run(async () =>
        {
            try
            {
                if (delay > 0) await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                var nodes = SidebarTree.Build(index, options, cts.Token);
                UiDispatch.Post(() =>
                {
                    if (version != _searchVersion || _disposed) return;
                    _searchCts = null;
                    cts.Dispose();
                    Apply(md, nodes, filter);
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        });
    }

    private void CancelSearch()
    {
        _searchVersion++;
        if (_searchCts is not { } cts) return;
        _searchCts = null;
        cts.Cancel();
    }

    private void Apply(DbMetadata md, List<SidebarNode> nodes, string filter)
    {
        var selected = SelectedNodes.Select(n => n.Key).ToList();
        var wasFiltering = _appliedFilter.Length > 0;
        QueryJustCleared = wasFiltering && filter.Length == 0;
        if (!wasFiltering && filter.Length > 0) _preSearchSelection = selected;
        else if (QueryJustCleared)
        {
            // Clearing the query brings back what was selected before searching (unless a result was picked meanwhile).
            if (selected.Count == 0) selected = [.. _preSearchSelection];
            _preSearchSelection = [];
        }
        _appliedFilter = filter;

        if (!SameRows(Nodes, nodes))
        {
            Nodes.ReplaceAll(nodes);
            NodesReplacedCount++;
        }
        StatusText = md.IsEmpty
            ? Workspace.State switch
            {
                DbSessionState.Connecting => "Loading schema…",
                DbSessionState.Connected => Workspace.Metadata.IsRefreshing ? "Loading schema…" : "No schemas",
                _ => "Not connected",
            }
            : nodes.Count == 0 && filter.Length > 0 ? "No matches" : "";
        NodesRebuilt?.Invoke(selected);
        QueryJustCleared = false;
        if (Workspace.State == DbSessionState.Connected) LoadExpandedSchemas(md);
    }

    /// <summary>Same rows in the same state (item nodes are shared by the index, header rows are rebuilt each time).</summary>
    private static bool SameRows(IList<SidebarNode> a, List<SidebarNode> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < b.Count; i++)
        {
            SidebarNode x = a[i], y = b[i];
            if (ReferenceEquals(x, y)) continue;
            if (x.IsItem || y.IsItem || x.Kind != y.Kind || x.Key != y.Key || x.Text != y.Text || x.Detail != y.Detail || x.IsExpanded != y.IsExpanded
                || x.ToolTip != y.ToolTip) return false;
        }
        return true;
    }

    private HashSet<string>? EffectiveVisible(DbMetadata md)
    {
        if (_visibleSchemas is null) return null;
        // Ignore a saved selection that no longer matches any schema (show all instead of an empty tree).
        return md.Schemas.Any(s => _visibleSchemas.Contains(s.Name)) ? _visibleSchemas : null;
    }

    private void LoadExpandedSchemas(DbMetadata md)
    {
        var visible = EffectiveVisible(md);
        foreach (var s in md.Schemas)
        {
            if (s.IsLoaded || !_expanded.Contains(s.Name) || (visible is not null && !visible.Contains(s.Name))) continue;
            LoadSchema(s.Name);
        }
    }

    private void LoadSchema(string schema)
    {
        if (!_loading.Add(schema)) return;
        Task<SchemaInfo?> task;
        try { task = Workspace.Metadata.EnsureSchemaLoadedAsync(schema); }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            _loading.Remove(schema);
            return;
        }
        task.ContinueWith(t =>
        {
            UiDispatch.Run(() => _loading.Remove(schema));
            if (t.Exception is { } ex) Workspace.Log($"Loading schema {schema} failed: {ex.GetBaseException().Message}");
        }, TaskScheduler.Default);
    }

    /// <summary>Expands or collapses a schema/section row (lazy-loading the schema on first expand).</summary>
    public void Toggle(SidebarNode node)
    {
        if (!node.IsExpandable) return;
        if (SearchText.Trim().Length > 0) return; // everything with matches is expanded while filtering
        if (!_expanded.Remove(node.Key)) _expanded.Add(node.Key);
        Rebuild();
    }

    public void SetExpanded(string key, bool expanded)
    {
        if (expanded ? _expanded.Add(key) : _expanded.Remove(key)) Rebuild();
    }

    /// <summary>Single click (preview), double click (pinned) or Enter/Space on an item row.</summary>
    public void Activate(SidebarNode node, bool pinned = false, bool newTab = false)
    {
        if (node.IsHeader)
        {
            Toggle(node);
            return;
        }
        if (node.ToRef() is { } r) SidebarActions.Open(Workspace, r, pinned, newTab);
    }

    public IReadOnlyList<SidebarItemRef> SelectedRefs() => [.. SelectedNodes.Where(n => n.IsItem).Select(n => n.ToRef()).OfType<SidebarItemRef>()];

    /// <summary>Makes <paramref name="schema"/> visible and expanded and asks the view to reveal it (palette → schema).</summary>
    public void RevealSchema(string schema)
    {
        SelectedTabIndex = ItemsTab;
        if (_visibleSchemas is not null && !_visibleSchemas.Contains(schema))
        {
            _visibleSchemas.Add(schema);
            SaveSchemas();
        }
        SearchText = "";
        _expanded.Add(schema);
        _expanded.Add(SidebarTree.SectionKey(schema, SidebarSection.Tables));
        Rebuild();
        RevealRequested?.Invoke(schema);
    }

    public void RequestFocusSearch() => FocusSearchRequested?.Invoke();

    public void NewTable() => SidebarActions.NewTable(Workspace, SelectedNodes.FirstOrDefault()?.Schema);

    // ---- schema selector --------------------------------------------------------------------------------------------

    private void UpdateSchemaChoices(DbMetadata md)
    {
        var names = md.Schemas.Select(s => s.Name).ToList();
        var visible = EffectiveVisible(md);
        _updatingChoices = true;
        try
        {
            if (!names.SequenceEqual(SchemaChoices.Select(c => c.Name)))
            {
                SchemaChoices.Clear();
                foreach (var n in names) SchemaChoices.Add(new SchemaChoice(n, visible?.Contains(n) ?? true, OnSchemaChoiceChanged));
            }
            else
            {
                foreach (var c in SchemaChoices) c.IsChecked = visible?.Contains(c.Name) ?? true;
            }
        }
        finally
        {
            _updatingChoices = false;
        }
        var count = SchemaChoices.Count(c => c.IsChecked);
        SchemaSelectorText = visible is null || count == SchemaChoices.Count
            ? (SchemaChoices.Count == 1 ? SchemaChoices[0].Name : "All schemas")
            : count == 0 ? "No schemas" : string.Join(", ", SchemaChoices.Where(c => c.IsChecked).Select(c => c.Name));
    }

    private void OnSchemaChoiceChanged()
    {
        if (_updatingChoices) return;
        var chosen = SchemaChoices.Where(c => c.IsChecked).Select(c => c.Name).ToList();
        _visibleSchemas = chosen.Count == SchemaChoices.Count ? null : new HashSet<string>(chosen, StringComparer.Ordinal);
        SaveSchemas();
        Rebuild();
    }

    /// <summary>Sets the visible schemas (null = all) and remembers them for this connection + database.</summary>
    public void SetVisibleSchemas(IEnumerable<string>? schemas)
    {
        _visibleSchemas = schemas is null ? null : new HashSet<string>(schemas, StringComparer.Ordinal);
        SaveSchemas();
        Rebuild();
    }

    public void ShowAllSchemas() => SetVisibleSchemas(null);

    private void SaveSchemas() => _store?.Save(Workspace.Profile.Id, Workspace.Database, _visibleSchemas?.Order(StringComparer.Ordinal));

    // ---- history ----------------------------------------------------------------------------------------------------

    /// <summary>Reloads the history file off the UI thread, then filters.</summary>
    public async Task LoadHistoryAsync()
    {
        var version = ++_historyVersion;
        var store = Workspace.History;
        var id = Workspace.Profile.Id;
        var rows = await Task.Run(() => store.Load(id).Select(e => new HistoryRow(e)).ToList()).ConfigureAwait(true);
        if (version != _historyVersion || _disposed) return;
        _historyAll = rows;
        FilterHistory();
    }

    private void FilterHistory() => RefilterHistory(synchronous: true);

    /// <summary>Like the Items search: small histories filter on the keystroke, large ones debounced off the UI thread.</summary>
    private void RefilterHistory(bool synchronous = false)
    {
        var version = ++_historyFilterVersion;
        if (_historyCts is { } old)
        {
            _historyCts = null;
            old.Cancel();
        }
        var terms = HistorySearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var all = _historyAll;
        if (synchronous || terms.Length == 0 || all.Count < ImmediateSearchBelow)
        {
            ApplyHistory(terms.Length == 0 ? all : FilterHistoryRows(all, terms, CancellationToken.None));
            return;
        }
        var cts = _historyCts = new CancellationTokenSource();
        var delay = _debounceMs;
        _ = Task.Run(async () =>
        {
            try
            {
                if (delay > 0) await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                var rows = FilterHistoryRows(all, terms, cts.Token);
                UiDispatch.Post(() =>
                {
                    if (version != _historyFilterVersion || _disposed) return;
                    _historyCts = null;
                    cts.Dispose();
                    ApplyHistory(rows);
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        });
    }

    private static List<HistoryRow> FilterHistoryRows(List<HistoryRow> all, string[] terms, CancellationToken ct)
    {
        var result = new List<HistoryRow>();
        for (var i = 0; i < all.Count; i++)
        {
            if ((i & 511) == 0) ct.ThrowIfCancellationRequested();
            var r = all[i];
            if (terms.All(t => r.Sql.Contains(t, StringComparison.OrdinalIgnoreCase) || r.Entry.Database.Contains(t, StringComparison.OrdinalIgnoreCase))) result.Add(r);
        }
        return result;
    }

    private void ApplyHistory(List<HistoryRow> rows)
    {
        if (rows.Count == HistoryItems.Count)
        {
            var same = true;
            for (var i = 0; i < rows.Count && same; i++) same = ReferenceEquals(rows[i], HistoryItems[i]);
            if (same) return;
        }
        HistoryItems.ReplaceAll(rows);
    }

    /// <summary>Double-click: the SQL in a new editor tab.</summary>
    public TabViewModelBase? OpenHistory(HistoryRow row)
    {
        var tab = Workspace.OpenTab(TabRequest.SqlEditor(row.Sql));
        if (tab is null) Workspace.Log("The SQL editor is not available.");
        return tab;
    }

    /// <summary>Run: opens the SQL in a new editor and runs it (query.runAll on that tab).</summary>
    public void RunHistory(HistoryRow row)
    {
        if (OpenHistory(row) is not { } tab) return;
        UiDispatch.PostBackground(() =>
        {
            var ctx = new CommandContext
            {
                CommandId = CommandIds.QueryRunAll,
                Scope = CommandScope.Editor,
                Workspace = Workspace,
                Target = tab,
                Window = (Workspace as WorkspaceViewModel)?.Window,
            };
            if (tab is ICommandTarget t && t.TryExecuteCommand(CommandIds.QueryRunAll, ctx)) return;
            if (AppServices.IsInitialized && AppServices.Commands.TryExecute(CommandIds.QueryRunAll, ctx)) return;
            Workspace.Log("Run from history: the editor did not accept query.runAll.");
        });
    }

    public void CopyHistory(IReadOnlyList<HistoryRow> rows)
    {
        if (rows.Count == 0) return;
        SidebarActions.SetClipboard(string.Join(";\n\n", rows.Select(r => r.Sql.TrimEnd().TrimEnd(';'))) + (rows.Count > 1 ? ";" : ""));
    }

    public async Task DeleteHistoryAsync(IReadOnlyList<HistoryRow> rows)
    {
        if (rows.Count == 0) return;
        var store = Workspace.History;
        var id = Workspace.Profile.Id;
        var ids = rows.Select(r => r.Entry.Id).ToList();
        await Task.Run(() => { foreach (var e in ids) store.Delete(id, e); }).ConfigureAwait(true);
        await LoadHistoryAsync().ConfigureAwait(true);
    }

    public async Task ClearHistoryAsync()
    {
        var store = Workspace.History;
        var id = Workspace.Profile.Id;
        await Task.Run(() => store.Clear(id)).ConfigureAwait(true);
        await LoadHistoryAsync().ConfigureAwait(true);
    }

    // ---- commands ---------------------------------------------------------------------------------------------------

    public bool TryExecuteCommand(string commandId, CommandContext context)
    {
        switch (commandId)
        {
            case CommandIds.SidebarOpenItem:
                if (SelectedTabIndex == HistoryTab)
                {
                    if (SelectedHistory.FirstOrDefault() is { } h) OpenHistory(h);
                    return true;
                }
                if (SelectedNodes.FirstOrDefault() is { } n) Activate(n);
                return true;
            case CommandIds.SidebarDropItem:
                if (SelectedTabIndex == HistoryTab)
                {
                    DeleteHistoryAsync(SelectedHistory).Forget();
                    return true;
                }
                if (SelectedRefs() is { Count: > 0 } drop) SidebarActions.RunDestructiveAsync(Workspace, DestructiveAction.Drop, drop).Forget();
                return true;
            case CommandIds.SidebarTruncateItem:
                if (SelectedTabIndex == ItemsTab && SelectedRefs() is { Count: > 0 } trunc)
                    SidebarActions.RunDestructiveAsync(Workspace, DestructiveAction.Truncate, trunc).Forget();
                return true;
            case CommandIds.SidebarSearch:
                RequestFocusSearch();
                return true;
            default:
                return false;
        }
    }

    // ---- events -----------------------------------------------------------------------------------------------------

    private void OnMetadataChanged(object? sender, MetadataChangedEventArgs e) =>
        UiDispatch.Run(() => Refresh(debounce: false), System.Windows.Threading.DispatcherPriority.Background);

    private void OnStateChanged(object? sender, DbSessionState state) => Rebuild();

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        var oldMatching = Matching;
        ApplySettings(e.Settings);
        if (oldMatching != Matching && SearchText.Length > 0) Rebuild();
    }

    private void ApplySettings(AppSettings s)
    {
        var sb = s.Sidebar;
        try { SidebarFontFamily = new FontFamily(string.IsNullOrWhiteSpace(sb.FontFamily) ? "Segoe UI" : sb.FontFamily); }
        catch (ArgumentException) { SidebarFontFamily = new FontFamily("Segoe UI"); }
        SidebarFontSize = sb.FontSize;
        RowPadding = new Thickness(4, sb.RowPadding, 6, sb.RowPadding);
        Matching = sb.Matching;
        _debounceMs = sb.SearchDebounceMs;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelSearch();
        _historyCts?.Cancel();
        Workspace.Metadata.Changed -= OnMetadataChanged;
        Workspace.StateChanged -= OnStateChanged;
        AppServices.SettingsChanged -= OnSettingsChanged;
    }
}
