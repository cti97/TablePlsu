using System.Collections.Frozen;
using System.Text;

namespace TablePlsu.Core.Sql;

/// <summary>Identifier and literal quoting for generated SQL.</summary>
public static class SqlIdent
{
    /// <summary>PostgreSQL reserved (and reserved-requires-AS / type-func-name) keywords that must be quoted as identifiers.</summary>
    private static readonly FrozenSet<string> Reserved = new[]
    {
        "all", "analyse", "analyze", "and", "any", "array", "as", "asc", "asymmetric", "authorization", "binary", "both",
        "case", "cast", "check", "collate", "collation", "column", "concurrently", "constraint", "create", "cross",
        "current_catalog", "current_date", "current_role", "current_schema", "current_time", "current_timestamp",
        "current_user", "default", "deferrable", "desc", "distinct", "do", "else", "end", "except", "false", "fetch",
        "for", "foreign", "freeze", "from", "full", "grant", "group", "having", "ilike", "in", "initially", "inner",
        "intersect", "into", "is", "isnull", "join", "lateral", "leading", "left", "like", "limit", "localtime",
        "localtimestamp", "natural", "not", "notnull", "null", "offset", "on", "only", "or", "order", "outer",
        "overlaps", "placing", "primary", "references", "returning", "right", "select", "session_user", "similar",
        "some", "symmetric", "system_user", "table", "tablesample", "then", "to", "trailing", "true", "union", "unique",
        "user", "using", "variadic", "verbose", "when", "where", "window", "with",
    }.ToFrozenSet(StringComparer.Ordinal);

    public static bool IsReservedKeyword(string word) => Reserved.Contains(word.ToLowerInvariant());

    /// <summary>Always wraps in double quotes, doubling embedded quotes.</summary>
    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    /// <summary>Quotes only when needed: not a plain lower-case identifier, or a reserved keyword.</summary>
    public static string QuoteIfNeeded(string identifier) => NeedsQuoting(identifier) ? Quote(identifier) : identifier;

    public static bool NeedsQuoting(string identifier)
    {
        if (identifier.Length == 0) return true;
        var first = identifier[0];
        if (!(first is >= 'a' and <= 'z' || first == '_')) return true;
        foreach (var c in identifier)
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '$')) return true;
        return Reserved.Contains(identifier);
    }

    public static string Qualified(string? schema, string name) =>
        string.IsNullOrEmpty(schema) ? QuoteIfNeeded(name) : QuoteIfNeeded(schema) + "." + QuoteIfNeeded(name);

    /// <summary>Standard-conforming string literal ('' escaping). Use parameters for values whenever possible.</summary>
    public static string Literal(string value)
    {
        var sb = new StringBuilder(value.Length + 2).Append('\'');
        foreach (var c in value)
        {
            if (c == '\'') sb.Append('\'');
            sb.Append(c);
        }
        return sb.Append('\'').ToString();
    }
}
