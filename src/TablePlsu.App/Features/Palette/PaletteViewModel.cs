using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using TablePlsu.App.Features.Results;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Db;
using TablePlsu.Core.Metadata;

namespace TablePlsu.App.Features.Palette;

/// <summary>A displayed palette row.</summary>
public sealed class PaletteRow(PaletteHit hit)
{
    public PaletteHit Hit { get; } = hit;
    public PaletteItem Item => Hit.Item;
    public string Title => Item.Title;
    public string? Detail => Item.Detail ?? PaletteKinds.Label(Item.Kind);
    public string? Shortcut => string.IsNullOrEmpty(Item.Shortcut) ? null : Item.Shortcut;
    public string Icon => PaletteKinds.Icon(Item.Kind);
}

/// <summary>
/// Open Anything / Open Database / Open Connection. The candidate list (<see cref="PaletteIndex"/>) is gathered once from
/// the <see cref="PaletteRegistry"/> providers when the palette opens and again when their data changes (metadata
/// loaded, database list fetched); each keystroke only re-ranks. Keyboard: Enter / Ctrl+Enter (new tab) /
/// Shift+Enter (new window), Up/Down and Ctrl+N/Ctrl+P, Tab completes, Esc clears then closes.
/// </summary>
public sealed partial class PaletteViewModel : ObservableObject, IDisposable
{
    public const int MaxRows = 60;

    private readonly Func<PaletteQuery, IEnumerable<PaletteItem>> _gather;
    private PaletteIndex _index = new([]);
    private bool _reloadPosted;
    private bool _disposed;

    public PaletteViewModel(PaletteMode mode, IWorkspace? workspace, Window? window, Func<PaletteQuery, IEnumerable<PaletteItem>>? gather = null)
    {
        Mode = mode;
        Workspace = workspace;
        Query = new PaletteQuery(mode, workspace, window);
        _gather = gather ?? (q => AppServices.Features.Palette.GetItems(q));
        Placeholder = mode switch
        {
            PaletteMode.Databases => "Open database…",
            PaletteMode.Connections => "Open connection…",
            _ => "Open anything: tables, functions, schemas, databases, commands…  (^prefix, suffix$)",
        };
        Reload();
        if (workspace is not null)
        {
            workspace.Metadata.Changed += OnMetadataChanged;
            StartBackgroundLoads();
        }
    }

    public PaletteMode Mode { get; }

    public IWorkspace? Workspace { get; }

    public PaletteQuery Query { get; }

    public string Placeholder { get; }

    /// <summary>The ranked rows; replaced with one Reset per keystroke (not a Clear plus one Add per row).</summary>
    public BulkObservableCollection<PaletteRow> Rows { get; } = new();

    public int CandidateCount => _index.Count;

    /// <summary>Time spent ranking for the last keystroke (diagnostics; the target is &lt; 16 ms).</summary>
    public TimeSpan LastSearchTime { get; private set; }

    [ObservableProperty]
    public partial string Text { get; set; } = "";

    partial void OnTextChanged(string value) => Refilter();

    [ObservableProperty]
    public partial int SelectedIndex { get; set; } = -1;

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "";

    /// <summary>Raised when the palette should close (after invoking, or on the second Esc).</summary>
    public event Action? CloseRequested;

    public PaletteRow? Selected => SelectedIndex >= 0 && SelectedIndex < Rows.Count ? Rows[SelectedIndex] : null;

    /// <summary>Re-gathers candidates from the providers (keeps the text and, when possible, the selection).</summary>
    public void Reload()
    {
        _reloadPosted = false;
        if (_disposed) return;
        _index = new PaletteIndex(_gather(Query));
        Refilter();
    }

    private void Refilter()
    {
        var keep = Selected?.Item;
        var sw = Stopwatch.StartNew();
        var hits = _index.Search(Text, MaxRows, emptyListsCommands: Mode == PaletteMode.Anything);
        LastSearchTime = sw.Elapsed;
        if (!SameHits(hits)) Rows.ReplaceAll(hits.Select(h => new PaletteRow(h)));
        var index = -1;
        for (var i = 0; keep is not null && i < hits.Count; i++)
            if (ReferenceEquals(hits[i].Item, keep)) { index = i; break; }
        SelectedIndex = Rows.Count == 0 ? -1 : Math.Max(0, index);
        UpdateStatus();
    }

