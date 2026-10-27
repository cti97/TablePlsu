using System.Collections;
using System.Globalization;
using System.Text;

namespace TablePlsu.Core.Db;

/// <summary>
/// Turns provider values into grid-friendly values. Scalars (numbers, dates, bool, Guid, string…) are kept as-is so the
/// grid can format and sort them; DBNull becomes null; bytea becomes a length-capped <c>\x…</c> hex preview; arrays become
/// PostgreSQL array literals. json/jsonb already arrive as strings.
/// </summary>
public static class DbValueConverter
{
    public const int MaxBinaryPreviewBytes = 256;

    public static object? ToDisplayValue(object? value) => value switch
    {
        null or DBNull => null,
        string => value,
        byte[] bytes => HexPreview(bytes),
        Array array => ArrayLiteral(array),
        _ => value,
    };

    /// <summary>
    /// True for display placeholders that are not the value itself: a truncated bytea preview or an "&lt;unreadable …&gt;"
    /// marker. Such text must never be written back to the database (e.g. by Duplicate Row).
    /// </summary>
    public static bool IsLossyPreview(object? value) => value is string s
        && ((s.StartsWith("\\x", StringComparison.Ordinal) && s.EndsWith(" bytes)", StringComparison.Ordinal) && s.Contains("… (", StringComparison.Ordinal))
            || s.StartsWith("<unreadable ", StringComparison.Ordinal));

    public static string HexPreview(byte[] bytes)
    {
        var n = Math.Min(bytes.Length, MaxBinaryPreviewBytes);
        var sb = new StringBuilder(2 + n * 2 + 24).Append("\\x");
        sb.Append(Convert.ToHexStringLower(bytes, 0, n));
        if (bytes.Length > n) sb.Append("… (").Append(bytes.Length.ToString(CultureInfo.InvariantCulture)).Append(" bytes)");
        return sb.ToString();
    }

    public static string ArrayLiteral(Array array)
    {
        var sb = new StringBuilder();
        if (array.Rank == 1)
        {
            AppendList(sb, array);
            return sb.ToString();
        }
        AppendDimension(sb, array, 0, new int[array.Rank]);
        return sb.ToString();
    }

    private static void AppendList(StringBuilder sb, IEnumerable items)
    {
        sb.Append('{');
        var first = true;
        foreach (var item in items)
        {
            if (!first) sb.Append(',');
            first = false;
            AppendElement(sb, item);
        }
        sb.Append('}');
    }

    private static void AppendDimension(StringBuilder sb, Array array, int dim, int[] index)
    {
        sb.Append('{');
        var lower = array.GetLowerBound(dim);
        var upper = array.GetUpperBound(dim);
        for (var i = lower; i <= upper; i++)
        {
            if (i > lower) sb.Append(',');
            index[dim] = i;
            if (dim == array.Rank - 1) AppendElement(sb, array.GetValue(index));
            else AppendDimension(sb, array, dim + 1, index);
        }
        sb.Append('}');
    }

    private static void AppendElement(StringBuilder sb, object? item)
    {
        switch (item)
        {
            case null or DBNull:
                sb.Append("NULL");
                break;
            case Array nested and not byte[]:
                sb.Append(ArrayLiteral(nested));
                break;
            default:
                var text = item switch
                {
                    byte[] b => HexPreview(b),
                    bool b => b ? "t" : "f",
                    // ISO text the server parses back to the same value (the culture-neutral default would be
                    // "01/01/2024 10:00:00": no fraction, no zone, month/day order up to DateStyle).
                    DateTime or DateTimeOffset or DateOnly or TimeOnly or TimeSpan => Data.PgText.Format(item),
                    IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                    _ => item.ToString() ?? "",
                };
                if (NeedsQuotes(text))
                    sb.Append('"').Append(text.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
                else
                    sb.Append(text);
                break;
        }
    }

    private static bool NeedsQuotes(string text)
    {
        if (text.Length == 0 || text.Equals("NULL", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var c in text)
            if (c is '{' or '}' or ',' or '"' or '\\' || char.IsWhiteSpace(c)) return true;
        return false;
    }
}
