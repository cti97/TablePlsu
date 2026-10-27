using System.IO;
using System.Security;
using System.Text;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace TablePlsu.App.Features.SqlEditor;

/// <summary>
/// The PostgreSQL highlighting definition, generated in code from the active theme's <c>editor.*</c> colours (spec §9
/// "Syntax highlighting"): keywords, types, strings (incl. E'' escapes), <c>$$</c> / <c>$tag$</c> bodies (delimiters
/// coloured, body highlighted as SQL), comments, numbers, <c>::</c> casts, <c>$1</c> / <c>:name</c> parameters and function
/// calls. Rebuilt when the theme changes; definitions are cached per colour set.
/// </summary>
public static class SqlHighlighting
{
    public const string Name = "PostgreSQL";

    private static readonly object Gate = new();
    private static string? _cachedKey;
    private static IHighlightingDefinition? _cached;

    /// <summary>Theme key → highlighting colour name used in the generated XSHD.</summary>
    public static IReadOnlyDictionary<string, string> ColorKeys { get; } = new Dictionary<string, string>
    {
        ["Comment"] = "editor.comment",
        ["String"] = "editor.string",
        ["DollarQuote"] = "editor.string",
        ["Keyword"] = "editor.keyword",
        ["Type"] = "editor.type",
        ["Number"] = "editor.number",
        ["Function"] = "editor.function",
        ["Parameter"] = "editor.parameter",
        ["Operator"] = "editor.operator",
        ["Cast"] = "editor.type",
    };

    /// <summary>Creates (or returns the cached) definition for the given theme colours (keys like "editor.keyword").</summary>
    public static IHighlightingDefinition Create(IReadOnlyDictionary<string, string> themeColors)
    {
        var key = string.Join("|", ColorKeys.Values.Distinct().Select(k => themeColors.TryGetValue(k, out var v) ? v : ""));
        lock (Gate)
        {
            if (_cached is not null && _cachedKey == key) return _cached;
            using var reader = XmlReader.Create(new StringReader(BuildXshd(themeColors)));
            var xshd = HighlightingLoader.LoadXshd(reader);
            _cached = HighlightingLoader.Load(xshd, HighlightingManager.Instance);
            _cachedKey = key;
            return _cached;
        }
    }

