using System.Globalization;
using System.Text;
using TablePlsu.Core.Sql;

namespace TablePlsu.Core.Data;

public enum SortDirection { Asc, Desc }

/// <summary>One ORDER BY term.</summary>
public sealed record SortSpec(string Column, SortDirection Direction);

/// <summary>A column of the table being queried (name + format_type data type, for casts and "Any column").</summary>
public sealed record QueryColumn(string Name, string? DataType = null);

/// <summary>Everything that shapes a table data query.</summary>
public sealed record TableQuery
{
    public required string Schema { get; init; }
    public required string Table { get; init; }

    /// <summary>All columns of the table, in order. Used for "Any column" and the select list. Empty = <c>SELECT *</c>.</summary>
    public IReadOnlyList<QueryColumn> Columns { get; init; } = [];

    /// <summary>Optional narrower select list (column chooser). Null = all <see cref="Columns"/>.</summary>
    public IReadOnlyList<string>? SelectColumns { get; init; }

    public IReadOnlyList<FilterCondition> Filters { get; init; } = [];
    public FilterJoin Join { get; init; } = FilterJoin.And;
    public IReadOnlyList<SortSpec> Sorts { get; init; } = [];
    /// <summary>LIMIT (null or ≤ 0 = none).</summary>
    public int? Limit { get; init; }
    public long Offset { get; init; }
}

/// <summary>
/// Builds the table data view's SQL: <c>SELECT cols FROM schema.table WHERE … ORDER BY … LIMIT n OFFSET m</c>.
/// Filter values are always parameters (<c>$n</c>), never concatenated, except the Raw SQL filter which is inserted
/// verbatim (wrapped in parentheses). Pattern operators escape LIKE wildcards in the value.
/// </summary>
public static class TableQueryBuilder
{
    public static SqlCommandSpec BuildSelect(TableQuery q)
    {
        var b = new SqlBuilder();
        b.Append("SELECT ");
        var cols = q.SelectColumns ?? (q.Columns.Count > 0 ? q.Columns.Select(c => c.Name).ToList() : null);
        if (cols is null || cols.Count == 0) b.Append("*");
        else b.Append(string.Join(", ", cols.Select(SqlIdent.QuoteIfNeeded)));
        b.Append(" FROM ").Append(SqlIdent.Qualified(q.Schema, q.Table));
        AppendWhere(b, q);
        if (q.Sorts.Count > 0)
        {
            b.Append(" ORDER BY ");
            b.Append(string.Join(", ", q.Sorts.Select(s => SqlIdent.QuoteIfNeeded(s.Column) + (s.Direction == SortDirection.Desc ? " DESC" : " ASC"))));
        }
        if (q.Limit is > 0) b.Append(" LIMIT ").Append(q.Limit.Value.ToString(CultureInfo.InvariantCulture));
        if (q.Offset > 0) b.Append(" OFFSET ").Append(q.Offset.ToString(CultureInfo.InvariantCulture));
        return b.Build();
    }

    /// <summary><c>SELECT count(*) FROM schema.table WHERE …</c> (same filters, no sort/paging).</summary>
    public static SqlCommandSpec BuildCount(TableQuery q)
    {
        var b = new SqlBuilder();
        b.Append("SELECT count(*) FROM ").Append(SqlIdent.Qualified(q.Schema, q.Table));
        AppendWhere(b, q);
        return b.Build();
    }

    /// <summary>Just the WHERE clause ("WHERE …", or empty when no filter applies). For the filter bar's SQL button.</summary>
    public static SqlCommandSpec BuildWhere(TableQuery q)
    {
        var b = new SqlBuilder();
        AppendWhere(b, q);
        var spec = b.Build();
        return spec with { Sql = spec.Sql.TrimStart(), LiteralSql = spec.LiteralSql.TrimStart() };
    }

    private static void AppendWhere(SqlBuilder b, TableQuery q)
    {
        var active = q.Filters.Where(IsApplicable).ToList();
        if (active.Count == 0) return;
        b.Append(" WHERE ");
        var joiner = q.Join == FilterJoin.Or ? " OR " : " AND ";
        for (var i = 0; i < active.Count; i++)
        {
            if (i > 0) b.Append(joiner);
            if (active.Count > 1) b.Append("(");
            AppendCondition(b, active[i], q);
            if (active.Count > 1) b.Append(")");
        }
    }

