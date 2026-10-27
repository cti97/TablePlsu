using System.Security.Cryptography;
using System.Text;
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
/// <see cref="Flush"/> writes everything pending now and returns only after any in-flight write has landed (writes are
/// serialized, so an older text never overwrites a newer one). A failed write keeps the text pending and retries.
/// <para>
/// Several stores (one per window/workspace, possibly in several processes) may share one connection's directory. Each
/// store owns the tabs it restored or created; every session.json change re-reads the file under a cross-process lock and
/// merges, so one window never drops another window's tabs. <see cref="LoadTabs"/> skips tabs another live store in this
/// process has open. Tab files with no session entry (a crash between the text and the session write) are recovered.
/// </para>
/// Thread-safe. Never throws for I/O problems: they go to the log callback.
/// </summary>
public sealed class EditorSessionStore : IDisposable
{
    public const int MaxRecentlyClosed = 20;
    private const string SessionFileName = "session.json";

    /// <summary>Tab ids open in live stores of this process, per editor directory.</summary>
    private static readonly Dictionary<string, Dictionary<string, EditorSessionStore>> Claims = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _dir;
    private readonly TimeSpan _debounce;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly object _writeGate = new();
    private readonly Dictionary<string, string> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _writing = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _linked = new(StringComparer.Ordinal);
    private readonly HashSet<string> _owned = new(StringComparer.Ordinal);
    private readonly Timer _timer;
    private readonly string _mutexName;
    private EditorSession _session = new();
    private bool _sessionLoaded;
    private bool _disposed;

