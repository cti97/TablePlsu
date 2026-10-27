using TablePlsu.Core.Metadata;
using TablePlsu.Core.Search;
using TablePlsu.Core.Sql;

namespace TablePlsu.Core.Completion;

/// <summary>
/// SQL autocomplete: a pure function of (text, caret, metadata snapshot, options) to a ranked list of suggestions.
/// <list type="bullet">
/// <item>Context comes from a lightweight lexer (strings, comments and quoted identifiers are skipped) over the statement
/// that contains the caret: after FROM/JOIN/UPDATE/INTO/TABLE → tables; after <c>schema.</c> → that schema's objects;
/// after <c>alias.</c> / <c>table.</c> / <c>cte.</c> → columns; in SELECT/WHERE/ON/ORDER BY/GROUP BY/HAVING/SET →
/// columns of the statement's relations + functions + keywords; otherwise keywords + tables.</item>
/// <item>Ranking: case-sensitive prefix, then case-insensitive prefix, then fuzzy (<see cref="FuzzyMatcher"/>); inside a
/// match class, the context's kind order (columns before functions before keywords in column contexts), then exact
/// name, search-path schemas, shorter names and alphabetical order.</item>
/// </list>
/// Thread-safe and allocation-light; metadata snapshots are immutable.
/// </summary>
public static class CompletionEngine
{
    public static CompletionResult Complete(string text, int caret, DbMetadata? metadata, CompletionOptions? options = null)
    {
        options ??= CompletionOptions.Default;
        caret = Math.Clamp(caret, 0, text.Length);
        if (options.Disabled) return CompletionResult.None(caret);
        metadata ??= DbMetadata.Empty();

        var all = SqlLexer.Lex(text);

        // Caret inside a string / comment / number → nothing.
        var quoted = false;
        var wordStart = caret;
        foreach (var t in all)
        {
            if (t.Start >= caret) break;
            var inside = caret < t.End || caret == t.End && t.Unterminated;
            if (!inside) continue;
            switch (t.Kind)
            {
                case TokKind.String or TokKind.Comment or TokKind.Param:
                    return CompletionResult.None(caret);
                case TokKind.Number when caret <= t.End:
                    return CompletionResult.None(caret);
                case TokKind.QuotedIdent:
                    quoted = true;
                    wordStart = t.Start;
                    break;
            }
        }
        if (!quoted)
        {
            while (wordStart > 0 && SqlLexer.IsIdentPart(text[wordStart - 1])) wordStart--;
            if (wordStart < caret && !SqlLexer.IsIdentStart(text[wordStart])) return CompletionResult.None(caret);
        }
        var prefix = quoted ? text.Substring(wordStart + 1, caret - wordStart - 1) : text[wordStart..caret];

        // The statement around the caret: between the top-level ';' before and after it.
        int stmtFrom = 0, stmtTo = all.Count;
        for (var i = 0; i < all.Count; i++)
        {
            if (!all[i].IsPunct(';')) continue;
            if (all[i].End <= wordStart) stmtFrom = i + 1;
            else { stmtTo = i; break; }
        }
        var stmt = new List<Tok>(stmtTo - stmtFrom);
        var before = new List<Tok>();
        for (var i = stmtFrom; i < stmtTo; i++)
        {
            var t = all[i];
            if (t.Kind == TokKind.Comment) continue;
            if (t.Start >= wordStart && t.End <= caret && t.Start < caret) continue; // the word being typed
            stmt.Add(t);
            if (t.End <= wordStart) before.Add(t);
        }

        var ctx = new Ctx(metadata, options, prefix, quoted, wordStart, StatementAnalyzer.Analyze(stmt));

        // Qualifier chain: ident . ident . <word>
        var qualifier = new List<string>();
        var b = before.Count - 1;
        while (b >= 1 && before[b].IsPunct('.') && before[b - 1].IsIdent)
        {
            qualifier.Insert(0, before[b - 1].Value);
            b -= 2;
        }
        var clauseEnd = b; // last token before the qualifier chain / word

        var kind = DetectContext(before, clauseEnd, out var insertTarget);
        List<Scored> scored;
        if (qualifier.Count > 0)
        {
            scored = ctx.Member(qualifier, kind);
            kind = CompletionContextKind.Member;
        }
        else
        {
            scored = kind switch
            {
                CompletionContextKind.None => [],
                CompletionContextKind.Table => ctx.TableContext(),
                CompletionContextKind.Column => ctx.ColumnContext(),
                CompletionContextKind.InsertColumns => ctx.InsertColumns(insertTarget!.Value),
                _ => ctx.DefaultContext(),
            };
        }

        return new CompletionResult
        {
            Context = kind,
            Items = Rank(scored, options.MaxItems, prefix, ctx),
            ReplaceStart = wordStart,
            ReplaceLength = caret - wordStart,
            Prefix = prefix,
            Qualifier = qualifier,
            MissingSchemas = ctx.Missing.Count == 0 ? [] : [.. ctx.Missing],
        };
    }

