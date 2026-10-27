namespace TablePlsu.Core.Search;

/// <summary>A successful match: higher <see cref="Score"/> is better; <see cref="Indices"/> are matched candidate positions.</summary>
public readonly record struct FuzzyMatch(int Score, int[] Indices);

/// <summary>
/// Fuzzy matcher for the palette and sidebar. Case-insensitive subsequence matching with bonuses for consecutive runs,
/// word starts (after <c>_ . - space /</c> or a lower→upper camel step) and the first character.
/// Anchors: <c>^pre</c> = prefix, <c>suf$</c> = suffix, <c>^exact$</c> = whole name (no fuzziness when anchored).
/// </summary>
public static class FuzzyMatcher
{
    private const int MatchScore = 16;
    private const int ConsecutiveBonus = 12;
    private const int WordStartBonus = 10;
    private const int FirstCharBonus = 10;
    private const int GapPenalty = 3;
    private const int LeadingPenalty = 1;
    private const int ExactBonus = 1000;

    public static FuzzyMatch? Match(string pattern, string candidate)
    {
        if (pattern.Length == 0) return new FuzzyMatch(0, []);

        var prefix = pattern.StartsWith('^');
        var suffix = pattern.Length > (prefix ? 1 : 0) && pattern.EndsWith('$');
        if (prefix || suffix)
        {
            var core = pattern.Substring(prefix ? 1 : 0, pattern.Length - (prefix ? 1 : 0) - (suffix ? 1 : 0));
            return MatchAnchored(core, candidate, prefix, suffix);
        }
        return MatchFuzzy(pattern, candidate);
    }

    /// <summary>
    /// Same answer as <c>Match(pattern, candidate) is not null</c> (or <see cref="MatchContains"/> when
    /// <paramref name="contains"/>), without scoring: no allocation, linear time. For filters that only need yes/no.
    /// </summary>
    public static bool IsMatch(string pattern, string candidate, bool contains = false)
    {
        if (pattern.Length == 0) return true;
        if (contains) return candidate.Contains(pattern, StringComparison.OrdinalIgnoreCase);

        var prefix = pattern.StartsWith('^');
        var suffix = pattern.Length > (prefix ? 1 : 0) && pattern.EndsWith('$');
        if (prefix || suffix)
        {
            var core = pattern.AsSpan(prefix ? 1 : 0, pattern.Length - (prefix ? 1 : 0) - (suffix ? 1 : 0));
            if (prefix && suffix) return candidate.AsSpan().Equals(core, StringComparison.OrdinalIgnoreCase);
            return prefix ? candidate.AsSpan().StartsWith(core, StringComparison.OrdinalIgnoreCase) : candidate.AsSpan().EndsWith(core, StringComparison.OrdinalIgnoreCase);
        }

        // MatchFuzzy returns a match exactly when the pattern is a case-insensitive subsequence of the candidate.
        int m = pattern.Length, n = candidate.Length;
        if (m > n) return false;
        for (int pi = 0, ci = 0; ci < n; ci++)
        {
            if (char.ToLowerInvariant(pattern[pi]) == char.ToLowerInvariant(candidate[ci]) && ++pi == m) return true;
            if (n - ci - 1 < m - pi) return false;
        }
        return false;
    }

    public static bool TryMatch(string pattern, string candidate, out FuzzyMatch match)
    {
        var m = Match(pattern, candidate);
        match = m.GetValueOrDefault();
        return m.HasValue;
    }

    /// <summary>"Contains" strategy (sidebar.matching = contains): case-insensitive substring.</summary>
    public static FuzzyMatch? MatchContains(string pattern, string candidate)
    {
        if (pattern.Length == 0) return new FuzzyMatch(0, []);
        var at = candidate.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        var score = MatchScore * pattern.Length - at - (candidate.Length - pattern.Length) / 4
                    + (at == 0 ? FirstCharBonus : 0) + (candidate.Length == pattern.Length ? ExactBonus : 0);
        return new FuzzyMatch(score, Enumerable.Range(at, pattern.Length).ToArray());
    }