    public EditorSessionStore(AppPaths paths, string connectionId, TimeSpan? debounce = null, Action<string>? log = null)
    {
        _dir = Path.GetFullPath(paths.EditorDir(connectionId));
        _debounce = debounce ?? TimeSpan.FromMilliseconds(500);
        _log = log;
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_dir.ToUpperInvariant())))[..32];
        _mutexName = @"Local\TablePlsu.Editors." + hash;
    }

    public string Directory => _dir;

    private string SessionFile => Path.Combine(_dir, SessionFileName);

    public static string NewTabId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>Newest first.</summary>
    public IReadOnlyList<RecentlyClosedTab> RecentlyClosed
    {
        get { lock (_gate) { EnsureSession(); return _session.RecentlyClosed.ToList(); } }
    }

    /// <summary>Reads session.json (empty session when missing or corrupt), plus orphaned tab files as recovered tabs.</summary>
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

    /// <summary>
    /// Loads the session and the text of every tab not already open in another live store of this process (at most
    /// <paramref name="max"/>); the returned tabs become this store's. Missing files restore as empty text.
    /// </summary>
    public IReadOnlyList<RestoredTab> LoadTabs(int? max = null)
    {
        var session = Load();
        List<EditorTabState> mine;
        lock (Claims)
        {
            var claims = ClaimsOfDir();
            mine = session.Tabs.Where(t => !claims.TryGetValue(t.Id, out var owner) || owner == this).Take(max ?? int.MaxValue).ToList();
            foreach (var t in mine) Claim(t.Id);
        }
        return mine.Select(t => new RestoredTab(t, ReadText(t.Id) ?? "")).ToList();
    }

    /// <summary>The latest text for a tab: pending (unsaved) text first, then the file. Null when unknown.</summary>
    public string? ReadText(string tabId)
    {
        lock (_gate)
        {
            if (_pending.TryGetValue(tabId, out var pending)) return pending;
            if (_writing.TryGetValue(tabId, out var writing)) return writing;
        }
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
        lock (Claims) Claim(tabId);
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

    /// <summary>
    /// Writes all pending texts now. Returns after they (and any write already in flight) are on disk. A text whose
    /// write failed stays pending (unless newer text replaced it) and is retried.
    /// </summary>
    public void Flush()
    {
        lock (_writeGate)
        {
            KeyValuePair<string, string>[] work;
            Dictionary<string, string> linked;
            lock (_gate)
            {
                if (_pending.Count == 0) return;
                work = [.. _pending];
                foreach (var (id, text) in work) _writing[id] = text;
                _pending.Clear();
                linked = new Dictionary<string, string>(_linked);
            }
            var failed = false;
            foreach (var (tabId, text) in work)
            {
                var path = TabFile(tabId);
                var ok = path is null || TryWrite(path, text);
                if (linked.TryGetValue(tabId, out var linkedPath)) TryWrite(linkedPath, text);
                lock (_gate)
                {
                    _writing.Remove(tabId);
                    if (!ok && !_pending.ContainsKey(tabId))
                    {
                        _pending[tabId] = text;
                        failed = true;
                    }
                }
            }
            if (failed)
            {
                lock (_gate)
                    if (!_disposed) _timer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>
    /// Sets this store's tabs (order/state) and the active tab, keeping the recently-closed list, and writes session.json.
    /// Tabs on disk that belong to another store (window) are kept; this store's tabs missing from
    /// <paramref name="session"/> are dropped.
    /// </summary>
    public void SaveSession(EditorSession session)
    {
        lock (_gate)
        {
            WithSessionFile(disk =>
            {
                HashSet<string> previous;
                lock (Claims)
                {
                    previous = [.. _owned];
                    foreach (var id in previous) Unclaim(id);
                    foreach (var t in session.Tabs) Claim(t.Id);
                }
                var tabs = Merge(disk.Tabs, session.Tabs, previous);
                var ids = tabs.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
                var closed = session.RecentlyClosed.Count > 0 ? session.RecentlyClosed : disk.RecentlyClosed;
                return new EditorSession
                {
                    Tabs = tabs,
                    ActiveTabId = session.ActiveTabId is { } a && ids.Contains(a) ? a : disk.ActiveTabId is { } d && ids.Contains(d) ? d : tabs.FirstOrDefault()?.Id,
                    RecentlyClosed = closed.Where(r => !ids.Contains(r.Id)).ToList(),
                };
            });
            foreach (var t in session.Tabs)
                if (t.LinkedFilePath is { Length: > 0 } p) _linked[t.Id] = p;
        }
    }

    /// <summary>Removes the tab from the session and puts it at the top of the recently-closed list (max 20).</summary>
    public void CloseTab(string tabId)
    {
        Flush();
        lock (_gate)
        {
            WithSessionFile(disk =>
            {
                var state = disk.Tabs.Find(t => t.Id == tabId) ?? _session.Tabs.Find(t => t.Id == tabId) ?? new EditorTabState { Id = tabId };
                var tabs = disk.Tabs.Where(t => t.Id != tabId).ToList();
                var ids = tabs.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
                var closed = disk.RecentlyClosed.Where(r => r.Id != tabId && !ids.Contains(r.Id)).ToList();
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
                    if (!ids.Contains(dropped.Id) && TabFile(dropped.Id) is { } f) TryDelete(f);
                }
                return new EditorSession
                {
                    Tabs = tabs,
                    ActiveTabId = disk.ActiveTabId == tabId || disk.ActiveTabId is null ? tabs.FirstOrDefault()?.Id : disk.ActiveTabId,
                    RecentlyClosed = closed,
                };
            });
            _linked.Remove(tabId);
            lock (Claims) Unclaim(tabId);
        }
    }

    /// <summary>Pops the most recently closed tab, appends it to the session and returns it with its text.</summary>
    public RestoredTab? ReopenLast()
    {
        RecentlyClosedTab? entry = null;
        lock (_gate)
        {
            WithSessionFile(disk =>
            {
                if (disk.RecentlyClosed.Count == 0) return null;
                entry = disk.RecentlyClosed[0];
                var state = entry.State ?? new EditorTabState { Id = entry.Id, Title = entry.Title };
                return disk with
                {
                    RecentlyClosed = disk.RecentlyClosed.Skip(1).ToList(),
                    Tabs = [.. disk.Tabs.Where(t => t.Id != state.Id), state],
                    ActiveTabId = state.Id,
                };
            });
            if (entry is null) return null;
            if (entry.State?.LinkedFilePath is { Length: > 0 } p) _linked[entry.Id] = p;
            lock (Claims) Claim(entry.Id);
        }
        var restored = entry.State ?? new EditorTabState { Id = entry.Id, Title = entry.Title };
        return new RestoredTab(restored, ReadText(entry.Id) ?? "");
    }

    // ---- session.json -------------------------------------------------------------------------------------------

    /// <summary>
    /// Read-modify-write of session.json under the cross-process lock (caller holds <see cref="_gate"/>).
    /// <paramref name="change"/> gets the current file contents (or the in-memory copy when the file is unreadable) and
    /// returns the new session, or null for no change.
    /// </summary>
    private void WithSessionFile(Func<EditorSession, EditorSession?> change)
    {
        EnsureSession();
        using var mutex = AcquireMutex(out var held);
        try
        {
            var disk = ReadSessionFile(out var corrupt) ?? (corrupt ? _session : new EditorSession());
            var updated = change(disk);
            if (updated is null)
            {
                _session = disk;
                return;
            }
            _session = updated;
            if (corrupt) BackupCorrupt();
            PersistSession();
        }
        finally
        {
            if (held) mutex?.ReleaseMutex();
        }
    }

    private Mutex? AcquireMutex(out bool held)
    {
        held = false;
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(false, _mutexName);
            try { held = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { held = true; }
            if (!held) Log("Editor session lock timed out; writing anyway.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            Log("Editor session lock unavailable: " + ex.Message);
        }
        return mutex;
    }

    /// <summary>This store's tabs replace its previous ones at their position; other stores' tabs keep theirs.</summary>
    private static List<EditorTabState> Merge(List<EditorTabState> disk, List<EditorTabState> mine, HashSet<string> previous)
    {
        var mineIds = mine.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var result = new List<EditorTabState>();
        var placed = false;
        foreach (var t in disk)
        {
            if (mineIds.Contains(t.Id) || previous.Contains(t.Id))
            {
                if (!placed) result.AddRange(mine);
                placed = true;
                continue;
            }
            if (!result.Exists(r => r.Id == t.Id)) result.Add(t);
        }
        if (!placed) result.AddRange(mine);
        return result;
    }

    private void EnsureSession()
    {
        if (_sessionLoaded) return;
        _sessionLoaded = true;
        CleanStaleTempFiles();
        _session = ReadSessionFile(out _) ?? new EditorSession();
        RecoverOrphans();
    }

    /// <summary>Null when missing or unreadable (<paramref name="corrupt"/> tells which).</summary>
    private EditorSession? ReadSessionFile(out bool corrupt)
    {
        corrupt = false;
        try
        {
            if (!File.Exists(SessionFile)) return null;
            var loaded = JsonSerializer.Deserialize(AtomicFile.ReadAllTextShared(SessionFile), EditorJsonContext.Default.EditorSession);
            if (loaded is null) return null;
            return loaded with
            {
                Tabs = (loaded.Tabs ?? []).Where(t => t is not null && AppPaths.IsSafeSegment(t.Id)).DistinctBy(t => t.Id).ToList(),
                RecentlyClosed = (loaded.RecentlyClosed ?? []).Where(t => t is not null && AppPaths.IsSafeSegment(t.Id)).Take(MaxRecentlyClosed).ToList(),
            };
        }
        catch (Exception ex) when (IsIo(ex) || ex is JsonException or NotSupportedException)
        {
            corrupt = ex is JsonException or NotSupportedException;
            Log($"Editor session for {_dir} could not be read: {ex.Message}");
            return null;
        }
    }

    /// <summary>Tab files listed nowhere in the session (crash before session.json caught up) come back as tabs.</summary>
    private void RecoverOrphans()
    {
        string[] files;
        try
        {
            if (!System.IO.Directory.Exists(_dir)) return;
            files = System.IO.Directory.GetFiles(_dir, "*.sql");
        }
        catch (Exception ex) when (IsIo(ex)) { return; }
        var known = _session.Tabs.Select(t => t.Id).Concat(_session.RecentlyClosed.Select(r => r.Id)).ToHashSet(StringComparer.Ordinal);
        lock (Claims)
        {
            var claims = ClaimsOfDir();
            known.UnionWith(claims.Keys);
        }
        var recovered = files
            .Select(f => (Id: Path.GetFileNameWithoutExtension(f), Time: SafeWriteTime(f)))
            .Where(x => AppPaths.IsSafeSegment(x.Id) && !x.Id.StartsWith('.') && !known.Contains(x.Id))
            .OrderBy(x => x.Time)
            .Select(x => new EditorTabState { Id = x.Id, Title = "Recovered" })
            .ToList();
        if (recovered.Count == 0) return;
        Log($"Recovered {recovered.Count} editor tab file(s) missing from {SessionFileName}.");
        _session = _session with { Tabs = [.. _session.Tabs, .. recovered] };
    }

    private static DateTime SafeWriteTime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); } catch (Exception) { return DateTime.MinValue; }
    }

    /// <summary>Temp files left by a crash mid-write (never the real text: the target is only replaced after a full write).</summary>
    private void CleanStaleTempFiles()
    {
        try
        {
            if (!System.IO.Directory.Exists(_dir)) return;
            foreach (var f in System.IO.Directory.GetFiles(_dir, ".*.tmp"))
                if (DateTime.UtcNow - SafeWriteTime(f) > TimeSpan.FromMinutes(5)) TryDelete(f);
        }
        catch (Exception ex) when (IsIo(ex)) { /* best effort */ }
    }

    private void BackupCorrupt()
    {
        try
        {
            if (File.Exists(SessionFile))
                File.Copy(SessionFile, Path.Combine(_dir, $"{SessionFileName}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}"), overwrite: true);
        }
        catch (Exception ex) when (IsIo(ex)) { Log("Could not back up the corrupt editor session: " + ex.Message); }
    }

    private void PersistSession() =>
        TryWrite(SessionFile, JsonSerializer.Serialize(_session, EditorJsonContext.Default.EditorSession));

    private EditorSession Snapshot() => _session with
    {
        Tabs = [.. _session.Tabs],
        RecentlyClosed = [.. _session.RecentlyClosed],
    };

    // ---- claims (lock Claims) -----------------------------------------------------------------------------------

    private Dictionary<string, EditorSessionStore> ClaimsOfDir()
    {
        if (!Claims.TryGetValue(_dir, out var map)) Claims[_dir] = map = new Dictionary<string, EditorSessionStore>(StringComparer.Ordinal);
        return map;
    }

    private void Claim(string tabId)
    {
        if (_disposed) return;
        ClaimsOfDir()[tabId] = this;
        _owned.Add(tabId);
    }

    private void Unclaim(string tabId)
    {
        var map = ClaimsOfDir();
        if (map.TryGetValue(tabId, out var owner) && owner == this) map.Remove(tabId);
        _owned.Remove(tabId);
    }

    // ---- io -----------------------------------------------------------------------------------------------------

    private string? TabFile(string tabId)
    {
        if (!AppPaths.IsSafeSegment(tabId))
        {
            Log($"Invalid editor tab id '{tabId}'.");
            return null;
        }
        return Path.Combine(_dir, tabId + ".sql");
    }

    private bool TryWrite(string path, string text)
    {
        try
        {
            AtomicFile.WriteAllText(path, text);
            return true;
        }
        catch (Exception ex) when (IsIo(ex))
        {
            Log($"Autosave failed for {path}: {ex.Message}");
            return false;
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
        lock (Claims)
        {
            foreach (var id in _owned.ToList()) Unclaim(id);
        }
    }
}
