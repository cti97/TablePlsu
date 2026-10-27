using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Search;

namespace TablePlsu.App.Features.Palette;

/// <summary>One ranked palette hit.</summary>
public sealed record PaletteHit(PaletteItem Item, int Score, int[] Indices, bool MatchedTitle);

/// <summary>
/// The candidate list, precomputed once when the palette opens (or when providers' data changes), so each keystroke
/// is just a scan. Scores are exactly <see cref="FuzzyMatcher"/>'s (anchors <c>^</c> / <c>$</c> included), computed by an
/// allocation-free copy of its DP (<see cref="Score"/>, kept equal by tests); match indices are only computed for the
/// rows shown. Every item is matched on its <see cref="PaletteItem.Title"/> and, for qualified queries (containing '.'
/// or a space) or when the title does not match, on its <see cref="PaletteItem.SearchText"/> (for example
/// "schema.table"); the better score wins, so <c>^ord</c> matches the table name while <c>sal.ord</c> matches the
/// qualified name. A query that extends the previous one only rescans the previous hits.
/// Empty input lists app commands (items of kind <see cref="PaletteKinds.Command"/>), sorted by title.
/// Not thread-safe (one palette, UI thread).
/// </summary>
public sealed class PaletteIndex
{
    // FuzzyMatcher's scoring constants (Core/Search/FuzzyMatcher.cs).
    private const int MatchScore = 16;
    private const int ConsecutiveBonus = 12;
    private const int WordStartBonus = 10;
    private const int FirstCharBonus = 10;
    private const int GapPenalty = 3;
    private const int LeadingPenalty = 1;
    private const int ExactBonus = 1000;
    private const int NegInf = int.MinValue / 4;

    private readonly PaletteItem[] _items;
    private readonly string[] _titles;
    private readonly string?[] _alt;
    private string? _lastQuery;
    private List<int>? _lastMatched;
    private int[] _prev = new int[64];
    private int[] _cur = new int[64];
    private int[] _bonus = new int[64];

    public PaletteIndex(IEnumerable<PaletteItem> items)
    {
        _items = [.. items];
        _titles = new string[_items.Length];
        _alt = new string?[_items.Length];
        for (var i = 0; i < _items.Length; i++)
        {
            _titles[i] = _items[i].Title;
            var s = _items[i].SearchText;
            _alt[i] = string.IsNullOrEmpty(s) || s == _titles[i] ? null : s;
        }
    }

    public int Count => _items.Length;

    public IReadOnlyList<PaletteItem> Items => _items;

