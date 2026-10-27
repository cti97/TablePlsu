using System.Text.Json;
using System.Text.Json.Serialization;
using TablePlsu.Core.IO;
using TablePlsu.Core.Paths;

namespace TablePlsu.Core.Editors;

/// <summary>Persisted state of one SQL editor tab (its text lives in <c>{Id}.sql</c> next to session.json).</summary>
public sealed record EditorTabState
{
    public string Id { get; init; } = "";
    /// <summary>Automatic title ("Query 1", file name…).</summary>
    public string? Title { get; init; }
    /// <summary>User-set label from "Rename Tab"; wins over <see cref="Title"/> when set.</summary>
    public string? CustomTitle { get; init; }
    public int CaretOffset { get; init; }
    public int SelectionStart { get; init; }
    public int SelectionLength { get; init; }
    public double ScrollOffsetX { get; init; }
    public double ScrollOffsetY { get; init; }
    /// <summary>A .sql file the tab is linked to (opened or saved-as); autosave also writes there.</summary>
    public string? LinkedFilePath { get; init; }
    /// <summary>Folded regions as [start, end] offsets (P1).</summary>
    public List<int[]>? Folds { get; init; }
}

/// <summary>A closed tab kept for Ctrl+Shift+T. Its .sql file stays on disk until it falls off the list.</summary>
public sealed record RecentlyClosedTab
{
    public string Id { get; init; } = "";
    public string? Title { get; init; }
    public DateTimeOffset ClosedAt { get; init; }
    public EditorTabState? State { get; init; }
}

/// <summary>session.json: tab order, the active tab and the recently-closed list for one connection.</summary>
public sealed record EditorSession
{
    public List<EditorTabState> Tabs { get; init; } = [];
    public string? ActiveTabId { get; init; }
    public List<RecentlyClosedTab> RecentlyClosed { get; init; } = [];
}

/// <summary>A tab restored from disk.</summary>
public readonly record struct RestoredTab(EditorTabState State, string Text);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(EditorSession))]
internal sealed partial class EditorJsonContext : JsonSerializerContext;

/// <summary>
/// Autosave for SQL editor tabs of one connection: <c>editors\{connId}\{tabId}.sql</c> plus <c>session.json</c>.
/// <see cref="SaveText"/> is debounced (default 500 ms) and writes atomically on a background thread;
/// <see cref="Flush"/> writes everything pending now (call on run, tab close, deactivate, exit).
/// Thread-safe. Never throws for I/O problems: they go to the log callback.
/// </summary>
public sealed class EditorSessionStore : IDisposable
{
    public const int MaxRecentlyClosed = 20;

    private readonly string _dir;
    private readonly TimeSpan _debounce;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _linked = new(StringComparer.Ordinal);
    private readonly Timer _timer;
    private EditorSession _session = new();
    private bool _sessionLoaded;
    private bool _disposed;

