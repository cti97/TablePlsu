using System.Text;
using System.Text.RegularExpressions;
using TablePlsu.Core.Sql;

namespace TablePlsu.App.Features.SqlEditor;

/// <summary>A text replacement plus the selection to apply afterwards (offsets into the new text).</summary>
public readonly record struct TextEdit(int Start, int Length, string Replacement, int SelectionStart, int SelectionLength);

/// <summary>What "Run Current" / "Run All" sends: the SQL text and its offset in the editor (for error positions).</summary>
public readonly record struct RunTarget(string Sql, int BaseOffset);

/// <summary>Pure text operations of the SQL editor (no WPF), unit-tested.</summary>
public static class SqlTextOps
{
    /// <summary>
    /// The SQL "Run Current" executes: the selection when it has non-whitespace text, else the statement under the caret
    /// (<see cref="StatementSplitter.StatementAt(string,int)"/>). Null when there is nothing to run.
    /// </summary>
    public static RunTarget? CurrentTarget(string text, int selectionStart, int selectionLength, int caret)
    {
        if (selectionLength > 0 && selectionStart >= 0 && selectionStart + selectionLength <= text.Length)
        {
            var sel = text.Substring(selectionStart, selectionLength);
            if (!string.IsNullOrWhiteSpace(sel)) return new RunTarget(sel, selectionStart);
        }
        var st = StatementSplitter.StatementAt(text, Math.Clamp(caret, 0, text.Length));
        return st is { } s ? new RunTarget(s.Text, s.Start) : null;
    }

    /// <summary>"Run All": the whole text (null when it holds no statement).</summary>
    public static RunTarget? AllTarget(string text) =>
        StatementSplitter.Split(text).Count == 0 ? null : new RunTarget(text, 0);

    /// <summary>The span to underline for an error at <paramref name="offset"/>: the word (or quoted token) there, at least 1 char.</summary>
    public static (int Start, int Length) ErrorSpan(string text, int offset)
    {
        if (text.Length == 0) return (0, 0);
        offset = Math.Clamp(offset, 0, text.Length - 1);
        // Positions can land on whitespace just after a token (e.g. at end of input): step back onto it.
        while (offset > 0 && char.IsWhiteSpace(text[offset])) offset--;
        var c = text[offset];
        int end;
        if (c is '"' or '\'')
        {
            end = text.IndexOf(c, offset + 1);
            end = end < 0 ? Math.Min(text.Length, offset + 1) : end + 1;
        }
        else if (IsWordChar(c))
        {
            end = offset;
            while (end < text.Length && IsWordChar(text[end])) end++;
        }
        else
        {
            end = offset + 1;
        }
        return (offset, Math.Max(1, end - offset));
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '.';

    /// <summary>
    /// Toggles <c>-- </c> on the lines covered by the selection (or the caret line). When every non-blank line is already
    /// commented, the markers (and one following space) are removed; otherwise they are added at the smallest indentation.
    /// </summary>
    public static TextEdit ToggleLineComment(string text, int selectionStart, int selectionLength, int caret)
    {
        var hasSelection = selectionLength > 0;
        var from = hasSelection ? selectionStart : caret;
        var to = hasSelection ? selectionStart + selectionLength : caret;
        from = Math.Clamp(from, 0, text.Length);
        to = Math.Clamp(to, from, text.Length);
        // A selection ending at column 0 does not include that line.
        if (hasSelection && to > from && to > 0 && text[to - 1] == '\n') to--;

        var blockStart = LineStart(text, from);
        var blockEnd = LineEnd(text, to);
        var block = text[blockStart..blockEnd];
        var lines = block.Split('\n');

        var nonBlank = lines.Where(l => l.Trim().Length > 0).ToList();
        var uncomment = nonBlank.Count > 0 && nonBlank.All(l => l.TrimStart().StartsWith("--", StringComparison.Ordinal));
        var indent = nonBlank.Count == 0 ? 0 : nonBlank.Min(l => l.Length - l.TrimStart(' ', '\t').Length);

        var sb = new StringBuilder(block.Length + lines.Length * 3);
        var caretDelta = 0;
        var caretLineStart = LineStart(text, Math.Clamp(caret, 0, text.Length));
        var lineStart = blockStart;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var newLine = line;
            int delta;
            int at;
            if (uncomment)
            {
                at = line.IndexOf("--", StringComparison.Ordinal);
                if (line.Trim().Length == 0 || at < 0) { delta = 0; }
                else
                {
                    var remove = at + 2 < line.Length && line[at + 2] == ' ' ? 3 : 2;
                    newLine = line.Remove(at, remove);
                    delta = -remove;
                }
            }
            else
            {
                at = Math.Min(indent, line.Length);
                if (line.Trim().Length == 0 && nonBlank.Count > 0) { delta = 0; }
                else
                {
                    newLine = line.Insert(at, "-- ");
                    delta = 3;
                }
            }
            if (lineStart == caretLineStart && delta != 0)
            {
                var col = caret - lineStart;
                if (delta > 0) caretDelta = col >= at ? delta : 0;
                else caretDelta = col >= at - delta ? delta : col > at ? at - col : 0;
            }
            sb.Append(newLine);
            if (i < lines.Length - 1) sb.Append('\n');
            lineStart += line.Length + 1;
        }
        var replacement = sb.ToString();
        if (hasSelection) return new TextEdit(blockStart, block.Length, replacement, blockStart, replacement.Length);
        var newCaret = Math.Clamp(caret + caretDelta, blockStart, blockStart + replacement.Length);
        return new TextEdit(blockStart, block.Length, replacement, newCaret, 0);
    }

