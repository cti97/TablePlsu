using System.Windows;
using System.Windows.Media;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Settings;

namespace TablePlsu.App.Features.Results;

/// <summary>
/// Grid font resources from settings, written into <c>Application.Resources</c> and kept current on settings reload:
/// <c>grid.fontFamily</c> (FontFamily), <c>grid.fontSize</c> (double), <c>grid.cellPadding</c> (Thickness, from
/// grid.rowPadding), <c>grid.rowHeight</c> (double). Use them with <c>{DynamicResource grid.fontSize}</c>. Also updates
/// <see cref="CellFormatter.Current"/> (grid.nullDisplay, grid.dateFormat) and raises <see cref="Changed"/>.
/// </summary>
public static class GridResources
{
    public const string FontFamilyKey = "grid.fontFamily";
    public const string FontSizeKey = "grid.fontSize";
    public const string CellPaddingKey = "grid.cellPadding";
    public const string RowHeightKey = "grid.rowHeight";

    private static bool _initialized;

    /// <summary>Tests (no Application): the dictionary <see cref="Apply"/> writes to instead of Application.Resources.</summary>
    internal static ResourceDictionary? TestTarget { get; set; }

    /// <summary>Tests: forget the one-time subscription so <see cref="EnsureInitialized"/> subscribes to a fresh AppServices.</summary>
    internal static void ResetForTests()
    {
        _initialized = false;
        Changed = null;
    }

    /// <summary>Raised (UI thread) after the resources and formatter were updated from settings.</summary>
    public static event EventHandler? Changed;

    /// <summary>Applies once and subscribes to settings changes. Safe to call repeatedly (every ResultGrid does).</summary>
    public static void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;
        Apply();
        if (AppServices.IsInitialized) AppServices.SettingsChanged += (_, _) => Apply();
    }

    public static void Apply()
    {
        var g = AppServices.IsInitialized ? AppServices.Current.Grid : new GridSettings();
        CellFormatter.Current = CellFormatter.FromSettings(g);
        if ((TestTarget ?? Application.Current?.Resources) is { } res) ApplyTo(res, g);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void ApplyTo(ResourceDictionary target, GridSettings g)
    {
        var family = new FontFamily(string.IsNullOrWhiteSpace(g.FontFamily) ? "Segoe UI" : g.FontFamily);
        target[FontFamilyKey] = family;
        target[FontSizeKey] = g.FontSize;
        target[CellPaddingKey] = new Thickness(4, g.RowPadding, 4, g.RowPadding);
        target[RowHeightKey] = RowHeight(family, g.FontSize, g.RowPadding);
    }

    /// <summary>A fixed row height (fixed heights keep virtualized scrolling smooth): line height + padding + border.</summary>
    public static double RowHeight(FontFamily family, double fontSize, double padding)
    {
        var line = Math.Ceiling(fontSize * (family.LineSpacing > 0 ? family.LineSpacing : 1.33));
        return line + 2 * padding + 1;
    }
}