    /// <summary>Walks back from <paramref name="last"/> to the clause keyword that decides the context.</summary>
    private static CompletionContextKind DetectContext(List<Tok> tokens, int last, out (string? Schema, string Name)? insertTarget)
    {
        insertTarget = null;
        if (last < 0) return CompletionContextKind.Default;
        var prev = tokens[last];
        if (prev.Is("as")) return CompletionContextKind.None;            // naming an alias

        var depth = 0;
        var sawComma = false;
        for (var j = last; j >= 0; j--)
        {
            var t = tokens[j];
            if (t.IsPunct(')')) { depth++; continue; }
            if (t.IsPunct('('))
            {
                if (depth > 0) { depth--; continue; }
                // Opened here and still open: INSERT INTO t ( → columns of t; otherwise keep looking outward.
                var k = j - 1;
                var parts = new List<string>();
                while (k >= 0 && tokens[k].IsIdent)
                {
                    parts.Insert(0, tokens[k].Value);
                    if (k >= 1 && tokens[k - 1].IsPunct('.')) k -= 2;
                    else { k--; break; }
                }
                if (parts.Count > 0 && k >= 0 && tokens[k].Is("into"))
                {
                    insertTarget = (parts.Count >= 2 ? parts[^2] : null, parts[^1]);
                    return CompletionContextKind.InsertColumns;
                }
                if (j == last) return CompletionContextKind.Column;     // fn( | or ( |
                continue;
            }
            if (depth > 0) continue;
            if (t.IsPunct(',')) { sawComma = true; continue; }
            if (t.Kind != TokKind.Ident) continue;

            var w = t.Value.ToLowerInvariant();
            switch (w)
            {
                case "from" or "join" or "update" or "into" or "table" or "truncate" or "only" or "lateral":
                    if (j == last) return CompletionContextKind.Table;
                    if (w == "from" && sawComma && tokens[last].IsPunct(',')) return CompletionContextKind.Table;
                    return CompletionContextKind.Default;
                case "select" or "where" or "on" or "by" or "having" or "set" or "returning" or "and" or "or" or "not"
                    or "case" or "when" or "then" or "else" or "distinct" or "using" or "values" or "in" or "between"
                    or "like" or "ilike" or "is":
                    return CompletionContextKind.Column;
                case "limit" or "offset":
                    return CompletionContextKind.Default;
            }
        }
        return HasOperatorBefore(tokens, last) ? CompletionContextKind.Column : CompletionContextKind.Default;
    }

    private static bool HasOperatorBefore(List<Tok> tokens, int last) =>
        last >= 0 && tokens[last].Kind == TokKind.Punct && tokens[last].Value is "=" or "<" or ">" or "+" or "-" or "*" or "/" or "||" or "::";

    // ───────────────────────── ranking ─────────────────────────

