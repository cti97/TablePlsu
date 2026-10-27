using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using TablePlsu.Core.Settings;
using TablePlsu.Core.Themes;

namespace TablePlsu.App.Infrastructure;

/// <summary>
/// Pushes the active theme into a <see cref="ResourceDictionary"/> (normally <c>Application.Resources</c>) as frozen
/// <see cref="SolidColorBrush"/> resources keyed <c>"c." + key</c> (for example <c>c.editor.keyword</c>) and
/// <see cref="Color"/> resources keyed <c>"color." + key</c>. All XAML uses <c>{DynamicResource c.…}</c>, so replacing the
/// values switches the theme live. In Auto mode it follows Windows' app light/dark setting (registry
/// <c>AppsUseLightTheme</c>, re-read on <see cref="SystemEvents.UserPreferenceChanged"/>). Window title bars follow too.
/// </summary>
public sealed class ThemeApplier
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private readonly ThemeService _themes;
    private readonly Func<ThemeSettings> _settings;
    private ResourceDictionary? _target;
    private bool _systemDark;
    private bool _watchingSystem;

    public ThemeApplier(ThemeService themes, Func<ThemeSettings> settings)
    {
        _themes = themes;
        _settings = settings;
        Current = ThemeService.BuiltInLight;
    }

    public ThemeDefinition Current { get; private set; }

    public bool IsDark => Current.IsDark;

    /// <summary>Raised (UI thread) after a theme has been applied. Editors regenerate highlighting from it.</summary>
    public event EventHandler<ThemeDefinition>? ThemeChanged;

    /// <summary>Sets the target dictionary and applies the theme for the current settings.</summary>
    public void Attach(ResourceDictionary target)
    {
        _target = target;
        _systemDark = IsSystemDark();
        Apply();
    }

    /// <summary>Starts following the Windows light/dark switch (called after first render; it creates a hidden window).</summary>
    public void WatchSystemTheme()
    {
        if (_watchingSystem) return;
        _watchingSystem = true;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public void StopWatchingSystemTheme()
    {
        if (!_watchingSystem) return;
        _watchingSystem = false;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    /// <summary>Re-resolves the theme from settings (and the system mode) and applies it.</summary>
    public void Apply()
    {
        var theme = _themes.Resolve(_settings(), _systemDark);
        Current = theme;
        if (_target is not null) ApplyTo(_target, theme);
        UpdateAllTitleBars();
        ThemeChanged?.Invoke(this, theme);
    }

    /// <summary>Writes every colour of <paramref name="theme"/> into <paramref name="target"/>. Invalid colours are skipped.</summary>
    public static void ApplyTo(ResourceDictionary target, ThemeDefinition theme)
    {
        foreach (var (key, value) in theme.Colors)
        {
            if (!ThemeColor.TryParse(value, out var c)) continue;
            var color = Color.FromArgb(c.A, c.R, c.G, c.B);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            target["c." + key] = brush;
            target["color." + key] = color;
        }
    }

    /// <summary>Parses "#RRGGBB" style text into a frozen brush (null when invalid). Used for connection and tag colours.</summary>
    public static SolidColorBrush? ParseBrush(string? text)
    {
        if (!ThemeColor.TryParse(text, out var c)) return null;
        var brush = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }

    /// <summary>True when Windows apps are set to dark mode.</summary>
    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Makes the standard Windows title bar dark or light to match the theme (Windows 10 2004+ / 11).</summary>
    public void ApplyTitleBar(Window window) => SetTitleBarDark(window, Current.IsDark);

    public static void SetTitleBarDark(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var value = dark ? 1 : 0;
        try
        {
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
        }
        catch (Exception)
        {
            // Older Windows: keep the default title bar.
        }
    }

    private void UpdateAllTitleBars()
    {
        var app = Application.Current;
        if (app is null) return;
        foreach (Window w in app.Windows) ApplyTitleBar(w);
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)) return;
        var dark = IsSystemDark();
        if (dark == _systemDark) return;
        _systemDark = dark;
        UiDispatch.Run(() =>
        {
            if (_settings().Mode == TablePlsu.Core.Settings.ThemeMode.Auto) Apply();
        });
    }

    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
