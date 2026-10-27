namespace TablePlsu.Core.Completion;

internal enum RelationSource { Table, Cte, Subquery, Function }

/// <summary>A relation referenced by the statement: <c>[schema.]name [AS] alias</c>, a CTE, or a subquery.</summary>
internal sealed class StatementRelation
{
    public RelationSource Source { get; init; }
    public string? Schema { get; init; }
    public string Name { get; init; } = "";
    public string? Alias { get; init; }
    /// <summary>Known column names (CTE / subquery / column alias list); null = ask the metadata.</summary>
    public IReadOnlyList<string>? Columns { get; set; }

    /// <summary>Character range of the (sub)query the reference lives in; it is visible to a caret inside it.</summary>
    public int ScopeStart { get; set; }
    public int ScopeEnd { get; set; } = int.MaxValue;

    public bool VisibleAt(int offset) => offset >= ScopeStart && offset <= ScopeEnd;

    /// <summary>The name the statement refers to it by.</summary>
    public string RefName => Alias ?? Name;
}

/// <summary>A CTE: <c>WITH name [(cols)] AS (body)</c>.</summary>
internal sealed class CteDef
{
    public string Name { get; init; } = "";
    public IReadOnlyList<string> Columns { get; set; } = [];
    /// <summary>Star items of the body that must be expanded against metadata: (qualifier or null, relations of the body).</summary>
    public List<(string? Qualifier, List<StatementRelation> Relations)> Stars { get; } = [];
}

/// <summary>
/// Extracts relations (FROM/JOIN/UPDATE/INTO targets with aliases), CTEs and subquery aliases from a token list.
/// Works on a flat token list at every nesting depth: completion only needs "what can this statement see".
/// </summary>
internal sealed class StatementAnalyzer
{
    private readonly List<Tok> _t;

    public List<StatementRelation> Relations { get; } = [];
    public Dictionary<string, CteDef> Ctes { get; } = new(StringComparer.OrdinalIgnoreCase);

    private StatementAnalyzer(List<Tok> tokens) => _t = tokens;

    public static StatementAnalyzer Analyze(List<Tok> tokens)
    {
        var a = new StatementAnalyzer(tokens);
        a.Run(0, tokens.Count, a.Relations, 0, int.MaxValue);
        return a;
    }

    private void Run(int from, int to, List<StatementRelation> into, int scopeStart, int scopeEnd)
    {
        // Per paren depth: are we inside a FROM list (so ',' starts another table reference)? And that depth's scope.
        var fromList = new Stack<bool>();
        var scopes = new Stack<(int Start, int End)>();
        fromList.Push(false);
        scopes.Push((scopeStart, scopeEnd));
        for (var i = from; i < to; i++)
        {
            var t = _t[i];
            if (t.IsPunct('('))
            {
                fromList.Push(false);
                var close = MatchParen(i, to);
                scopes.Push((t.End, close < _t.Count ? _t[close].Start : int.MaxValue));
                continue;
            }
            if (t.IsPunct(')')) { if (fromList.Count > 1) { fromList.Pop(); scopes.Pop(); } continue; }
            if (t.Kind != TokKind.Ident && !t.IsPunct(',')) continue;

            if (t.Is("with"))
            {
                i = ParseWith(i + 1, to);
                continue;
            }
            if (t.Is("from") || t.Is("join") || t.Is("update") || t.Is("into") || (t.Is("table") && IsTableStatement(i)) || t.Is("truncate"))
            {
                if (t.Is("from")) { fromList.Pop(); fromList.Push(true); }
                i = ParseTableRef(i + 1, to, into, scopes.Peek()) - 1;
                continue;
            }
            if (t.IsPunct(',') && fromList.Peek())
            {
                i = ParseTableRef(i + 1, to, into, scopes.Peek()) - 1;
                continue;
            }
            if (t.Kind == TokKind.Ident && EndsFromList(t)) { fromList.Pop(); fromList.Push(false); }
        }
    }

    private bool IsTableStatement(int i) => i == 0 || _t[i - 1].Is("lock") || _t[i - 1].Is("alter") || _t[i - 1].Is("truncate");

    private static bool EndsFromList(Tok t) =>
        t.Is("where") || t.Is("group") || t.Is("order") || t.Is("having") || t.Is("limit") || t.Is("offset") ||
        t.Is("union") || t.Is("intersect") || t.Is("except") || t.Is("window") || t.Is("returning") || t.Is("set") ||
        t.Is("select") || t.Is("fetch") || t.Is("for") || t.Is("values");

