using TablePlsu.Core.Connections;
using TablePlsu.Core.Sql;

namespace TablePlsu.Core.Data;

/// <summary>What Safe Mode asks before statements are sent.</summary>
public enum SafeModeRequirement
{
    /// <summary>Send without asking.</summary>
    None,
    /// <summary>Confirm dialog listing the statements (Alert levels).</summary>
    Confirm,
    /// <summary>Confirm by typing the database password (Safe levels).</summary>
    Password,
}

/// <summary>
/// Safe Mode rules (spec §8): Silent sends; Alert 1 confirms everything; Alert 2 confirms everything except reads;
/// Safe 1 asks for the DB password for everything; Safe 2 asks for it except for reads. Reads are SELECT, EXPLAIN (without
/// ANALYZE of a write), SHOW, WITH-select (no data-modifying CTE), VALUES and TABLE.
/// </summary>
public static class SafeModePolicy
{
    public static SafeModeRequirement Evaluate(SafeModeLevel level, IReadOnlyList<string> statements)
    {
        if (statements.Count == 0) return SafeModeRequirement.None;
        return level switch
        {
            SafeModeLevel.Alert1 => SafeModeRequirement.Confirm,
            SafeModeLevel.Alert2 => AllReadOnly(statements) ? SafeModeRequirement.None : SafeModeRequirement.Confirm,
            SafeModeLevel.Safe1 => SafeModeRequirement.Password,
            SafeModeLevel.Safe2 => AllReadOnly(statements) ? SafeModeRequirement.None : SafeModeRequirement.Password,
            _ => SafeModeRequirement.None,
        };
    }

    /// <summary>How strict a level is: Silent &lt; Alert 2 &lt; Alert 1 &lt; Safe 2 &lt; Safe 1.</summary>
    public static int Strictness(SafeModeLevel level) => level switch
    {
        SafeModeLevel.Alert2 => 1,
        SafeModeLevel.Alert1 => 2,
        SafeModeLevel.Safe2 => 3,
        SafeModeLevel.Safe1 => 4,
        _ => 0,
    };

    public static bool IsPasswordLevel(SafeModeLevel level) => level is SafeModeLevel.Safe1 or SafeModeLevel.Safe2;

    /// <summary>Lowering the level from Safe 1 / Safe 2 requires the password.</summary>
    public static bool ChangeRequiresPassword(SafeModeLevel from, SafeModeLevel to) =>
        IsPasswordLevel(from) && Strictness(to) < Strictness(from);

    /// <summary>True when every statement (each entry may hold several) is read-only.</summary>
    public static bool AllReadOnly(IEnumerable<string> statements)
    {
        foreach (var text in statements)
        {
            foreach (var st in StatementSplitter.Split(text))
                if (!IsReadOnlyStatement(st.Text)) return false;
        }
        return true;
    }

    /// <summary>Classifies one statement (leading comments allowed).</summary>
    public static bool IsReadOnlyStatement(string statement)
    {
        var words = Words(statement);
        if (words.Count == 0) return true;
        switch (words[0])
        {
            case "SHOW":
            case "VALUES":
            case "TABLE":
                return true;
            case "SELECT":
            case "WITH":
                return !HasWriteKeyword(words, 1);
            case "EXPLAIN":
            {
                var analyze = false;
                var i = 1;
                // EXPLAIN [ ( options ) ] / EXPLAIN [ANALYZE] [VERBOSE] statement
                for (; i < words.Count; i++)
                {
                    var w = words[i];
                    if (w is "ANALYZE" or "ANALYSE") { analyze = true; continue; }
                    if (w is "VERBOSE" or "TRUE" or "FALSE" or "ON" or "OFF" or "COSTS" or "SETTINGS" or "BUFFERS" or "WAL"
                        or "TIMING" or "SUMMARY" or "FORMAT" or "TEXT" or "XML" or "JSON" or "YAML" or "GENERIC_PLAN" or "MEMORY" or "SERIALIZE" or "NONE" or "BINARY")
                        continue;
                    break;
                }
                if (!analyze) return true;
                if (i >= words.Count) return true;
                return words[i] is "SELECT" or "WITH" or "VALUES" or "TABLE" && !HasWriteKeyword(words, i + 1);
            }
            default:
                return false;
        }
    }

    private static bool HasWriteKeyword(List<string> words, int start)
    {
        for (var i = start; i < words.Count; i++)
        {
            switch (words[i])
            {
                case "INSERT" or "DELETE" or "MERGE" or "INTO" or "TRUNCATE":
                    return true;
                case "UPDATE":
                    // FOR UPDATE / FOR NO KEY UPDATE are row locks, not writes.
                    if (i > 0 && words[i - 1] is "FOR" or "KEY") continue;
                    return true;
            }
        }
        return false;
    }

    /// <summary>Upper-cased bare words outside strings, quoted identifiers, dollar bodies and comments.</summary>
    internal static List<string> Words(string text)
    {
        var words = new List<string>();
        var i = 0;
        var n = text.Length;
        while (i < n)
        {
            var c = text[i];
            if (c == '-' && i + 1 < n && text[i + 1] == '-')
            {
                var nl = text.IndexOf('\n', i);
                i = nl < 0 ? n : nl + 1;
                continue;
            }
            if (c == '/' && i + 1 < n && text[i + 1] == '*')
            {
                var depth = 1;
                i += 2;
                while (i < n && depth > 0)
                {
                    if (text[i] == '/' && i + 1 < n && text[i + 1] == '*') { depth++; i += 2; }
                    else if (text[i] == '*' && i + 1 < n && text[i + 1] == '/') { depth--; i += 2; }
                    else i++;
                }
                continue;
            }
            if (c == '\'' || c == '"')
            {
                var escapes = c == '\'' && i > 0 && (text[i - 1] is 'E' or 'e') && (i < 2 || !char.IsLetterOrDigit(text[i - 2]));
                i++;
                while (i < n)
                {
                    if (escapes && text[i] == '\\') { i += 2; continue; }
                    if (text[i] == c)
                    {
                        if (i + 1 < n && text[i + 1] == c) { i += 2; continue; }
                        i++;
                        break;
                    }
                    i++;
                }
                continue;
            }
            if (c == '$')
            {
                var j = i + 1;
                while (j < n && (char.IsLetterOrDigit(text[j]) || text[j] == '_')) j++;
                if (j < n && text[j] == '$' && (j == i + 1 || !char.IsDigit(text[i + 1])))
                {
                    var tag = text[i..(j + 1)];
                    var end = text.IndexOf(tag, j + 1, StringComparison.Ordinal);
                    i = end < 0 ? n : end + tag.Length;
                    continue;
                }
                i = j;
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '$')) i++;
                words.Add(text[start..i].ToUpperInvariant());
                continue;
            }
            i++;
        }
        return words;
    }
}
