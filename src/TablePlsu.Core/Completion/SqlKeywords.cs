using System.Collections.Frozen;

namespace TablePlsu.Core.Completion;

/// <summary>Keyword and built-in function lists offered by the completion engine.</summary>
public static class SqlKeywords
{
    /// <summary>Keywords suggested by autocomplete (upper case; the engine lower-cases them when configured).</summary>
    public static readonly IReadOnlyList<string> All =
    [
        "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "IN", "IS", "NULL", "AS", "ON", "JOIN", "LEFT", "RIGHT", "INNER",
        "OUTER", "FULL", "CROSS", "NATURAL", "LATERAL", "USING", "GROUP", "ORDER", "BY", "HAVING", "LIMIT", "OFFSET",
        "FETCH", "FIRST", "NEXT", "ROWS", "ONLY", "DISTINCT", "ALL", "ANY", "SOME", "EXISTS", "BETWEEN", "LIKE", "ILIKE",
        "SIMILAR", "CASE", "WHEN", "THEN", "ELSE", "END", "ASC", "DESC", "NULLS", "LAST", "UNION", "INTERSECT", "EXCEPT",
        "WITH", "RECURSIVE", "MATERIALIZED", "INSERT", "INTO", "VALUES", "DEFAULT", "UPDATE", "SET", "DELETE",
        "RETURNING", "CONFLICT", "DO", "NOTHING", "TRUNCATE", "CREATE", "ALTER", "DROP", "TABLE", "VIEW", "INDEX",
        "SCHEMA", "SEQUENCE", "FUNCTION", "PROCEDURE", "TRIGGER", "TYPE", "EXTENSION", "DATABASE", "COLUMN",
        "CONSTRAINT", "PRIMARY", "KEY", "FOREIGN", "REFERENCES", "UNIQUE", "CHECK", "CASCADE", "RESTRICT", "ADD",
        "RENAME", "TO", "IF", "TRUE", "FALSE", "CAST", "BEGIN", "COMMIT", "ROLLBACK", "SAVEPOINT", "TRANSACTION",
        "EXPLAIN", "ANALYZE", "VERBOSE", "VACUUM", "GRANT", "REVOKE", "WINDOW", "OVER", "PARTITION", "FILTER",
        "TEMPORARY", "TEMP", "UNLOGGED", "REPLACE", "OWNER", "COMMENT", "CALL", "COPY", "LOCK", "SHOW", "FOR",
        "SKIP", "LOCKED", "NOWAIT", "TABLESAMPLE", "INTERVAL", "COLLATE", "ARRAY", "ROW", "CURRENT_DATE",
        "CURRENT_TIMESTAMP", "CURRENT_USER",
    ];

    /// <summary>Common PostgreSQL functions, offered even when pg_catalog is not loaded (name, arguments hint).</summary>
    public static readonly IReadOnlyList<(string Name, string Args)> BuiltinFunctions =
    [
        ("count", "*"), ("sum", "expr"), ("avg", "expr"), ("min", "expr"), ("max", "expr"), ("coalesce", "value, ..."),
        ("nullif", "a, b"), ("greatest", "value, ..."), ("least", "value, ..."), ("now", ""), ("lower", "text"),
        ("upper", "text"), ("length", "text"), ("trim", "text"), ("substring", "text, from, for"),
        ("replace", "text, from, to"), ("concat", "value, ..."), ("concat_ws", "sep, value, ..."),
        ("split_part", "text, delimiter, n"), ("position", "sub in text"), ("left", "text, n"), ("right", "text, n"),
        ("to_char", "value, format"), ("to_date", "text, format"), ("to_timestamp", "text, format"),
        ("date_trunc", "field, source"), ("date_part", "field, source"), ("extract", "field from source"), ("age", "ts, ts"),
        ("round", "numeric, digits"), ("floor", "numeric"), ("ceil", "numeric"), ("abs", "numeric"), ("random", ""),
        ("string_agg", "expr, delimiter"), ("array_agg", "expr"), ("json_agg", "expr"), ("jsonb_agg", "expr"),
        ("json_build_object", "key, value, ..."), ("jsonb_build_object", "key, value, ..."),
        ("jsonb_set", "target, path, value"), ("row_number", ""), ("rank", ""), ("dense_rank", ""), ("lag", "expr"),
        ("lead", "expr"), ("generate_series", "start, stop"), ("unnest", "array"), ("array_length", "array, dim"),
        ("gen_random_uuid", ""), ("current_setting", "name"), ("pg_size_pretty", "bigint"),
    ];

    /// <summary>Words that end a table reference (so they are never taken as an alias).</summary>
    internal static readonly FrozenSet<string> NotAlias = new[]
    {
        "where", "join", "inner", "left", "right", "full", "cross", "natural", "outer", "on", "using", "group", "order",
        "having", "limit", "offset", "fetch", "union", "intersect", "except", "window", "set", "returning", "values",
        "select", "from", "as", "lateral", "only", "default", "for", "with", "tablesample", "do", "on", "and", "or",
        "when", "then", "else", "end", "into", "overriding", "partition", "add", "drop", "alter", "rename", "owner",
        "is", "not", "in", "like", "ilike", "between",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