    /// <summary>The XSHD document text for <paramref name="themeColors"/> (missing colours fall back to the foreground).</summary>
    public static string BuildXshd(IReadOnlyDictionary<string, string> themeColors)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\"?>");
        sb.AppendLine($"<SyntaxDefinition name=\"{Name}\" extensions=\".sql;.psql;.pgsql\" xmlns=\"http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008\">");
        foreach (var (name, themeKey) in ColorKeys)
        {
            var color = ColorOf(themeColors, themeKey);
            var italic = name == "Comment" ? " fontStyle=\"italic\"" : "";
            sb.AppendLine(color is null
                ? $"  <Color name=\"{name}\"{italic} />"
                : $"  <Color name=\"{name}\" foreground=\"{color}\"{italic} />");
        }

        // The main rule set is named so $$ bodies can recurse into it.
        sb.AppendLine("  <RuleSet name=\"Sql\" ignoreCase=\"true\">");
        sb.AppendLine("    <Span color=\"Comment\" begin=\"--\" />");
        sb.AppendLine("    <Span color=\"Comment\" multiline=\"true\" begin=\"/\\*\" end=\"\\*/\" />");
        // E'…' strings: backslash escapes.
        sb.AppendLine("    <Span color=\"String\" multiline=\"true\" begin=\"\\b[Ee]'\" end=\"'\">");
        sb.AppendLine("      <RuleSet><Span begin=\"\\\\\" end=\".\" /></RuleSet>");
        sb.AppendLine("    </Span>");
        // '…' strings ('' escapes simply read as two adjacent strings).
        sb.AppendLine("    <Span color=\"String\" multiline=\"true\" begin=\"'\" end=\"'\" />");
        // "quoted identifiers" stay the default colour but must not be scanned for keywords.
        sb.AppendLine("    <Span multiline=\"true\" begin=\"&quot;\" end=\"&quot;\" />");
        // Dollar-quoted bodies: delimiters coloured, body highlighted as SQL.
        sb.AppendLine("    <Span ruleSet=\"Sql\" multiline=\"true\">");
        sb.AppendLine("      <Begin color=\"DollarQuote\">\\$\\$</Begin>");
        sb.AppendLine("      <End color=\"DollarQuote\">\\$\\$</End>");
        sb.AppendLine("    </Span>");
        sb.AppendLine("    <Span ruleSet=\"Sql\" multiline=\"true\">");
        sb.AppendLine("      <Begin color=\"DollarQuote\">\\$[A-Za-z_][A-Za-z0-9_]*\\$</Begin>");
        sb.AppendLine("      <End color=\"DollarQuote\">\\$[A-Za-z_][A-Za-z0-9_]*\\$</End>");
        sb.AppendLine("    </Span>");
        // ::type casts (including schema-qualified and array types).
        sb.AppendLine("    <Rule color=\"Cast\">::\\s*[A-Za-z_][A-Za-z0-9_$]*(\\.[A-Za-z_][A-Za-z0-9_$]*)?(\\[\\])?</Rule>");
        // Parameters: $1, :name (not ::).
        sb.AppendLine("    <Rule color=\"Parameter\">\\$\\d+\\b</Rule>");
        sb.AppendLine("    <Rule color=\"Parameter\">(?&lt;![:\\w]):[A-Za-z_][A-Za-z0-9_]*</Rule>");
        AppendKeywords(sb, "Type", SqlKeywords.Types);
        AppendKeywords(sb, "Keyword", SqlKeywords.Keywords);
        sb.AppendLine("    <Rule color=\"Function\">\\b[A-Za-z_][A-Za-z0-9_$]*(?=\\s*\\()</Rule>");
        sb.AppendLine("    <Rule color=\"Number\">\\b\\d+(\\.\\d+)?([eE][+-]?\\d+)?\\b|\\B\\.\\d+\\b</Rule>");
        sb.AppendLine("    <Rule color=\"Operator\">[-+*/%&lt;&gt;=~!@#^&amp;|?]+</Rule>");
        sb.AppendLine("  </RuleSet>");
        sb.AppendLine("  <RuleSet>");
        sb.AppendLine("    <Import ruleSet=\"Sql\" />");
        sb.AppendLine("  </RuleSet>");
        sb.AppendLine("</SyntaxDefinition>");
        return sb.ToString();
    }

    private static void AppendKeywords(StringBuilder sb, string color, IEnumerable<string> words)
    {
        sb.AppendLine($"    <Keywords color=\"{color}\">");
        foreach (var w in words) sb.Append("      <Word>").Append(SecurityElement.Escape(w)).AppendLine("</Word>");
        sb.AppendLine("    </Keywords>");
    }

    private static string? ColorOf(IReadOnlyDictionary<string, string> colors, string key)
    {
        if (colors.TryGetValue(key, out var v) && TablePlsu.Core.Themes.ThemeColor.TryParse(v, out var c)) return c.ToString();
        if (colors.TryGetValue("editor.foreground", out var fg) && TablePlsu.Core.Themes.ThemeColor.TryParse(fg, out var f)) return f.ToString();
        return null;
    }
}