    /// <summary>Enabled, and complete enough to produce SQL.</summary>
    public static bool IsApplicable(FilterCondition f)
    {
        if (!f.Enabled) return false;
        return f.Target switch
        {
            FilterTarget.RawSql => !string.IsNullOrWhiteSpace(f.Value),
            FilterTarget.Column => !string.IsNullOrEmpty(f.Column),
            _ => true,
        };
    }

    private static void AppendCondition(SqlBuilder b, FilterCondition f, TableQuery q)
    {
        var columns = q.Columns;
        switch (f.Target)
        {
            case FilterTarget.RawSql:
                b.Append(f.Value!.Trim().TrimEnd(';'));
                return;
            case FilterTarget.Column:
            {
                var type = columns.FirstOrDefault(c => c.Name == f.Column)?.DataType;
                AppendPredicate(b, SqlIdent.QuoteIfNeeded(f.Column!), type, f, castToText: false);
                return;
            }
            default:
            {
                if (columns.Count == 0)
                {
                    // No column list: compare the whole row (the table name is its row value) as text.
                    AppendPredicate(b, SqlIdent.QuoteIfNeeded(q.Table), null, f, castToText: true);
                    return;
                }
                var joiner = FilterOperators.IsNegative(f.Operator) ? " AND " : " OR ";
                for (var i = 0; i < columns.Count; i++)
                {
                    if (i > 0) b.Append(joiner);
                    AppendPredicate(b, SqlIdent.QuoteIfNeeded(columns[i].Name), columns[i].DataType, f, castToText: true);
                }
                return;
            }
        }
    }

    private static void AppendPredicate(SqlBuilder b, string column, string? dataType, FilterCondition f, bool castToText)
    {
        var op = f.Operator;
        var asText = (castToText || FilterOperators.IsPattern(op)) && !IsTextType(dataType);
        var expr = asText ? column + "::text" : column;
        var value = f.Value ?? "";
        switch (op)
        {
            case FilterOperator.IsNull:
                b.Append(expr).Append(" IS NULL");
                return;
            case FilterOperator.IsNotNull:
                b.Append(expr).Append(" IS NOT NULL");
                return;
            case FilterOperator.Equal: Binary("="); return;
            case FilterOperator.NotEqual: Binary("<>"); return;
            case FilterOperator.Less: Binary("<"); return;
            case FilterOperator.Greater: Binary(">"); return;
            case FilterOperator.LessOrEqual: Binary("<="); return;
            case FilterOperator.GreaterOrEqual: Binary(">="); return;
            case FilterOperator.Like: b.Append(expr).Append(" LIKE ").AppendValue(value); return;
            case FilterOperator.ILike: b.Append(expr).Append(" ILIKE ").AppendValue(value); return;
            case FilterOperator.Contains: b.Append(expr).Append(" LIKE ").AppendValue("%" + EscapeLike(value) + "%"); return;
            case FilterOperator.NotContains: b.Append(expr).Append(" NOT LIKE ").AppendValue("%" + EscapeLike(value) + "%"); return;
            case FilterOperator.HasPrefix: b.Append(expr).Append(" LIKE ").AppendValue(EscapeLike(value) + "%"); return;
            case FilterOperator.HasSuffix: b.Append(expr).Append(" LIKE ").AppendValue("%" + EscapeLike(value)); return;
            case FilterOperator.IContains: b.Append(expr).Append(" ILIKE ").AppendValue("%" + EscapeLike(value) + "%"); return;
            case FilterOperator.INotContains: b.Append(expr).Append(" NOT ILIKE ").AppendValue("%" + EscapeLike(value) + "%"); return;
            case FilterOperator.IHasPrefix: b.Append(expr).Append(" ILIKE ").AppendValue(EscapeLike(value) + "%"); return;
            case FilterOperator.IHasSuffix: b.Append(expr).Append(" ILIKE ").AppendValue("%" + EscapeLike(value)); return;
            case FilterOperator.In:
            case FilterOperator.NotIn:
            {
                var items = SplitList(value);
                if (items.Count == 0)
                {
                    b.Append(op == FilterOperator.In ? "FALSE" : "TRUE");
                    return;
                }
                b.Append(expr).Append(op == FilterOperator.In ? " IN (" : " NOT IN (");
                for (var i = 0; i < items.Count; i++)
                {
                    if (i > 0) b.Append(", ");
                    b.AppendValue(items[i]);
                }
                b.Append(")");
                return;
            }
            case FilterOperator.Between:
            case FilterOperator.NotBetween:
            {
                var (lo, hi) = BetweenBounds(f);
                b.Append(expr).Append(op == FilterOperator.Between ? " BETWEEN " : " NOT BETWEEN ").AppendValue(lo).Append(" AND ").AppendValue(hi);
                return;
            }
        }
        throw new ArgumentOutOfRangeException(nameof(f), op, "Unknown filter operator.");

        void Binary(string sqlOp) => b.Append(expr).Append(" ").Append(sqlOp).Append(" ").AppendValue(value);
    }