    /// <summary>A candidate with its ranking keys.</summary>
    /// <remarks><see cref="Item"/> is null for a table or function whose item is built only if it makes the cut
    /// (<see cref="Table"/>, or <see cref="Function"/> in schema <see cref="FnSchema"/>): most candidates never do.</remarks>
    private readonly record struct Scored(CompletionItem? Item, int Class, int KindOrder, bool Exact, int SubOrder, int Fuzzy, string MatchName,
        TableInfo? Table = null, bool Qualify = false, FunctionInfo? Function = null, string? FnSchema = null);

    private const int FuzzyPending = int.MinValue;

    private static IReadOnlyList<CompletionItem> Rank(List<Scored> list, int max, string prefix, Ctx ctx)
    {
        var noPrefix = prefix.Length == 0;
        var limit = max <= 0 ? list.Count : max;
        var prefixHits = 0;
        var pending = false;
        foreach (var x in list)
        {
            if (x.Class >= 2) prefixHits++;
            else if (x.Fuzzy == FuzzyPending) pending = true;
        }
        if (pending)
        {
            if (prefixHits >= limit) list.RemoveAll(static x => x.Fuzzy == FuzzyPending);
            else
            {
                for (var i = 0; i < list.Count; i++)
                    if (list[i].Fuzzy == FuzzyPending)
                        list[i] = list[i] with { Fuzzy = FuzzyMatcher.Match(prefix, list[i].MatchName)?.Score ?? int.MinValue / 2 };
            }
        }

        Comparison<Scored> compare = (a, b) =>
        {
            var c = b.Class.CompareTo(a.Class);
            if (c != 0) return c;
            c = b.Exact.CompareTo(a.Exact);
            if (c != 0) return c;
            c = a.KindOrder.CompareTo(b.KindOrder);
            if (c != 0) return c;
            c = b.Fuzzy.CompareTo(a.Fuzzy);
            if (c != 0) return c;
            c = a.SubOrder.CompareTo(b.SubOrder);
            if (c != 0) return c;
            if (!noPrefix)
            {
                c = a.MatchName.Length.CompareTo(b.MatchName.Length);
                if (c != 0) return c;
            }
            c = string.Compare(a.MatchName, b.MatchName, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.MatchName, b.MatchName);
            return c != 0 ? c : string.CompareOrdinal(TieKey(a), TieKey(b));
        };
        var n = Math.Min(max <= 0 ? list.Count : max, list.Count);
        if (list.Count > n * 2)
        {
            // Top-n selection: a heap of the n best with the worst on top; most candidates lose one comparison.
            var heap = new PriorityQueue<Scored, Scored>(n + 1, Comparer<Scored>.Create((a, b) => compare(b, a)));
            foreach (var x in list)
            {
                if (heap.Count < n) heap.Enqueue(x, x);
                else if (compare(x, heap.Peek()) < 0) heap.DequeueEnqueue(x, x);
            }
            list = new List<Scored>(n);
            foreach (var (e, _) in heap.UnorderedItems) list.Add(e);
        }
        list.Sort(compare);
        var result = new CompletionItem[n];
        for (var i = 0; i < n; i++)
        {
            var x = list[i];
            result[i] = x.Item ?? (x.Table is not null ? ctx.TableItem(x.Table, x.Qualify) : ctx.FunctionItem(x.Function!, x.FnSchema!, x.Qualify));
        }
        return result;
    }

    /// <summary>The last tie-breaker: the item's detail (built for a deferred function only when a tie gets this far).</summary>
    private static string? TieKey(in Scored x) =>
        x.Item?.Detail ?? x.Table?.Schema ?? (x.Function is { } f ? Ctx.FunctionDetail(f, x.FnSchema!) : null);