    public IReadOnlyList<PaletteHit> Search(string query, int limit = 50, bool emptyListsCommands = true)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            _lastQuery = null;
            IEnumerable<PaletteItem> empty = emptyListsCommands
                ? _items.Where(i => i.Kind == PaletteKinds.Command).OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
                : _items.OrderByDescending(i => i.Priority);
            return [.. empty.Take(limit).Select(i => new PaletteHit(i, 0, [], true))];
        }

        var candidates = Narrow(query);
        var useAlt = UsesAlt(query);
        var lower = query.ToLowerInvariant();
        // Keep only the best `limit` hits (sorted insertion) instead of sorting every match.
        var hits = new List<(int Index, int Score, bool Title)>(limit + 1);
        var matched = new List<int>();
        foreach (var i in candidates)
        {
            var best = Score(query, lower, _titles[i]);
            var onTitle = best is not null;
            if (_alt[i] is { } alt && (useAlt || best is null) && Score(query, lower, alt) is { } m && (best is null || m > best.Value))
            {
                best = m;
                onTitle = false;
            }
            if (best is not { } b) continue;
            matched.Add(i);
            var hit = (i, b, onTitle);
            if (hits.Count == limit && Compare(hit, hits[^1]) >= 0) continue;
            var at = hits.Count;
            while (at > 0 && Compare(hit, hits[at - 1]) < 0) at--;
            hits.Insert(at, hit);
            if (hits.Count > limit) hits.RemoveAt(hits.Count - 1);
        }
        _lastQuery = query;
        _lastMatched = matched;

        var result = new List<PaletteHit>(Math.Min(limit, hits.Count));
        foreach (var h in hits)
        {
            if (result.Count >= limit) break;
            var key = h.Title ? _titles[h.Index] : _alt[h.Index]!;
            var indices = FuzzyMatcher.Match(query, key)?.Indices ?? [];
            result.Add(new PaletteHit(_items[h.Index], h.Score, indices, h.Title));
        }
        return result;
    }

    /// <summary>Ranking: score desc, priority desc, shorter title, input order.</summary>
    private int Compare((int Index, int Score, bool Title) a, (int Index, int Score, bool Title) b)
    {
        var c = b.Score.CompareTo(a.Score);
        if (c != 0) return c;
        c = _items[b.Index].Priority.CompareTo(_items[a.Index].Priority);
        if (c != 0) return c;
        c = _titles[a.Index].Length.CompareTo(_titles[b.Index].Length);
        return c != 0 ? c : a.Index.CompareTo(b.Index);
    }

    private static bool UsesAlt(string q) => q.Contains('.') || q.Contains(' ');

    private static bool HasAnchor(string q) => q.StartsWith('^') || q.EndsWith('$');

    private IEnumerable<int> Narrow(string query)
    {
        if (_lastQuery is { Length: > 0 } last && _lastMatched is not null && query.Length > last.Length
            && query.StartsWith(last, StringComparison.OrdinalIgnoreCase)
            && !HasAnchor(last) && !HasAnchor(query) && UsesAlt(last) == UsesAlt(query))
            return _lastMatched;
        return Enumerable.Range(0, _items.Length);
    }

    /// <summary><see cref="FuzzyMatcher.Match"/>'s score (null = no match) without allocating.</summary>
    public int? Score(string pattern, string candidate) => Score(pattern, pattern.ToLowerInvariant(), candidate);

    private int? Score(string pattern, string lower, string candidate)
    {
        if (pattern.StartsWith('^') || (pattern.Length > 0 && pattern.EndsWith('$')))
            return FuzzyMatcher.Match(pattern, candidate)?.Score; // anchored: no DP, cheap

        int m = lower.Length, n = candidate.Length;
        if (m > n) return null;
        for (int pi = 0, ci = 0; ; ci++)
        {
            if (pi == m) break;
            if (ci == n) return null;
            if (lower[pi] == char.ToLowerInvariant(candidate[ci])) pi++;
        }

        if (_bonus.Length < n)
        {
            var size = Math.Max(n, _bonus.Length * 2);
            _bonus = new int[size];
            _prev = new int[size];
            _cur = new int[size];
        }
        var bonus = _bonus;
        for (var j = 0; j < n; j++) bonus[j] = PositionBonus(candidate, j);

        var prev = _prev;
        var cur = _cur;
        var p0 = lower[0];
        for (var j = 0; j < n; j++)
            prev[j] = char.ToLowerInvariant(candidate[j]) == p0
                ? MatchScore + bonus[j] + (j == 0 ? FirstCharBonus : 0) - LeadingPenalty * Math.Min(j, 10)
                : NegInf;

        for (var i = 1; i < m; i++)
        {
            var pc = lower[i];
            for (var j = 0; j < n; j++)
            {
                cur[j] = NegInf;
                if (char.ToLowerInvariant(candidate[j]) != pc) continue;
                var best = NegInf;
                if (j >= 1 && prev[j - 1] > NegInf) best = prev[j - 1] + MatchScore + ConsecutiveBonus + bonus[j];
                for (var k = j - 2; k >= 0; k--)
                {
                    if (prev[k] <= NegInf) continue;
                    var v = prev[k] + MatchScore + bonus[j] - GapPenalty * Math.Min(j - k - 1, 8);
                    if (v > best) best = v;
                }
                cur[j] = best;
            }
            (prev, cur) = (cur, prev);
        }

        var endBest = NegInf;
        for (var j = m - 1; j < n; j++)
            if (prev[j] > endBest) endBest = prev[j];
        if (endBest <= NegInf) return null;
        return endBest - (n - m) / 2 + (n == m ? ExactBonus : 0);
    }

    private static int PositionBonus(string s, int j)
    {
        if (j == 0) return WordStartBonus;
        var prev = s[j - 1];
        var cur = s[j];
        if (prev is '_' or '.' or '-' or ' ' or '/' or '\\' or ':') return WordStartBonus;
        if (char.IsLower(prev) && char.IsUpper(cur)) return WordStartBonus - 2;
        if (!char.IsLetterOrDigit(prev)) return WordStartBonus / 2;
        return 0;
    }
}

/// <summary>Palette item kinds used by this feature (other features may add their own strings).</summary>
public static class PaletteKinds
{
    public const string Command = "command";
    public const string Database = ObjectKinds.Database;
    public const string Schema = ObjectKinds.Schema;
    public const string Table = ObjectKinds.Table;
    public const string View = ObjectKinds.View;
    public const string MaterializedView = ObjectKinds.MaterializedView;
    public const string Function = ObjectKinds.Function;
    public const string Procedure = ObjectKinds.Procedure;
    public const string SqlTab = "sqlTab";
    public const string Connection = "connection";

    public static string Label(string kind) => kind switch
    {
        Command => "Command",
        Database => "Database",
        Schema => "Schema",
        Table => "Table",
        View => "View",
        MaterializedView => "Materialized view",
        Function => "Function",
        Procedure => "Procedure",
        SqlTab => "SQL tab",
        Connection => "Connection",
        _ => kind,
    };

    public static string Icon(string kind) => kind switch
    {
        Command => "",
        Database => "",
        Schema => "",
        Table => "",
        View => "",
        MaterializedView => "",
        Function => "",
        Procedure => "",
        SqlTab => "",
        Connection => "",
        _ => "",
    };
}