/// <summary>PostgreSQL keyword and type lists used by the highlighter.</summary>
public static class SqlKeywords
{
    public static IReadOnlyList<string> Keywords { get; } =
    [
        "abort", "absolute", "access", "action", "add", "admin", "after", "aggregate", "all", "also", "alter", "always",
        "analyse", "analyze", "and", "any", "array", "as", "asc", "assertion", "assignment", "asymmetric", "at", "attach",
        "attribute", "authorization", "backward", "before", "begin", "between", "both", "by", "cache", "call", "called",
        "cascade", "cascaded", "case", "cast", "catalog", "chain", "characteristics", "check", "checkpoint", "class", "close",
        "cluster", "coalesce", "collate", "collation", "column", "columns", "comment", "comments", "commit", "committed",
        "concurrently", "configuration", "conflict", "connection", "constraint", "constraints", "content", "continue",
        "conversion", "copy", "cost", "create", "cross", "csv", "cube", "current", "current_catalog", "current_date",
        "current_role", "current_schema", "current_time", "current_timestamp", "current_user", "cursor", "cycle", "data",
        "database", "day", "deallocate", "declare", "default", "defaults", "deferrable", "deferred", "definer", "delete",
        "delimiter", "delimiters", "depends", "desc", "detach", "dictionary", "disable", "discard", "distinct", "do",
        "document", "domain", "drop", "each", "else", "elsif", "enable", "encoding", "encrypted", "end", "enum", "escape",
        "event", "except", "exception", "exclude", "excluding", "exclusive", "execute", "exists", "explain", "expression",
        "extension", "external", "extract", "false", "family", "fetch", "filter", "first", "following", "for", "force",
        "foreach", "foreign", "forward", "freeze", "from", "full", "function", "functions", "generated", "get", "global",
        "grant", "granted", "greatest", "group", "grouping", "groups", "handler", "having", "header", "hold", "hour",
        "identity", "if", "ilike", "immediate", "immutable", "implicit", "import", "in", "include", "including", "increment",
        "index", "indexes", "inherit", "inherits", "initially", "inline", "inner", "inout", "input", "insensitive", "insert",
        "instead", "intersect", "into", "invoker", "is", "isnull", "isolation", "join", "key", "label", "language", "large",
        "last", "lateral", "leading", "leakproof", "least", "left", "level", "like", "limit", "listen", "load", "local",
        "localtime", "localtimestamp", "location", "lock", "locked", "logged", "loop", "mapping", "match", "materialized",
        "maxvalue", "merge", "method", "minute", "minvalue", "mode", "month", "move", "name", "names", "natural", "new",
        "next", "nfc", "nfd", "nfkc", "nfkd", "no", "none", "normalize", "normalized", "not", "nothing", "notice", "notify",
        "notnull", "nowait", "null", "nullif", "nulls", "of", "off", "offset", "oids", "old", "on", "only", "operator",
        "option", "options", "or", "order", "ordinality", "others", "out", "outer", "over", "overlaps", "overlay",
        "overriding", "owned", "owner", "parallel", "parser", "partial", "partition", "passing", "password", "perform",
        "placing", "plans", "policy", "position", "preceding", "prepare", "prepared", "preserve", "primary", "prior",
        "privileges", "procedural", "procedure", "procedures", "program", "publication", "quote", "raise", "range", "read",
        "reassign", "recheck", "recursive", "ref", "references", "referencing", "refresh", "reindex", "relative", "release",
        "rename", "repeatable", "replace", "replica", "reset", "restart", "restrict", "return", "returning", "returns",
        "revoke", "right", "role", "rollback", "rollup", "routine", "routines", "row", "rows", "rule", "savepoint", "schema",
        "schemas", "scroll", "search", "second", "security", "select", "sequence", "sequences", "serializable", "server",
        "session", "session_user", "set", "setof", "sets", "share", "show", "similar", "simple", "skip", "some", "sql",
        "stable", "standalone", "start", "statement", "statistics", "stdin", "stdout", "storage", "stored", "strict", "strip",
        "subscription", "substring", "support", "symmetric", "sysid", "system", "table", "tables", "tablesample",
        "tablespace", "temp", "template", "temporary", "then", "ties", "to", "trailing", "transaction", "transform", "treat",
        "trigger", "trim", "true", "truncate", "trusted", "type", "types", "uescape", "unbounded", "uncommitted",
        "unencrypted", "union", "unique", "unknown", "unlisten", "unlogged", "until", "update", "user", "using", "vacuum",
        "valid", "validate", "validator", "value", "values", "variadic", "verbose", "version", "view", "views", "volatile",
        "when", "where", "while", "whitespace", "window", "with", "within", "without", "work", "wrapper", "write", "xml",
        "year", "yes", "zone",
    ];

    public static IReadOnlyList<string> Types { get; } =
    [
        "bigint", "bigserial", "bit", "bool", "boolean", "box", "bytea", "char", "character", "cidr", "circle", "date",
        "decimal", "double", "float", "float4", "float8", "inet", "int", "int2", "int4", "int8", "integer", "interval",
        "json", "jsonb", "jsonpath", "line", "lseg", "macaddr", "macaddr8", "money", "numeric", "oid", "path", "point",
        "polygon", "precision", "real", "record", "regclass", "regproc", "regtype", "serial", "serial2", "serial4",
        "serial8", "smallint", "smallserial", "text", "time", "timestamp", "timestamptz", "timetz", "tsquery", "tsvector",
        "uuid", "varbit", "varchar", "varying", "void", "xid", "anyelement", "anyarray", "cstring",
        "int4range", "int8range", "numrange", "tsrange", "tstzrange", "daterange", "citext", "hstore",
    ];
}