    /// <summary>
    /// Wraps the selection (or the caret line) in <c>/* */</c>, or unwraps it when it already is a block comment.
    /// </summary>
    public static TextEdit ToggleBlockComment(string text, int selectionStart, int selectionLength, int caret)
    {
        int start, end;
        if (selectionLength > 0)
        {
            start = Math.Clamp(selectionStart, 0, text.Length);
            end = Math.Clamp(selectionStart + selectionLength, start, text.Length);
        }
        else
        {
            var c = Math.Clamp(caret, 0, text.Length);
            start = LineStart(text, c);
            end = LineEnd(text, c);
            while (start < end && char.IsWhiteSpace(text[start])) start++;
            while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        }
        var inner = text[start..end];
        var trimmed = inner.Trim();
        if (trimmed.StartsWith("/*", StringComparison.Ordinal) && trimmed.EndsWith("*/", StringComparison.Ordinal) && trimmed.Length >= 4)
        {
            var lead = inner.Length - inner.TrimStart().Length;
            var body = trimmed[2..^2];
            if (body.StartsWith(' ')) body = body[1..];
            if (body.EndsWith(' ')) body = body[..^1];
            var replacement = inner[..lead] + body + inner[(lead + trimmed.Length)..];
            return new TextEdit(start, inner.Length, replacement, start, replacement.Length);
        }
        var wrapped = "/* " + inner + " */";
        return new TextEdit(start, inner.Length, wrapped, start, wrapped.Length);
    }

    /// <summary>The regex the find/replace bar uses (mirrors the AvalonEdit search panel options). Null when invalid.</summary>
    public static Regex? BuildSearchRegex(string pattern, bool matchCase, bool wholeWords, bool useRegex)
    {
        if (string.IsNullOrEmpty(pattern)) return null;
        var p = useRegex ? pattern : Regex.Escape(pattern);
        if (wholeWords) p = @"\b" + p + @"\b";
        var options = RegexOptions.Multiline | (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
        try { return new Regex(p, options, TimeSpan.FromSeconds(2)); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>Replace All: returns the new text and the number of replacements.</summary>
    public static (string Text, int Count) ReplaceAll(string text, Regex regex, string replacement, bool useRegex)
    {
        var count = 0;
        var result = regex.Replace(text, m =>
        {
            count++;
            return useRegex ? m.Result(replacement) : replacement;
        });
        return (result, count);
    }

    /// <summary>"Query N" with N one more than the largest existing "Query k" title.</summary>
    public static string NextQueryTitle(IEnumerable<string?> existingTitles)
    {
        var max = 0;
        foreach (var t in existingTitles)
        {
            if (t is null || !t.StartsWith("Query ", StringComparison.Ordinal)) continue;
            if (int.TryParse(t.AsSpan(6), out var n) && n > max) max = n;
        }
        return "Query " + (max + 1);
    }

    private static int LineStart(string text, int offset)
    {
        if (offset <= 0) return 0;
        var i = text.LastIndexOf('\n', Math.Min(offset, text.Length) - 1);
        return i < 0 ? 0 : i + 1;
    }

    private static int LineEnd(string text, int offset)
    {
        var i = text.IndexOf('\n', Math.Min(offset, text.Length));
        if (i < 0) return text.Length;
        return i > 0 && text[i - 1] == '\r' ? i - 1 : i;
    }
}