    /// <summary>Per-request state: metadata, options, the typed prefix and the statement analysis.</summary>
    private sealed class Ctx(DbMetadata md, CompletionOptions o, string prefix, bool quoted, int caret, StatementAnalyzer an)
    {
        public readonly HashSet<string> Missing = new(StringComparer.Ordinal);
        private readonly bool _fuzzy = prefix.Length > 0 && prefix.IndexOfAny(['^', '$']) < 0;
        private readonly HashSet<string> _searchPath = new(md.EffectiveSearchPath, StringComparer.Ordinal);

        /// <summary>Match class: 3 = case-sensitive prefix, 2 = case-insensitive prefix, 1 = fuzzy, 0 = no match.</summary>
        private int Match(string name, bool caseless, out int fuzzy)
        {
            fuzzy = 0;
            if (prefix.Length == 0) return 3;
            if (name.StartsWith(prefix, StringComparison.Ordinal)) return 3;
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return caseless ? 3 : 2;
            if (!_fuzzy) return 0;
            if (prefix.Length == 1)
            {
                // One character: a cheap "contains" (earlier is better) instead of the fuzzy DP.
                var at = name.IndexOf(prefix[0]);
                if (at < 0) at = name.IndexOf(char.IsUpper(prefix[0]) ? char.ToLowerInvariant(prefix[0]) : char.ToUpperInvariant(prefix[0]));
                if (at < 0) return 0;
                fuzzy = -at;
                return 1;
            }
            // Subsequence check now; the FuzzyMatcher score is computed in Rank only when fuzzy hits can make the cut.
            if (!IsSubsequence(prefix, name)) return 0;
            fuzzy = FuzzyPending;
            return 1;
        }

        private static bool IsSubsequence(string pattern, string candidate)
        {
            var pi = 0;
            for (var ci = 0; ci < candidate.Length && pi < pattern.Length; ci++)
                if (char.ToLowerInvariant(pattern[pi]) == char.ToLowerInvariant(candidate[ci])) pi++;
            return pi == pattern.Length;
        }

        private int _cls, _fz;

        /// <summary>Matches <paramref name="name"/> and remembers the result for <see cref="AddMatched"/> (so items are
        /// only built for candidates that match — the big lists are tables and functions).</summary>
        private bool Matches(string name, bool caseless = false)
        {
            _cls = Match(name, caseless, out _fz);
            return _cls != 0;
        }

        private void AddMatched(List<Scored> list, CompletionItem item, string matchName, int kindOrder, int subOrder)
        {
            var exact = prefix.Length > 0 && matchName.Length == prefix.Length;
            list.Add(new Scored(item, _cls, kindOrder, exact, subOrder, _fz, matchName));
        }

        private void AddTable(List<Scored> list, TableInfo t, bool qualify, int kindOrder, int subOrder)
        {
            var exact = prefix.Length > 0 && t.Name.Length == prefix.Length;
            list.Add(new Scored(null, _cls, kindOrder, exact, subOrder, _fz, t.Name, t, qualify));
        }

        private void Add(List<Scored> list, CompletionItem item, string matchName, int kindOrder, int subOrder = 0, bool caseless = false)
        {
            if (Matches(matchName, caseless)) AddMatched(list, item, matchName, kindOrder, subOrder);
        }

        private string Space => o.AddSpace ? " " : "";

        private string Ident(string name) => quoted ? SqlIdent.Quote(name) : SqlIdent.QuoteIfNeeded(name);

        // ── item factories ──

        private void AddKeywords(List<Scored> list, int order)
        {
            if (!o.Keywords) return;
            foreach (var k in SqlKeywords.All)
            {
                var word = o.UppercaseKeywords ? k : k.ToLowerInvariant();
                Add(list, new CompletionItem(CompletionKind.Keyword, word, word + Space, "keyword"), k, order, caseless: true);
            }
        }

