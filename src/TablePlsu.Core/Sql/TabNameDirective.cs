using System.Text.RegularExpressions;

namespace TablePlsu.Core.Sql;

/// <summary>
/// Result-tab naming: a <c>-- name: X</c>, <c>-- label: X</c> or <c>-- tab: X</c> comment line directly above a statement
/// (i.e. in the leading comment block of a <see cref="SqlStatement"/>) sets the tab title. The key is case-insensitive;
/// the last directive in the block wins.
/// </summary>
public static partial class TabNameDirective
{
    [GeneratedRegex(@"^--\s*(?:name|label|tab)\s*:\s*(?<v>.*?)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Directive();

    public static string? Parse(string statementText)
    {
        string? found = null;
        foreach (var raw in statementText.AsSpan().EnumerateLines())
        {
            var line = raw.Trim();
            if (line.IsEmpty) continue;
            if (!line.StartsWith("--")) break; // first code line ends the leading comment block
            var m = Directive().Match(line.ToString());
            if (m.Success && m.Groups["v"].Value.Length > 0) found = m.Groups["v"].Value;
        }
        return found;
    }
}