    /// <summary>Parses one table reference at <paramref name="i"/>; returns the index after it.</summary>
    private int ParseTableRef(int i, int to, List<StatementRelation> into, (int Start, int End) scope)
    {
        while (i < to && (_t[i].Is("only") || _t[i].Is("lateral") || _t[i].Is("if") || _t[i].Is("exists") || _t[i].Is("not")))
            i++;
        if (i >= to) return i;

        if (_t[i].IsPunct('('))
        {
            var close = MatchParen(i, to);
            var body = (i + 1, close);
            var j = close + 1;
            var (alias, cols, after) = ParseAlias(j, to);
            if (alias is not null)
            {
                var rel = new StatementRelation { Source = RelationSource.Subquery, Name = alias, Alias = alias, ScopeStart = scope.Start, ScopeEnd = scope.End };
                rel.Columns = cols ?? DeriveColumns(body.Item1, body.Item2, out _);
                into.Add(rel);
            }
            // Relations inside the subquery are visible to a caret inside it.
            Run(body.Item1, body.Item2, into, _t[i].End, close < _t.Count ? _t[close].Start : int.MaxValue);
            return after;
        }

        if (!_t[i].IsIdent) return i;
        var parts = new List<string> { _t[i].Value };
        var k = i + 1;
        while (k + 1 < to && _t[k].IsPunct('.') && _t[k + 1].IsIdent)
        {
            parts.Add(_t[k + 1].Value);
            k += 2;
        }
        var source = RelationSource.Table;
        if (k < to && _t[k].IsPunct('('))
        {
            // Function in FROM (generate_series(...)) or INSERT INTO t (cols).
            if (i > 0 && _t[i - 1].Is("into")) source = RelationSource.Table;
            else source = RelationSource.Function;
            k = MatchParen(k, to) + 1;
        }
        var name = parts[^1];
        var schema = parts.Count >= 2 ? parts[^2] : null;
        var (a, c, next) = ParseAlias(k, to);
        // INSERT INTO t (cols) VALUES: the paren list is a column list, not an alias.
        into.Add(new StatementRelation { Source = source, Schema = schema, Name = name, Alias = a, Columns = c, ScopeStart = scope.Start, ScopeEnd = scope.End });
        return next;
    }

    /// <summary>Optional <c>[AS] alias [(col, ...)]</c> at <paramref name="i"/>.</summary>
    private (string? Alias, List<string>? Columns, int Next) ParseAlias(int i, int to)
    {
        var hadAs = false;
        if (i < to && _t[i].Is("as")) { hadAs = true; i++; }
        if (i >= to || !_t[i].IsIdent) return (null, null, i);
        if (!hadAs && _t[i].Kind == TokKind.Ident && SqlKeywords.NotAlias.Contains(_t[i].Value)) return (null, null, i);
        var alias = _t[i].Value;
        i++;
        List<string>? cols = null;
        if (i < to && _t[i].IsPunct('('))
        {
            var close = MatchParen(i, to);
            cols = IdentList(i + 1, close);
            i = close + 1;
        }
        return (alias, cols, i);
    }

    /// <summary>WITH [RECURSIVE] name [(cols)] AS [NOT] [MATERIALIZED] (body) [, ...]. Returns the last index consumed.</summary>
    private int ParseWith(int i, int to)
    {
        if (i < to && _t[i].Is("recursive")) i++;
        while (i < to && _t[i].IsIdent)
        {
            var cte = new CteDef { Name = _t[i].Value };
            i++;
            List<string>? cols = null;
            if (i < to && _t[i].IsPunct('('))
            {
                var close = MatchParen(i, to);
                cols = IdentList(i + 1, close);
                i = close + 1;
            }
            if (i < to && _t[i].Is("as")) i++;
            else return i - 1;
            while (i < to && (_t[i].Is("not") || _t[i].Is("materialized"))) i++;
            if (i >= to || !_t[i].IsPunct('(')) { Ctes[cte.Name] = cte; return i - 1; }
            var end = MatchParen(i, to);
            var derived = DeriveColumns(i + 1, end, out var stars);
            cte.Columns = cols ?? derived;
            if (cols is null) cte.Stars.AddRange(stars);
            Ctes[cte.Name] = cte;
            // Relations of the CTE body are visible to a caret inside the body.
            Run(i + 1, end, Relations, _t[i].End, end < _t.Count ? _t[end].Start : int.MaxValue);
            i = end + 1;
            if (i < to && _t[i].IsPunct(',')) { i++; continue; }
            return i - 1;
        }
        return i - 1;
    }