        private void AddTables(List<Scored> list, int order, SchemaInfo? only = null)
        {
            if (!o.Tables) return;
            if (only is not null)
            {
                if (!only.IsLoaded) Missing.Add(only.Name);
                foreach (var t in only.Tables)
                    if (Matches(t.Name)) AddTable(list, t, qualify: false, order, 0);
                return;
            }
            foreach (var s in md.Schemas)
            {
                var sub = _searchPath.Contains(s.Name) ? 0 : 1;
                var tables = s.Tables;
                for (var i = 0; i < tables.Count; i++)
                {
                    var t = tables[i];
                    if (Matches(t.Name)) AddTable(list, t, o.PrefixSchema && t.Schema != "public", order, sub);
                }
            }
        }

        public CompletionItem FunctionItem(FunctionInfo f, string schema, bool qualify)
        {
            var insert = (qualify ? Ident(schema) + "." : "") + Ident(f.Name) + "()";
            return new CompletionItem(CompletionKind.Function, f.Name + "(" + f.Arguments + ")", insert, FunctionDetail(f, schema))
            { CaretBack = f.Arguments.Length > 0 ? 1 : 0, Schema = schema };
        }

        public static string FunctionDetail(FunctionInfo f, string schema) => (f.ReturnType is null ? "procedure" : "→ " + f.ReturnType) + " · " + schema;

        public CompletionItem TableItem(TableInfo t, bool qualify)
        {
            var insert = (qualify ? Ident(t.Schema) + "." : "") + Ident(t.Name) + Space;
            if (qualify && quoted) insert = SqlIdent.QuoteIfNeeded(t.Schema) + "." + SqlIdent.Quote(t.Name) + Space;
            var kind = t.IsView ? CompletionKind.View : CompletionKind.Table;
            var what = t.Kind switch
            {
                RelationKind.View => "view",
                RelationKind.MaterializedView => "materialized view",
                RelationKind.ForeignTable => "foreign table",
                RelationKind.PartitionedTable => "partitioned table",
                _ => "table",
            };
            return new CompletionItem(kind, t.Name, insert, $"{what} · {t.Schema}") { Schema = t.Schema };
        }

        private void AddSchemas(List<Scored> list, int order)
        {
            if (!o.Tables) return;
            foreach (var s in md.Schemas)
                Add(list, new CompletionItem(CompletionKind.Schema, s.Name, Ident(s.Name) + ".", "schema"), s.Name, order, s.IsSystem ? 1 : 0);
        }

        private void AddCtes(List<Scored> list, int order)
        {
            foreach (var c in an.Ctes.Values)
                Add(list, new CompletionItem(CompletionKind.Cte, c.Name, Ident(c.Name) + Space, "CTE"), c.Name, order);
        }

        private void AddFunctions(List<Scored> list, int order, SchemaInfo? only = null)
        {
            if (!o.Functions) return;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IEnumerable<SchemaInfo> schemas = only is null ? md.Schemas : [only];
            foreach (var s in schemas)
            {
                var sub = _searchPath.Contains(s.Name) || s.Name == "pg_catalog" ? 0 : 1;
                seen.Clear();
                var fns = s.Functions;
                for (var i = 0; i < fns.Count; i++)
                {
                    var f = fns[i];
                    if (f.Kind == RoutineKind.Procedure && only is null) continue;
                    if (!seen.Add(f.Name)) continue; // overloads: one item per schema + name
                    names.Add(f.Name);
                    if (!Matches(f.Name)) continue;
                    var qualify = only is null && sub == 1 && o.PrefixSchema && s.Name != "public";
                    var exact = prefix.Length > 0 && f.Name.Length == prefix.Length;
                    list.Add(new Scored(null, _cls, order, exact, sub, _fz, f.Name, Qualify: qualify, Function: f, FnSchema: s.Name));
                }
            }
            if (only is not null) return;
            foreach (var (name, args) in SqlKeywords.BuiltinFunctions)
            {
                if (!names.Add(name)) continue;
                var item = new CompletionItem(CompletionKind.Function, name + "(" + args + ")", name + "()", "built-in")
                { CaretBack = args.Length > 0 ? 1 : 0 };
                Add(list, item, name, order, 0);
            }
        }

