using System.Windows;
using System.Windows.Media;
using TablePlsu.App.Infrastructure;
using TablePlsu.App.Shell;
using TablePlsu.Core.Settings;

namespace TablePlsu.App.Features.Settings;

/// <summary>
/// Publishes the editor and sidebar font settings as <c>Application.Resources</c> so views can bind them with
/// <c>{DynamicResource …}</c> and follow settings.json live (the grid's own keys live in <c>GridResources</c>):
/// <list type="bullet">
/// <item><c>editor.fontFamily</c> (FontFamily, with Cascadia Mono / Consolas fallback), <c>editor.fontSize</c> (double),
/// <c>editor.lineHeight</c> (double, multiplier).</item>
/// <item><c>sidebar.fontFamily</c> (FontFamily), <c>sidebar.fontSize</c> (double), <c>sidebar.rowPadding</c> (Thickness),
/// <c>sidebar.rowHeight</c> (double, a fixed row height for virtualized trees).</item>
/// </list>
/// Also refreshes things that cache settings values on construction (connection tag colours).
/// </summary>
public static class SettingsResources
{
    public const string EditorFontFamilyKey = "editor.fontFamily";
    public const string EditorFontSizeKey = "editor.fontSize";
    public const string EditorLineHeightKey = "editor.lineHeight";
    public const string SidebarFontFamilyKey = "sidebar.fontFamily";
    public const string SidebarFontSizeKey = "sidebar.fontSize";
    public const string SidebarRowPaddingKey = "sidebar.rowPadding";
    public const string SidebarRowHeightKey = "sidebar.rowHeight";

    private static bool _initialized;

    /// <summary>Tests (no Application): the dictionary <see cref="Apply"/> writes to instead of Application.Resources.</summary>
    internal static ResourceDictionary? TestTarget { get; set; }

    /// <summary>Tests: forget the one-time subscription so <see cref="EnsureInitialized"/> subscribes to a fresh AppServices.</summary>
    internal static void ResetForTests()
    {
        _initialized = false;
        Changed = null;
    }
    private static AppSettings? _applied;

    /// <summary>Raised (UI thread) after the resources were updated.</summary>
    public static event EventHandler? Changed;

    /// <summary>Applies once and subscribes to settings changes. Cheap and idempotent.</summary>
    public static void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;
        Apply();
        if (AppServices.IsInitialized) AppServices.SettingsChanged += (_, _) => OnSettingsChanged();
    }

    private static void OnSettingsChanged()
    {
        var previous = _applied;
        Apply();
        if (previous is not null && !TagsEqual(previous.Tags, AppServices.Current.Tags)) RefreshTagColours();
    }

    public static void Apply()
    {
        var s = AppServices.IsInitialized ? AppServices.Current : new AppSettings();
        if ((TestTarget ?? Application.Current?.Resources) is { } res) ApplyTo(res, s);
        _applied = s;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void ApplyTo(ResourceDictionary target, AppSettings s)
    {
        target[EditorFontFamilyKey] = WithFallback(s.Editor.FontFamily, "Cascadia Mono, Consolas, Courier New");
        target[EditorFontSizeKey] = s.Editor.FontSize;
        target[EditorLineHeightKey] = s.Editor.LineHeight;

        var sidebarFont = WithFallback(s.Sidebar.FontFamily, "Segoe UI");
        target[SidebarFontFamilyKey] = sidebarFont;
        target[SidebarFontSizeKey] = s.Sidebar.FontSize;
        target[SidebarRowPaddingKey] = new Thickness(0, s.Sidebar.RowPadding, 0, s.Sidebar.RowPadding);
        var line = Math.Ceiling(s.Sidebar.FontSize * (sidebarFont.LineSpacing > 0 ? sidebarFont.LineSpacing : 1.33));
        target[SidebarRowHeightKey] = line + 2 * s.Sidebar.RowPadding;
    }

    /// <summary>"Fira Code" → FontFamily("Fira Code, Cascadia Mono, Consolas") so a missing font falls back to a monospace one.</summary>
    public static FontFamily WithFallback(string? family, string fallback)
    {
        var name = string.IsNullOrWhiteSpace(family) ? fallback : family.Trim();
        if (!string.Equals(name, fallback, StringComparison.OrdinalIgnoreCase)) name = name + ", " + fallback;
        return new FontFamily(name);
    }

    private static bool TagsEqual(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    /// <summary>Welcome lists and workspace headers resolve tag brushes once; rebuild them after the tags changed.</summary>
    private static void RefreshTagColours()
    {
        if (Application.Current is null) return;
        foreach (Window w in Application.Current.Windows)
        {
            if (w is not MainWindow main) continue;
            switch (main.ViewModel.Content)
            {
                case Welcome.WelcomeViewModel welcome:
                    welcome.Reload();
                    break;
                case Workspace.WorkspaceViewModel ws:
                    ws.RefreshTagBrush();
                    break;
            }
        }
    }
}