    /// <summary>The shown rows are these hits already (same items, same highlighted characters): nothing to redraw.</summary>
    private bool SameHits(IReadOnlyList<PaletteHit> hits)
    {
        if (hits.Count != Rows.Count) return false;
        for (var i = 0; i < hits.Count; i++)
        {
            var old = Rows[i].Hit;
            if (!ReferenceEquals(old.Item, hits[i].Item) || old.MatchedTitle != hits[i].MatchedTitle || !old.Indices.AsSpan().SequenceEqual(hits[i].Indices)) return false;
        }
        return true;
    }

    private void UpdateStatus()
    {
        if (Rows.Count > 0) { StatusText = ""; return; }
        StatusText = Mode switch
        {
            PaletteMode.Databases when Workspace is not null && !DatabaseList.For(Workspace).IsLoaded => "Loading databases…",
            PaletteMode.Connections when Text.Length == 0 => "No saved connections",
            _ => Text.Length == 0 ? "" : "No matches",
        };
    }

    public void MoveSelection(int delta)
    {
        if (Rows.Count == 0) return;
        SelectedIndex = ((SelectedIndex < 0 ? 0 : SelectedIndex + delta) % Rows.Count + Rows.Count) % Rows.Count;
    }

    /// <summary>Tab: complete the text to the highlighted item's name.</summary>
    public void Complete()
    {
        if (Selected is { } row) Text = row.Item.CompletionText ?? row.Item.Title;
    }

    /// <summary>Esc: clears the text first; returns true when the palette should close.</summary>
    public bool Escape()
    {
        if (Text.Length == 0) return true;
        Text = "";
        return false;
    }

    /// <summary>Enter (Ctrl+Enter = new tab, Shift+Enter = new window). Returns the invoked item (null when nothing is selected).</summary>
    public PaletteItem? Invoke(bool newTab = false, bool newWindow = false, PaletteRow? row = null)
    {
        row ??= Selected;
        if (row is null) return null;
        CloseRequested?.Invoke();
        row.Item.Invoke(new PaletteInvocation(Query, newTab, newWindow));
        return row.Item;
    }

    private void StartBackgroundLoads()
    {
        var ws = Workspace!;
        if (ws.State != DbSessionState.Connected) return;
        if (Mode is PaletteMode.Anything or PaletteMode.Databases)
        {
            DatabaseList.For(ws).RefreshAsync(ws.Session).ContinueWith(t =>
            {
                if (t.Exception is { } ex) ws.Log("Listing databases failed: " + ex.GetBaseException().Message);
                UiDispatch.Run(PostReload);
            }, TaskScheduler.Default);
        }
        if (Mode == PaletteMode.Anything) LoadLazySchemas(ws);
    }

    /// <summary>Lazy schemas: the palette searches every schema, so load the ones the sidebar has not loaded yet.</summary>
    private void LoadLazySchemas(IWorkspace ws)
    {
        if (_disposed || ws.State != DbSessionState.Connected) return;
        foreach (var s in ws.Metadata.Current.Schemas.Where(s => !s.IsLoaded).Select(s => s.Name).ToList())
        {
            try { ws.Metadata.EnsureSchemaLoadedAsync(s).Forget(); }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { break; }
        }
    }

    private void OnMetadataChanged(object? sender, MetadataChangedEventArgs e)
    {
        if (Mode != PaletteMode.Anything) return;
        // Opened before the first refresh (right after connect) or across a reload: schemas listed now were not known
        // when the palette opened, so load them too (EnsureSchemaLoadedAsync is a no-op for loaded/running ones).
        if (e.Reason is MetadataChangeReason.Refreshed or MetadataChangeReason.LoadedFromDisk && Workspace is { } ws) LoadLazySchemas(ws);
        UiDispatch.Run(PostReload);
    }

    /// <summary>Coalesces bursts of change events (one per lazily loaded schema) into one reload.</summary>
    private void PostReload()
    {
        if (_reloadPosted || _disposed) return;
        _reloadPosted = true;
        UiDispatch.Post(Reload, System.Windows.Threading.DispatcherPriority.Background);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Workspace is not null) Workspace.Metadata.Changed -= OnMetadataChanged;
    }
}