        private void AddColumns(List<Scored> list, int order, StatementRelation rel, bool showOwner, int relIndex = 0)
        {
            var owner = showOwner ? rel.RefName : null;
            if (ColumnsOf(rel) is { } cols) AddColumnList(list, order, cols, owner, relIndex);
        }

        /// <summary>Columns keep their table order (relation index, then ordinal) among equally good matches.</summary>
        private void AddColumnList(List<Scored> list, int order, List<(string Name, string? Type)> cols, string? owner, int relIndex = 0, bool space = true)
        {
            for (var i = 0; i < cols.Count; i++)
            {
                var item = ColumnItem(cols[i].Name, cols[i].Type, owner);
                if (!space) item = item with { InsertText = Ident(cols[i].Name) };
                Add(list, item, cols[i].Name, order, relIndex * 10000 + i);
            }
        }

        private CompletionItem ColumnItem(string name, string? type, string? owner)
        {
            var detail = type is null ? owner : owner is null ? type : type + " · " + owner;
            return new CompletionItem(CompletionKind.Column, name, Ident(name) + Space, detail);
        }

        /// <summary>Columns of a statement relation: explicit list, CTE, subquery, or the metadata table.</summary>
        private List<(string Name, string? Type)>? ColumnsOf(StatementRelation rel, int depth = 0)
        {
            if (depth > 4) return null;
            if (rel.Source == RelationSource.Table && rel.Schema is null && an.Ctes.TryGetValue(rel.Name, out var cte))
            {
                var cols = CteColumns(cte, depth);
                if (rel.Columns is { Count: > 0 } renamed) return Rename(cols, renamed);
                return cols;
            }
            if (rel.Source is RelationSource.Subquery or RelationSource.Function) return rel.Columns?.Select(c => (c, (string?)null)).ToList();
            var table = FindTable(rel.Schema, rel.Name);
            if (table is null) return rel.Columns?.Select(c => (c, (string?)null)).ToList();
            var tcols = table.Columns.Select(c => (c.Name, (string?)c.DataType)).ToList();
            return rel.Columns is { Count: > 0 } aliasCols && rel.Alias is not null ? Rename(tcols, aliasCols) : tcols;
        }

        private static List<(string, string?)> Rename(List<(string Name, string? Type)> cols, IReadOnlyList<string> names)
        {
            var result = new List<(string, string?)>();
            for (var i = 0; i < cols.Count; i++) result.Add((i < names.Count ? names[i] : cols[i].Name, cols[i].Type));
            for (var i = cols.Count; i < names.Count; i++) result.Add((names[i], null));
            return result;
        }

