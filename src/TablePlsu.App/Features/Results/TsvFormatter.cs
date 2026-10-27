using System.Text;

namespace TablePlsu.App.Features.Results;

/// <summary>
/// Tab-separated text that pastes cleanly into Excel: rows end with CRLF (no trailing newline after the last row); a
/// field containing a tab, CR, LF or a double quote is wrapped in double quotes with inner quotes doubled.
/// </summary>
public static class TsvFormatter
{
    public static string Format(IReadOnlyList<string>? header, IEnumerable<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();
        var first = true;
        if (header is not null)
        {
            AppendRow(sb, header);
            first = false;
        }
        foreach (var row in rows)
        {
            if (!first) sb.Append("\r\n");
            first = false;
            AppendRow(sb, row);
        }
        return sb.ToString();
    }

    public static void AppendRow(StringBuilder sb, IReadOnlyList<string> fields)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0) sb.Append('\t');
            AppendField(sb, fields[i]);
        }
    }

    public static void AppendField(StringBuilder sb, string field)
    {
        if (field.AsSpan().IndexOfAny("\t\r\n\"") < 0)
        {
            sb.Append(field);
            return;
        }
        sb.Append('"').Append(field.Replace("\"", "\"\"")).Append('"');
    }
}