    /// <summary>Output column names of the first SELECT in [from, to) (aliases, bare columns, function names).</summary>
    private List<string> DeriveColumns(int from, int to, out List<(string? Qualifier, List<StatementRelation> Relations)> stars)
    {
        stars = [];
        var cols = new List<string>();
        var s = from;
        while (s < to && !_t[s].Is("select")) { if (_t[s].IsPunct('(')) s = MatchParen(s, to); s++; }
        if (s >= to) return cols;
        s++;
        while (s < to && (_t[s].Is("distinct") || _t[s].Is("all"))) s++;
        if (s < to && _t[s].Is("on") && s + 1 < to && _t[s + 1].IsPunct('(')) s = MatchParen(s + 1, to) + 1;

        List<StatementRelation>? bodyRelations = null;
        var itemStart = s;
        for (var i = s; i <= to; i++)
        {
            var atEnd = i == to || _t[i].IsPunct(',') || _t[i].Kind == TokKind.Ident && (_t[i].Is("from") || _t[i].Is("where") || _t[i].Is("union") || _t[i].Is("group") || _t[i].Is("order") || _t[i].Is("limit") || _t[i].Is("into"));
            if (i < to && _t[i].IsPunct('(')) { i = MatchParen(i, to); continue; }
            if (!atEnd) continue;
            if (i > itemStart)
            {
                var name = ItemName(itemStart, i, out var star, out var starQualifier);
                if (star)
                {
                    bodyRelations ??= CollectBodyRelations(i, to);
                    stars.Add((starQualifier, bodyRelations));
                }
                else if (name is not null) cols.Add(name);
            }
            if (i == to || !_t[i].IsPunct(',')) break;
            itemStart = i + 1;
        }
        return cols;
    }

    private List<StatementRelation> CollectBodyRelations(int from, int to)
    {
        var list = new List<StatementRelation>();
        Run(from, to, list, 0, int.MaxValue);
        return list;
    }

    /// <summary>The output name of a select item in [from, to).</summary>
    private string? ItemName(int from, int to, out bool star, out string? starQualifier)
    {
        star = false;
        starQualifier = null;
        var n = to - from;
        var last = _t[to - 1];
        if (last.IsPunct('*'))
        {
            star = true;
            if (n >= 3 && _t[to - 2].IsPunct('.') && _t[to - 3].IsIdent) starQualifier = _t[to - 3].Value;
            return null;
        }
        if (n >= 2 && last.IsIdent && _t[to - 2].Is("as")) return last.Value;
        // a | a.b | a.b.c
        var allChain = true;
        for (var i = from; i < to; i++)
        {
            var expectIdent = (i - from) % 2 == 0;
            if (expectIdent ? !_t[i].IsIdent : !_t[i].IsPunct('.')) { allChain = false; break; }
        }
        if (allChain && n % 2 == 1) return last.Value;
        // expr alias  (implicit alias after a complete expression)
        if (n >= 2 && last.IsIdent && !SqlKeywords.NotAlias.Contains(last.Value))
        {
            var prev = _t[to - 2];
            if (prev.IsPunct(')') || prev.Kind is TokKind.String or TokKind.Number || prev.IsIdent) return last.Value;
        }
        // fn(...) → fn
        if (_t[from].IsIdent && from + 1 < to && _t[from + 1].IsPunct('(') && MatchParen(from + 1, to) == to - 1) return _t[from].Value;
        // x::type → x
        if (n >= 3 && _t[from].IsIdent && _t[from + 1].Value == "::") return _t[from].Value;
        return null;
    }

    private List<string> IdentList(int from, int to)
    {
        var list = new List<string>();
        for (var i = from; i < to; i++)
            if (_t[i].IsIdent && (i == from || _t[i - 1].IsPunct(','))) list.Add(_t[i].Value);
        return list;
    }

    /// <summary>Index of the ')' matching the '(' at <paramref name="open"/>, or <paramref name="to"/> when unbalanced.</summary>
    private int MatchParen(int open, int to)
    {
        var depth = 0;
        for (var i = open; i < to; i++)
        {
            if (_t[i].IsPunct('(')) depth++;
            else if (_t[i].IsPunct(')') && --depth == 0) return i;
        }
        return to;
    }
}