        private List<(string Name, string? Type)> CteColumns(CteDef cte, int depth)
        {
            var cols = cte.Columns.Select(c => (c, (string?)null)).ToList();
            foreach (var (qualifier, rels) in cte.Stars)
            {
                foreach (var r in rels)
                {
                    if (qualifier is not null && !string.Equals(r.RefName, qualifier, StringComparison.OrdinalIgnoreCase)) continue;
                    if (r.Source == RelationSource.Table && r.Schema is null && string.Equals(r.Name, cte.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (ColumnsOf(r, depth + 1) is { } more) cols.AddRange(more);
                }
            }
            return cols;
        }

        private TableInfo? FindTable(string? schema, string name)
        {
            if (schema is not null && md.FindSchema(schema) is { IsLoaded: false } s) Missing.Add(s.Name);
            return md.FindTable(schema, name);
        }

        // ── contexts ──

        public List<Scored> DefaultContext()
        {
            var list = new List<Scored>();
            AddKeywords(list, 0);
            AddCtes(list, 1);
            AddTables(list, 1);
            AddSchemas(list, 2);
            return list;
        }

        public List<Scored> TableContext()
        {
            var list = new List<Scored>();
            AddCtes(list, 0);
            AddTables(list, 1);
            AddSchemas(list, 2);
            return list;
        }

        public List<Scored> ColumnContext()
        {
            var list = new List<Scored>();
            var rels = DistinctRelations();
            var showOwner = rels.Count > 1;
            for (var i = 0; i < rels.Count; i++) AddColumns(list, 0, rels[i], showOwner, i);
            foreach (var r in rels)
            {
                if (r.Source == RelationSource.Function && r.Alias is null) continue;
                var detail = r.Alias is not null && r.Source == RelationSource.Table ? (r.Schema is null ? r.Name : r.Schema + "." + r.Name) : r.Source.ToString().ToLowerInvariant();
                Add(list, new CompletionItem(r.Source == RelationSource.Table && an.Ctes.ContainsKey(r.Name) && r.Alias is null ? CompletionKind.Cte : CompletionKind.Alias,
                    r.RefName, Ident(r.RefName), detail), r.RefName, 1);
            }
            AddFunctions(list, 2);
            AddKeywords(list, 3);
            return list;
        }

        public List<Scored> InsertColumns((string? Schema, string Name) target)
        {
            var list = new List<Scored>();
            var rel = new StatementRelation { Schema = target.Schema, Name = target.Name };
            if (ColumnsOf(rel) is { } cols) AddColumnList(list, 0, cols, null, space: false);
            return list;
        }

        /// <summary><c>q.</c> / <c>s.t.</c> completion.</summary>
        public List<Scored> Member(List<string> q, CompletionContextKind clause)
        {
            var list = new List<Scored>();
            if (q.Count >= 2)
            {
                var rel = new StatementRelation { Schema = q[^2], Name = q[^1] };
                if (ColumnsOf(rel) is { } cols) AddColumnList(list, 0, cols, null);
                return list;
            }

            var name = q[0];
            var tableClause = clause is CompletionContextKind.Table or CompletionContextKind.Default;
            var schema = md.FindSchema(name);
            if (tableClause && schema is not null) return SchemaObjects(schema, includeFunctions: false);

            // alias / table name / CTE referenced in this statement
            var rel2 = FindRelation(name, visibleOnly: true) ?? FindRelation(name, visibleOnly: false);
            if (rel2 is null && an.Ctes.ContainsKey(name)) rel2 = new StatementRelation { Name = name };
            if (rel2 is not null && ColumnsOf(rel2) is { Count: > 0 } relCols)
            {
                AddColumnList(list, 0, relCols, null);
                return list;
            }
            if (schema is not null) return SchemaObjects(schema, includeFunctions: clause != CompletionContextKind.Table);
            if (FindTable(null, name) is { } t) AddColumnList(list, 0, [.. t.Columns.Select(c => (c.Name, (string?)c.DataType))], null);
            return list;
        }

        private List<Scored> SchemaObjects(SchemaInfo schema, bool includeFunctions)
        {
            var list = new List<Scored>();
            AddTables(list, 0, schema);
            if (includeFunctions) AddFunctions(list, 1, schema);
            return list;
        }

        private StatementRelation? FindRelation(string name, bool visibleOnly)
        {
            foreach (var r in an.Relations)
                if ((!visibleOnly || r.VisibleAt(caret)) && string.Equals(r.Alias, name, StringComparison.OrdinalIgnoreCase)) return r;
            foreach (var r in an.Relations)
                if ((!visibleOnly || r.VisibleAt(caret)) && r.Alias is null && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)) return r;
            return null;
        }

        /// <summary>Relations visible at the caret, de-duplicated by the name they are referred to by.</summary>
        private List<StatementRelation> DistinctRelations()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<StatementRelation>();
            foreach (var r in an.Relations)
                if (r.VisibleAt(caret) && seen.Add(r.RefName)) result.Add(r);
            return result;
        }
    }
}
