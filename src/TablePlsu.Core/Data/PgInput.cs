using System.Globalization;

namespace TablePlsu.Core.Data;

/// <summary>Turns text the user typed or pasted into a grid cell into the value to stage for that column.</summary>
public static class PgInput
{
    private static readonly string[] LocalDateTimeFormats =
    [
        "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm",
    ];

    /// <summary>
    /// For <c>timestamp with time zone</c> columns: a date-time typed without a zone gets "+00". The grid shows timestamptz
    /// values in UTC, so the typed wall time is UTC too; without the suffix the server would read it in the session's
    /// TimeZone and silently shift the stored instant. Anything else (other types, text with a zone, 'now', 'infinity'…)
    /// is returned unchanged.
    /// </summary>
    public static string ForColumn(string? dataTypeName, string text)
    {
        if (dataTypeName is not ("timestamp with time zone" or "timestamptz")) return text;
        var trimmed = text.Trim();
        return DateTime.TryParseExact(trimmed, LocalDateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? trimmed + "+00"
            : text;
    }
}
