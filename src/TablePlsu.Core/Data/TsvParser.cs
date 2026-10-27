using System.Text;

namespace TablePlsu.Core.Data;

/// <summary>
/// Parses clipboard TSV as written by Excel and the grid's copy (fields separated by tabs, rows by CRLF or LF; a field
/// starting with a double quote may contain tabs, newlines and doubled quotes). A trailing line break does not add a row.
/// </summary>
public static class TsvParser
{
    public static List<string[]> Parse(string? text)
    {
        var rows = new List<string[]>();
        if (string.IsNullOrEmpty(text)) return rows;
        var row = new List<string>();
        var field = new StringBuilder();
        var i = 0;
        var n = text.Length;
        var fieldStart = true;
        while (i < n)
        {
            var c = text[i];
            if (fieldStart && c == '"')
            {
                i++;
                while (i < n)
                {
                    if (text[i] == '"')
                    {
                        if (i + 1 < n && text[i + 1] == '"') { field.Append('"'); i += 2; continue; }
                        i++;
                        break;
                    }
                    field.Append(text[i++]);
                }
                fieldStart = false;
                continue;
            }
            fieldStart = false;
            switch (c)
            {
                case '\t':
                    row.Add(field.ToString());
                    field.Clear();
                    fieldStart = true;
                    i++;
                    break;
                case '\r' when i + 1 < n && text[i + 1] == '\n':
                case '\n':
                case '\r':
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add([.. row]);
                    row.Clear();
                    fieldStart = true;
                    i += c == '\r' && i + 1 < n && text[i + 1] == '\n' ? 2 : 1;
                    break;
                default:
                    field.Append(c);
                    i++;
                    break;
            }
        }
        if (!fieldStart || row.Count > 0 || field.Length > 0)
        {
            row.Add(field.ToString());
            rows.Add([.. row]);
        }
        return rows;
    }
}
