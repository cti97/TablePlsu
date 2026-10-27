using System.Globalization;
using System.Text;
using System.Windows.Data;
using TablePlsu.Core.Settings;

namespace TablePlsu.App.Features.Results;

/// <summary>
/// Turns grid values (as produced by <c>DbValueConverter</c>: null, string, numbers, DateTime/DateTimeOffset/DateOnly/
/// TimeSpan, bool, Guid…) into text. <see cref="Display"/> is for cells (NULL text, single line, truncated);
/// <see cref="Full"/> is the untruncated text used for copying and editing. Numbers use the invariant culture.
/// </summary>
public sealed class CellFormatter
{
    /// <summary>Cells show at most this many characters (then "…").</summary>
    public const int DefaultMaxDisplayChars = 500;

    public CellFormatter(string nullDisplay = "NULL", string dateFormat = "yyyy-MM-dd HH:mm:ss", int maxDisplayChars = DefaultMaxDisplayChars)
    {
        NullDisplay = nullDisplay;
        DateFormat = string.IsNullOrWhiteSpace(dateFormat) ? "yyyy-MM-dd HH:mm:ss" : dateFormat;
        MaxDisplayChars = Math.Max(16, maxDisplayChars);
    }

    public static CellFormatter FromSettings(GridSettings g) => new(g.NullDisplay, g.DateFormat);

    /// <summary>The formatter used by every grid; replaced when settings change (see <see cref="GridResources"/>).</summary>
    public static CellFormatter Current { get; internal set; } = new();

    public string NullDisplay { get; }
    public string DateFormat { get; }
    public int MaxDisplayChars { get; }

    /// <summary>Full text of a non-null value (null gives <paramref name="nullText"/>).</summary>
    /// <param name="dataType">The column's PostgreSQL type name (ResultColumn.DataTypeName); "date" formats DateTime as yyyy-MM-dd.</param>
    public string Full(object? value, string nullText = "", string? dataType = null) => value switch
    {
        null or DBNull => nullText,
        string s => s,
        bool b => b ? "true" : "false",
        DateTime dt when dataType == "date" => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString(DateFormat, CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString(DateFormat, CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString(t.Millisecond == 0 && t.Microsecond == 0 ? "HH:mm:ss" : "HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture),
        TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
        byte[] bytes => TablePlsu.Core.Db.DbValueConverter.HexPreview(bytes),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// <summary>Cell text: NULL text for null, newlines shown as "↵", truncated to <see cref="MaxDisplayChars"/>.</summary>
    public string Display(object? value, string? dataType = null)
    {
        if (value is null or DBNull) return NullDisplay;
        var text = Full(value, "", dataType);
        return SingleLine(text, MaxDisplayChars);
    }

    public static string SingleLine(string text, int max)
    {
        var limit = Math.Min(text.Length, max);
        var needsWork = text.Length > max;
        if (!needsWork)
        {
            for (var i = 0; i < limit; i++)
                if (text[i] is '\n' or '\r' or '\t') { needsWork = true; break; }
        }
        if (!needsWork) return text;

        var sb = new StringBuilder(limit + 1);
        for (var i = 0; i < limit; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '\r':
                    if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                    sb.Append('↵');
                    break;
                case '\n':
                    sb.Append('↵');
                    break;
                case '\t':
                    sb.Append(' ');
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        if (text.Length > max) sb.Append('…');
        return sb.ToString();
    }
}

/// <summary>The shared one-way converter for every grid cell: <c>Binding("[i]") { Converter = CellValueConverter.Instance }</c>.</summary>
public sealed class CellValueConverter : IValueConverter
{
    public static CellValueConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => CellFormatter.Current.Display(value, parameter as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Editing text: the full value, empty for NULL.</summary>
public sealed class CellEditConverter : IValueConverter
{
    public static CellEditConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => CellFormatter.Current.Full(value, "", parameter as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
