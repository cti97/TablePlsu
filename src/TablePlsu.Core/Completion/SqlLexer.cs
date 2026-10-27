namespace TablePlsu.Core.Completion;

internal enum TokKind { Ident, QuotedIdent, String, Number, Punct, Comment, Param }

/// <summary>A lexed token. For identifiers <see cref="Value"/> is the name (quotes removed and unescaped); for punctuation
/// it is the operator text. <see cref="Unterminated"/> marks a string/comment/quoted identifier that runs to the end.</summary>
internal readonly record struct Tok(TokKind Kind, int Start, int End, string Value, bool Unterminated = false)
{
    public bool IsIdent => Kind is TokKind.Ident or TokKind.QuotedIdent;

    /// <summary>An unquoted identifier equal (case-insensitively) to <paramref name="keyword"/>.</summary>
    public bool Is(string keyword) => Kind == TokKind.Ident && Value.Equals(keyword, StringComparison.OrdinalIgnoreCase);

    public bool IsPunct(char c) => Kind == TokKind.Punct && Value.Length == 1 && Value[0] == c;
}

/// <summary>
/// A lightweight PostgreSQL lexer for completion: identifiers, quoted identifiers, strings ('', E'', $tag$), numbers,
/// comments (-- and nested /* */), parameters ($1) and punctuation. It never throws; unterminated constructs run to the end.
/// </summary>
internal static class SqlLexer
{
    public static List<Tok> Lex(string text, int start = 0, int end = -1)
    {
        if (end < 0) end = text.Length;
        var list = new List<Tok>(Math.Max(16, (end - start) / 4));
        var i = start;
        while (i < end)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            // Comments
            if (c == '-' && i + 1 < end && text[i + 1] == '-')
            {
                var j = i + 2;
                while (j < end && text[j] != '\n') j++;
                list.Add(new Tok(TokKind.Comment, i, j, "", Unterminated: true));
                i = j;
                continue;
            }
            if (c == '/' && i + 1 < end && text[i + 1] == '*')
            {
                var depth = 0;
                var j = i;
                var closed = false;
                while (j < end)
                {
                    if (text[j] == '/' && j + 1 < end && text[j + 1] == '*') { depth++; j += 2; continue; }
                    if (text[j] == '*' && j + 1 < end && text[j + 1] == '/')
                    {
                        depth--;
                        j += 2;
                        if (depth == 0) { closed = true; break; }
                        continue;
                    }
                    j++;
                }
                list.Add(new Tok(TokKind.Comment, i, j, "", !closed));
                i = j;
                continue;
            }

            // Strings
            if (c == '\'')
            {
                var escapes = i > start && text[i - 1] is 'E' or 'e' && (i - 2 < start || !IsIdentPart(text[i - 2]));
                if (escapes && list.Count > 0 && list[^1] is { Kind: TokKind.Ident, End: var e } && e == i) list.RemoveAt(list.Count - 1);
                var (j, closed) = ReadString(text, i, end, escapes);
                list.Add(new Tok(TokKind.String, escapes ? i - 1 : i, j, "", !closed));
                i = j;
                continue;
            }
            if (c == '$')
            {
                if (i + 1 < end && char.IsAsciiDigit(text[i + 1]))
                {
                    var j = i + 1;
                    while (j < end && char.IsAsciiDigit(text[j])) j++;
                    list.Add(new Tok(TokKind.Param, i, j, text[i..j]));
                    i = j;
                    continue;
                }
                if (TryReadDollarTag(text, i, end, out var tag))
                {
                    var close = text.IndexOf(tag, i + tag.Length, end - i - tag.Length, StringComparison.Ordinal);
                    var j = close < 0 ? end : close + tag.Length;
                    list.Add(new Tok(TokKind.String, i, j, "", close < 0));
                    i = j;
                    continue;
                }
            }

            // Quoted identifier
            if (c == '"')
            {
                var j = i + 1;
                var closed = false;
                var sb = new System.Text.StringBuilder();
                while (j < end)
                {
                    if (text[j] == '"')
                    {
                        if (j + 1 < end && text[j + 1] == '"') { sb.Append('"'); j += 2; continue; }
                        j++;
                        closed = true;
                        break;
                    }
                    sb.Append(text[j]);
                    j++;
                }
                list.Add(new Tok(TokKind.QuotedIdent, i, j, sb.ToString(), !closed));
                i = j;
                continue;
            }

            // Numbers
            if (char.IsAsciiDigit(c) || c == '.' && i + 1 < end && char.IsAsciiDigit(text[i + 1]) && !(list.Count > 0 && list[^1].End == i && list[^1].IsIdent))
            {
                var j = i + 1;
                while (j < end && (char.IsAsciiLetterOrDigit(text[j]) || text[j] == '.' || text[j] == '_')) j++;
                list.Add(new Tok(TokKind.Number, i, j, text[i..j]));
                i = j;
                continue;
            }

            // Identifiers
            if (IsIdentStart(c))
            {
                var j = i + 1;
                while (j < end && IsIdentPart(text[j])) j++;
                list.Add(new Tok(TokKind.Ident, i, j, text[i..j]));
                i = j;
                continue;
            }

            // Punctuation: '::' kept together, everything else single-char.
            if (c == ':' && i + 1 < end && text[i + 1] == ':')
            {
                list.Add(new Tok(TokKind.Punct, i, i + 2, "::"));
                i += 2;
                continue;
            }
            list.Add(new Tok(TokKind.Punct, i, i + 1, c.ToString()));
            i++;
        }
        return list;
    }

    public static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';

    public static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';

    private static (int End, bool Closed) ReadString(string text, int i, int end, bool backslash)
    {
        i++;
        while (i < end)
        {
            var c = text[i];
            if (backslash && c == '\\') { i += 2; continue; }
            if (c == '\'')
            {
                if (i + 1 < end && text[i + 1] == '\'') { i += 2; continue; }
                return (i + 1, true);
            }
            i++;
        }
        return (end, false);
    }

    private static bool TryReadDollarTag(string text, int i, int end, out string tag)
    {
        tag = "";
        if (i > 0 && IsIdentPart(text[i - 1])) return false;
        var j = i + 1;
        if (j < end && text[j] == '$') { tag = "$$"; return true; }
        if (j >= end || !IsIdentStart(text[j])) return false;
        while (j < end && (char.IsLetterOrDigit(text[j]) || text[j] == '_')) j++;
        if (j >= end || text[j] != '$') return false;
        tag = text.Substring(i, j - i + 1);
        return true;
    }
}