    /// <summary>Matches every item and returns the best <paramref name="limit"/> by score (ties: shorter key, then input order).</summary>
    public static IReadOnlyList<(T Item, FuzzyMatch Match)> Filter<T>(IEnumerable<T> items, string pattern, Func<T, string> key, int limit = int.MaxValue, bool contains = false)
    {
        var hits = new List<(T Item, FuzzyMatch Match, int Len, int Order)>();
        var order = 0;
        foreach (var item in items)
        {
            var k = key(item);
            var m = contains ? MatchContains(pattern, k) : Match(pattern, k);
            if (m is { } mm) hits.Add((item, mm, k.Length, order));
            order++;
        }
        hits.Sort((a, b) =>
        {
            var c = b.Match.Score.CompareTo(a.Match.Score);
            if (c != 0) return c;
            c = a.Len.CompareTo(b.Len);
            return c != 0 ? c : a.Order.CompareTo(b.Order);
        });
        return hits.Take(limit).Select(h => (h.Item, h.Match)).ToList();
    }

    private static FuzzyMatch? MatchAnchored(string core, string candidate, bool prefix, bool suffix)
    {
        int start;
        if (prefix && suffix)
        {
            if (!candidate.Equals(core, StringComparison.OrdinalIgnoreCase)) return null;
            start = 0;
        }
        else if (prefix)
        {
            if (!candidate.StartsWith(core, StringComparison.OrdinalIgnoreCase)) return null;
            start = 0;
        }
        else
        {
            if (!candidate.EndsWith(core, StringComparison.OrdinalIgnoreCase)) return null;
            start = candidate.Length - core.Length;
        }
        var score = MatchScore * core.Length + ConsecutiveBonus * Math.Max(0, core.Length - 1)
                    - (candidate.Length - core.Length) + (candidate.Length == core.Length ? ExactBonus : 0);
        return new FuzzyMatch(score, Enumerable.Range(start, core.Length).ToArray());
    }

    /// <summary>DP over (pattern index, candidate index) keeping the best score for alignments that end at each position.</summary>
    private static FuzzyMatch? MatchFuzzy(string pattern, string candidate)
    {
        int m = pattern.Length, n = candidate.Length;
        if (m > n) return null;

        // Quick subsequence rejection.
        for (int pi = 0, ci = 0; ; ci++)
        {
            if (pi == m) break;
            if (ci == n) return null;
            if (char.ToLowerInvariant(pattern[pi]) == char.ToLowerInvariant(candidate[ci])) pi++;
        }

        const int NegInf = int.MinValue / 4;
        var score = new int[m, n];      // best score with pattern[i] matched at candidate[j]
        var from = new int[m, n];       // candidate index of pattern[i-1] in that best alignment
        var bonus = new int[n];
        for (var j = 0; j < n; j++) bonus[j] = PositionBonus(candidate, j);

        for (var i = 0; i < m; i++)
        {
            var pc = char.ToLowerInvariant(pattern[i]);
            for (var j = 0; j < n; j++)
            {
                score[i, j] = NegInf;
                if (char.ToLowerInvariant(candidate[j]) != pc) continue;

                if (i == 0)
                {
                    score[i, j] = MatchScore + bonus[j] + (j == 0 ? FirstCharBonus : 0) - LeadingPenalty * Math.Min(j, 10);
                    from[i, j] = -1;
                    continue;
                }

                var best = NegInf;
                var bestFrom = -1;
                if (j >= 1 && score[i - 1, j - 1] > NegInf)
                {
                    best = score[i - 1, j - 1] + MatchScore + ConsecutiveBonus + bonus[j];
                    bestFrom = j - 1;
                }
                for (var k = j - 2; k >= 0; k--)
                {
                    if (score[i - 1, k] <= NegInf) continue;
                    var v = score[i - 1, k] + MatchScore + bonus[j] - GapPenalty * Math.Min(j - k - 1, 8);
                    if (v > best) { best = v; bestFrom = k; }
                }
                score[i, j] = best;
                from[i, j] = bestFrom;
            }
        }

        var endBest = NegInf;
        var endIdx = -1;
        for (var j = m - 1; j < n; j++)
            if (score[m - 1, j] > endBest) { endBest = score[m - 1, j]; endIdx = j; }
        if (endIdx < 0) return null;

        var indices = new int[m];
        for (int i = m - 1, j = endIdx; i >= 0; i--)
        {
            indices[i] = j;
            j = from[i, j];
        }
        var total = endBest - (n - m) / 2 + (n == m ? ExactBonus : 0);
        return new FuzzyMatch(total, indices);
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