    /// <summary>BETWEEN bounds: <see cref="FilterCondition.Value2"/>, or "a and b" / "a, b" typed into the first value.</summary>
    public static (string Low, string High) BetweenBounds(FilterCondition f)
    {
        var v = f.Value ?? "";
        if (!string.IsNullOrEmpty(f.Value2)) return (v, f.Value2);
        var idx = v.IndexOf(" and ", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) return (v[..idx].Trim(), v[(idx + 5)..].Trim());
        idx = v.IndexOf(',');
        if (idx >= 0) return (v[..idx].Trim(), v[(idx + 1)..].Trim());
        return (v, v);
    }

    /// <summary>Comma-separated list; items may be single-quoted ('a,b' stays one item, '' escapes a quote).</summary>
    public static IReadOnlyList<string> SplitList(string text)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        var i = 0;
        var any = false;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\'')
            {
                any = true;
                i++;
                while (i < text.Length)
                {
                    if (text[i] == '\'')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '\'') { sb.Append('\''); i += 2; continue; }
                        i++;
                        break;
                    }
                    sb.Append(text[i++]);
                }
                continue;
            }
            if (c == ',')
            {
                Flush();
                i++;
                continue;
            }
            sb.Append(c);
            i++;
        }
        Flush();
        return result;

        void Flush()
        {
            var item = any ? sb.ToString().Trim() : sb.ToString().Trim();
            if (any || item.Length > 0) result.Add(item);
            sb.Clear();
            any = false;
        }
    }

    /// <summary>Escapes LIKE wildcards with the default escape character (backslash).</summary>
    public static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public static bool IsTextType(string? dataType)
    {
        if (string.IsNullOrEmpty(dataType)) return false;
        return dataType is "text" or "name" or "citext" or "varchar" or "bpchar" or "char"
            || dataType.StartsWith("character varying", StringComparison.Ordinal)
            || dataType.StartsWith("character", StringComparison.Ordinal)
            || dataType.StartsWith("varchar", StringComparison.Ordinal);
    }

    /// <summary>
    /// Header click (spec §6 Sort): cycles DESC → ASC → none for <paramref name="column"/>. Without
    /// <paramref name="addToMultiSort"/> the result has at most that column; with it, other columns keep their place.
    /// </summary>
    public static IReadOnlyList<SortSpec> CycleSort(IReadOnlyList<SortSpec> current, string column, bool addToMultiSort)
    {
        var existing = current.FirstOrDefault(s => s.Column == column);
        SortSpec? next = existing is null ? new SortSpec(column, SortDirection.Desc)
            : existing.Direction == SortDirection.Desc ? existing with { Direction = SortDirection.Asc }
            : null;
        if (!addToMultiSort) return next is null ? [] : [next];
        var list = new List<SortSpec>();
        foreach (var s in current)
        {
            if (s.Column == column)
            {
                if (next is not null) list.Add(next);
            }
            else list.Add(s);
        }
        if (existing is null) list.Add(next!);
        return list;
    }
}