    public EditorSessionStore(AppPaths paths, string connectionId, TimeSpan? debounce = null, Action<string>? log = null)
    {
        _dir = paths.EditorDir(connectionId);
        _debounce = debounce ?? TimeSpan.FromMilliseconds(500);
        _log = log;
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public string Directory => _dir;

    public static string NewTabId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>Newest first.</summary>
    public IReadOnlyList<RecentlyClosedTab> RecentlyClosed
    {
        get { lock (_gate) { EnsureSession(); return _session.RecentlyClosed.ToList(); } }
    }

    /// <summary>Reads session.json (empty session when missing or corrupt).</summary>
    public EditorSession Load()
    {
        lock (_gate)
        {
            _sessionLoaded = false;
            EnsureSession();
            foreach (var t in _session.Tabs)
                if (t.LinkedFilePath is { Length: > 0 } p) _linked[t.Id] = p;
            return Snapshot();
        }
    }

    /// <summary>Loads the session and every tab's text (missing files restore as empty text).</summary>
    public IReadOnlyList<RestoredTab> LoadTabs()
    {
        var session = Load();
        return session.Tabs.Select(t => new RestoredTab(t, ReadText(t.Id) ?? "")).ToList();
    }

    /// <summary>The latest text for a tab: pending (unsaved) text first, then the file. Null when unknown.</summary>
    public string? ReadText(string tabId)
    {
        lock (_gate)
            if (_pending.TryGetValue(tabId, out var pending)) return pending;
        try
        {
            var path = TabFile(tabId);
            return path is not null && File.Exists(path) ? AtomicFile.ReadAllTextShared(path) : null;
        }
        catch (Exception ex) when (IsIo(ex))
        {
            Log($"Could not read tab {tabId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Queues the tab text; it is written after the debounce interval with no further calls.</summary>
    public void SaveText(string tabId, string text)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _pending[tabId] = text;
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Links a tab to a .sql file so autosave writes there too (null unlinks).</summary>
    public void LinkFile(string tabId, string? path)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(path)) _linked.Remove(tabId);
            else _linked[tabId] = path;
        }
    }

    /// <summary>Writes all pending texts immediately.</summary>
    public void Flush()
    {
        KeyValuePair<string, string>[] work;
        Dictionary<string, string> linked;
        lock (_gate)
        {
            if (_pending.Count == 0) return;
            work = [.. _pending];
            _pending.Clear();
            linked = new Dictionary<string, string>(_linked);
        }
        foreach (var (tabId, text) in work)
        {
            var path = TabFile(tabId);
            if (path is null) continue;
            TryWrite(path, text);
            if (linked.TryGetValue(tabId, out var linkedPath)) TryWrite(linkedPath, text);
        }
    }

    /// <summary>Replaces tab order/state and the active tab, keeping the recently-closed list, and writes session.json.</summary>
    public void SaveSession(EditorSession session)
    {
        lock (_gate)
        {
            EnsureSession();
            _session = session with { RecentlyClosed = session.RecentlyClosed.Count > 0 ? session.RecentlyClosed : _session.RecentlyClosed };
            foreach (var t in _session.Tabs)
                if (t.LinkedFilePath is { Length: > 0 } p) _linked[t.Id] = p;
            PersistSession();
        }
    }

    /// <summary>Removes the tab from the session and puts it at the top of the recently-closed list (max 20).</summary>
    public void CloseTab(string tabId)
    {
        Flush();
        lock (_gate)
        {
            EnsureSession();
            var state = _session.Tabs.Find(t => t.Id == tabId) ?? new EditorTabState { Id = tabId };
            var tabs = _session.Tabs.Where(t => t.Id != tabId).ToList();
            var closed = _session.RecentlyClosed.Where(r => r.Id != tabId).ToList();
            closed.Insert(0, new RecentlyClosedTab
            {
                Id = tabId,
                Title = state.CustomTitle ?? state.Title,
                ClosedAt = DateTimeOffset.UtcNow,
                State = state,
            });
            while (closed.Count > MaxRecentlyClosed)
            {
                var dropped = closed[^1];
                closed.RemoveAt(closed.Count - 1);
                if (TabFile(dropped.Id) is { } f) TryDelete(f);
            }
            _linked.Remove(tabId);
            _session = _session with
            {
                Tabs = tabs,
                ActiveTabId = _session.ActiveTabId == tabId ? tabs.FirstOrDefault()?.Id : _session.ActiveTabId,
                RecentlyClosed = closed,
            };
            PersistSession();
        }
    }

    /// <summary>Pops the most recently closed tab, appends it to the session and returns it with its text.</summary>
    public RestoredTab? ReopenLast()
    {
        RecentlyClosedTab entry;
        lock (_gate)
        {
            EnsureSession();
            if (_session.RecentlyClosed.Count == 0) return null;
            entry = _session.RecentlyClosed[0];
            var state = entry.State ?? new EditorTabState { Id = entry.Id, Title = entry.Title };
            _session = _session with
            {
                RecentlyClosed = _session.RecentlyClosed.Skip(1).ToList(),
                Tabs = [.. _session.Tabs, state],
                ActiveTabId = state.Id,
            };
            if (state.LinkedFilePath is { Length: > 0 } p) _linked[state.Id] = p;
            PersistSession();
        }
        var restored = entry.State ?? new EditorTabState { Id = entry.Id, Title = entry.Title };
        return new RestoredTab(restored, ReadText(entry.Id) ?? "");
    }

    private void EnsureSession()
    {
        if (_sessionLoaded) return;
        _sessionLoaded = true;
        _session = new EditorSession();
        var file = Path.Combine(_dir, "session.json");
        try
        {
            if (!File.Exists(file)) return;
            var loaded = JsonSerializer.Deserialize(AtomicFile.ReadAllTextShared(file), EditorJsonContext.Default.EditorSession);
            if (loaded is not null)
            {
                _session = loaded with
                {
                    Tabs = loaded.Tabs.Where(t => AppPaths.IsSafeSegment(t.Id)).ToList(),
                    RecentlyClosed = loaded.RecentlyClosed.Where(t => AppPaths.IsSafeSegment(t.Id)).Take(MaxRecentlyClosed).ToList(),
                };
            }
        }
        catch (Exception ex) when (IsIo(ex) || ex is JsonException or NotSupportedException)
        {
            Log($"Editor session for {_dir} could not be read: {ex.Message}");
        }
    }

    private void PersistSession()
    {
        TryWrite(Path.Combine(_dir, "session.json"), JsonSerializer.Serialize(_session, EditorJsonContext.Default.EditorSession));
    }

    private EditorSession Snapshot() => _session with
    {
        Tabs = [.. _session.Tabs],
        RecentlyClosed = [.. _session.RecentlyClosed],
    };

    private string? TabFile(string tabId)
    {
        if (!AppPaths.IsSafeSegment(tabId))
        {
            Log($"Invalid editor tab id '{tabId}'.");
            return null;
        }
        return Path.Combine(_dir, tabId + ".sql");
    }

    private void TryWrite(string path, string text)
    {
        try { AtomicFile.WriteAllText(path, text); }
        catch (Exception ex) when (IsIo(ex))
        {
            Log($"Autosave failed for {path}: {ex.Message}");
        }
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (IsIo(ex)) { Log($"Could not delete {path}: {ex.Message}"); }
    }

    private static bool IsIo(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    private void Log(string message)
    {
        try { _log?.Invoke(message); } catch { /* never throw from logging */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _timer.Dispose();
        Flush();
    }
}
