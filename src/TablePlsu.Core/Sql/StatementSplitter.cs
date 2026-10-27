namespace TablePlsu.Core.Sql;

/// <summary>One statement inside a larger SQL text. <see cref="Start"/>/<see cref="Length"/> are offsets into that text;
/// the span is trimmed of surrounding whitespace, includes leading comments and excludes the terminating ';'.</summary>
public readonly record struct SqlStatement(int Start, int Length, string Text)
{
    public int End => Start + Length;
}

/// <summary>
/// PostgreSQL-aware statement splitter. Semicolons inside '…' strings (with '' escapes), E'…' strings (backslash
/// escapes), "…" identifiers, $$…$$ / $tag$…$tag$ bodies, -- line comments and nested /* */ comments do not split.
/// Segments that contain only whitespace and comments are dropped.
/// </summary>
public static class StatementSplitter
{
    public static IReadOnlyList<SqlStatement> Split(string text)
    {
        var result = new List<SqlStatement>();
        if (string.IsNullOrEmpty(text)) return result;

        var firstToken = -1;   // first non-whitespace char (comments included) of the current segment
        var lastToken = -1;    // last non-whitespace char of the current segment
        var hasCode = false;   // segment contains something other than comments
        var i = 0;
        var n = text.Length;

        void Mark(int from, int toInclusive, bool code)
        {
            if (firstToken < 0) firstToken = from;
            lastToken = toInclusive;
            hasCode |= code;
        }

        while (i < n)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == ';')
            {
                Emit();
                i++;
                continue;
            }

            if (c == '-' && i + 1 < n && text[i + 1] == '-')
            {
                var end = text.IndexOf('\n', i);
                var stop = end < 0 ? n : end;
                Mark(i, LastNonWhite(text, i, stop), code: false);
                i = stop;
                continue;
            }

            if (c == '/' && i + 1 < n && text[i + 1] == '*')
            {
                var start = i;
                i = SkipBlockComment(text, i);
                Mark(start, i - 1, code: false);
                continue;
            }

            if (c == '\'')
            {
                var escape = IsEscapeStringPrefix(text, i);
                var start = i;
                i = SkipString(text, i, escape);
                Mark(start, i - 1, code: true);
                continue;
            }

            if (c == '"')
            {
                var start = i;
                i = SkipQuotedIdent(text, i);
                Mark(start, i - 1, code: true);
                continue;
            }

            if (c == '$' && TryReadDollarTag(text, i, out var tag))
            {
                var start = i;
                var close = text.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                i = close < 0 ? n : close + tag.Length;
                Mark(start, i - 1, code: true);
                continue;
            }

            // Ordinary token character (identifier chars are consumed together so "a$b$" stays one word).
            var tokStart = i;
            if (IsIdentChar(c))
            {
                while (i < n && (IsIdentChar(text[i]) || text[i] == '$')) i++;
            }
            else
            {
                i++;
            }
            Mark(tokStart, i - 1, code: true);
        }
        Emit();
        return result;

        void Emit()
        {
            if (hasCode && firstToken >= 0)
            {
                var len = lastToken - firstToken + 1;
                result.Add(new SqlStatement(firstToken, len, text.Substring(firstToken, len)));
            }
            firstToken = lastToken = -1;
            hasCode = false;
        }
    }

    /// <summary>The statement containing <paramref name="caret"/> (end-inclusive), or the previous one when the caret sits
    /// in whitespace/comments between statements; the first statement when the caret is before all of them.</summary>
    public static SqlStatement? StatementAt(string text, int caret) => StatementAt(Split(text), caret);

    public static SqlStatement? StatementAt(IReadOnlyList<SqlStatement> statements, int caret)
    {
        if (statements.Count == 0) return null;
        SqlStatement? previous = null;
        foreach (var s in statements)
        {
            if (caret < s.Start) return previous ?? s;
            if (caret <= s.End) return s;
            previous = s;
        }
        return previous;
    }

    private static int LastNonWhite(string text, int from, int stopExclusive)
    {
        var j = stopExclusive - 1;
        while (j > from && char.IsWhiteSpace(text[j])) j--;
        return j;
    }

    private static int SkipBlockComment(string text, int i)
    {
        var depth = 0;
        var n = text.Length;
        while (i < n)
        {
            if (text[i] == '/' && i + 1 < n && text[i + 1] == '*') { depth++; i += 2; continue; }
            if (text[i] == '*' && i + 1 < n && text[i + 1] == '/')
            {
                depth--;
                i += 2;
                if (depth == 0) return i;
                continue;
            }
            i++;
        }
        return n;
    }

    private static int SkipString(string text, int i, bool backslashEscapes)
    {
        var n = text.Length;
        i++; // opening quote
        while (i < n)
        {
            var c = text[i];
            if (backslashEscapes && c == '\\') { i += 2; continue; }
            if (c == '\'')
            {
                if (i + 1 < n && text[i + 1] == '\'') { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return n;
    }

    private static int SkipQuotedIdent(string text, int i)
    {
        var n = text.Length;
        i++;
        while (i < n)
        {
            if (text[i] == '"')
            {
                if (i + 1 < n && text[i + 1] == '"') { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return n;
    }

    /// <summary>True when the quote at <paramref name="quote"/> is preceded by a standalone E/e (E'…' string).</summary>
    private static bool IsEscapeStringPrefix(string text, int quote)
    {
        if (quote == 0) return false;
        var p = text[quote - 1];
        if (p is not ('E' or 'e')) return false;
        return quote - 2 < 0 || !(IsIdentChar(text[quote - 2]) || text[quote - 2] == '$');
    }

    /// <summary>Reads "$tag$" at <paramref name="i"/>. The tag is empty or an identifier not starting with a digit,
    /// and the '$' must not continue an identifier (a$b) or be a positional parameter ($1).</summary>
    private static bool TryReadDollarTag(string text, int i, out string tag)
    {
        tag = "";
        if (i > 0 && (IsIdentChar(text[i - 1]) || text[i - 1] == '$')) return false;
        var j = i + 1;
        var n = text.Length;
        if (j < n && text[j] == '$') { tag = "$$"; return true; }
        if (j >= n || !(char.IsLetter(text[j]) || text[j] == '_')) return false;
        while (j < n && (char.IsLetterOrDigit(text[j]) || text[j] == '_')) j++;
        if (j >= n || text[j] != '$') return false;
        tag = text.Substring(i, j - i + 1);
        return true;
    }

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
