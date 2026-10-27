namespace TablePlsu.Core.Data;

/// <summary>Filter operators (spec §6 "Filter operators"). The I-prefixed pattern operators are case-insensitive (ILIKE).</summary>
public enum FilterOperator
{
    Equal,
    NotEqual,
    Less,
    Greater,
    LessOrEqual,
    GreaterOrEqual,
    In,
    NotIn,
    IsNull,
    IsNotNull,
    Between,
    NotBetween,
    Like,
    ILike,
    Contains,
    NotContains,
    HasPrefix,
    HasSuffix,
    IContains,
    INotContains,
    IHasPrefix,
    IHasSuffix,
}

/// <summary>What a filter row applies to.</summary>
public enum FilterTarget
{
    /// <summary>One column (<see cref="FilterCondition.Column"/>).</summary>
    Column,
    /// <summary>Every column cast to text, combined with OR (AND for negative operators).</summary>
    AnyColumn,
    /// <summary><see cref="FilterCondition.Value"/> is inserted verbatim as a WHERE fragment.</summary>
    RawSql,
}

/// <summary>How the enabled filter rows are combined.</summary>
public enum FilterJoin { And, Or }

/// <summary>One filter row. <see cref="Value2"/> is the upper bound for BETWEEN / NOT BETWEEN.</summary>
public sealed record FilterCondition
{
    public bool Enabled { get; init; } = true;
    public FilterTarget Target { get; init; } = FilterTarget.Column;
    public string? Column { get; init; }
    public FilterOperator Operator { get; init; } = FilterOperator.Equal;
    public string? Value { get; init; }
    public string? Value2 { get; init; }

    public static FilterCondition For(string column, FilterOperator op, string? value = null, string? value2 = null) =>
        new() { Column = column, Operator = op, Value = value, Value2 = value2 };

    public static FilterCondition Any(FilterOperator op, string? value) => new() { Target = FilterTarget.AnyColumn, Operator = op, Value = value };

    public static FilterCondition Raw(string sql) => new() { Target = FilterTarget.RawSql, Value = sql };
}

/// <summary>Operator metadata for the filter bar.</summary>
public static class FilterOperators
{
    public static IReadOnlyList<FilterOperator> All { get; } = Enum.GetValues<FilterOperator>();

    public static string Label(FilterOperator op) => op switch
    {
        FilterOperator.Equal => "=",
        FilterOperator.NotEqual => "<>",
        FilterOperator.Less => "<",
        FilterOperator.Greater => ">",
        FilterOperator.LessOrEqual => "<=",
        FilterOperator.GreaterOrEqual => ">=",
        FilterOperator.In => "IN",
        FilterOperator.NotIn => "NOT IN",
        FilterOperator.IsNull => "IS NULL",
        FilterOperator.IsNotNull => "IS NOT NULL",
        FilterOperator.Between => "BETWEEN",
        FilterOperator.NotBetween => "NOT BETWEEN",
        FilterOperator.Like => "LIKE",
        FilterOperator.ILike => "ILIKE",
        FilterOperator.Contains => "Contains",
        FilterOperator.NotContains => "Not contains",
        FilterOperator.HasPrefix => "Has prefix",
        FilterOperator.HasSuffix => "Has suffix",
        FilterOperator.IContains => "Contains (case-insensitive)",
        FilterOperator.INotContains => "Not contains (case-insensitive)",
        FilterOperator.IHasPrefix => "Has prefix (case-insensitive)",
        FilterOperator.IHasSuffix => "Has suffix (case-insensitive)",
        _ => op.ToString(),
    };

    /// <summary>False for IS NULL / IS NOT NULL.</summary>
    public static bool NeedsValue(FilterOperator op) => op is not (FilterOperator.IsNull or FilterOperator.IsNotNull);

    public static bool NeedsSecondValue(FilterOperator op) => op is FilterOperator.Between or FilterOperator.NotBetween;

    /// <summary>Negative operators combine with AND across columns for "Any column" (no column matches).</summary>
    public static bool IsNegative(FilterOperator op) => op is FilterOperator.NotEqual or FilterOperator.NotIn or FilterOperator.IsNotNull
        or FilterOperator.NotBetween or FilterOperator.NotContains or FilterOperator.INotContains;

    /// <summary>Pattern operators compare the column as text.</summary>
    public static bool IsPattern(FilterOperator op) => op is FilterOperator.Like or FilterOperator.ILike or FilterOperator.Contains
        or FilterOperator.NotContains or FilterOperator.HasPrefix or FilterOperator.HasSuffix or FilterOperator.IContains
        or FilterOperator.INotContains or FilterOperator.IHasPrefix or FilterOperator.IHasSuffix;
}
