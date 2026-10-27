using System.Globalization;

namespace TablePlsu.Core.Themes;

public enum ThemeBase { Light, Dark }

/// <summary>
/// A resolved theme: <see cref="Base"/> plus a flat colour map keyed like "editor.keyword" (see <see cref="ThemeKeys"/>).
/// Built-in and user themes are always completed with every key from their base theme.
/// The App's ThemeApplier publishes each entry as a SolidColorBrush resource "c." + key.
/// </summary>
public sealed record ThemeDefinition(string Name, ThemeBase Base, IReadOnlyDictionary<string, string> Colors)
{
    public bool IsDark => Base == ThemeBase.Dark;
}

/// <summary>Every colour key a theme defines. Groups: app.*, editor.*, grid.*.</summary>
public static class ThemeKeys
{
    public static IReadOnlyList<string> App { get; } =
    [
        "app.window", "app.windowForeground", "app.mutedForeground", "app.border",
        "app.sidebar", "app.sidebarForeground", "app.sidebarSelection",
        "app.tab", "app.tabForeground", "app.tabActive", "app.tabActiveForeground", "app.tabHover",
        "app.toolbar", "app.toolbarForeground", "app.statusBar", "app.statusBarForeground",
        "app.accent", "app.accentForeground", "app.hover", "app.inputBackground", "app.inputBorder",
        "app.error", "app.warning", "app.success",
    ];

    public static IReadOnlyList<string> Editor { get; } =
    [
        "editor.background", "editor.foreground", "editor.keyword", "editor.string", "editor.number", "editor.comment",
        "editor.type", "editor.function", "editor.operator", "editor.parameter", "editor.selection",
        "editor.currentQueryBackground", "editor.currentLine", "editor.errorUnderline", "editor.caret",
        "editor.lineNumbers", "editor.lineNumbersActive",
    ];

    public static IReadOnlyList<string> Grid { get; } =
    [
        "grid.background", "grid.foreground", "grid.altRowBackground", "grid.lines",
        "grid.headerBackground", "grid.headerForeground", "grid.selectionBackground", "grid.selectionForeground",
        "grid.cellSelectionBorder", "grid.cellSelectionBackground",
        "grid.pendingNewBackground", "grid.pendingModifiedBackground", "grid.pendingDeletedBackground",
        "grid.nullForeground",
    ];

    public static IReadOnlyList<string> All { get; } = [.. App, .. Editor, .. Grid];
}

/// <summary>ARGB colour parsed from "#RGB", "#RRGGBB" or "#AARRGGBB".</summary>
public readonly record struct ThemeColor(byte A, byte R, byte G, byte B)
{
    public static bool TryParse(string? text, out ThemeColor color)
    {
        color = default;
        if (string.IsNullOrEmpty(text) || text[0] != '#') return false;
        var hex = text.AsSpan(1);
        if (hex.Length == 3)
        {
            Span<char> expanded = [hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]];
            if (!uint.TryParse(expanded, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v3)) return false;
            color = new ThemeColor(255, (byte)(v3 >> 16), (byte)(v3 >> 8), (byte)v3);
            return true;
        }
        if (hex.Length is not (6 or 8)) return false;
        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        color = hex.Length == 6
            ? new ThemeColor(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : new ThemeColor((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public override string ToString() => A == 255 ? $"#{R:X2}{G:X2}{B:X2}" : $"#{A:X2}{R:X2}{G:X2}{B:X2}";
}
