using TablePlsu.Core.Settings;
using TablePlsu.Core.Themes;
using TablePlsu.Tests.Infrastructure;

namespace TablePlsu.Tests.Themes;

public class ThemeServiceTests
{
    [Fact]
    public void Built_in_themes_define_every_color_key()
    {
        foreach (var theme in new[] { ThemeService.BuiltInLight, ThemeService.BuiltInDark })
        {
            foreach (var key in ThemeKeys.All)
            {
                Assert.True(theme.Colors.ContainsKey(key), $"{theme.Name} missing {key}");
                Assert.True(ThemeColor.TryParse(theme.Colors[key], out _), $"{theme.Name} {key} = {theme.Colors[key]}");
            }
        }
        Assert.Equal(ThemeBase.Light, ThemeService.BuiltInLight.Base);
        Assert.Equal(ThemeBase.Dark, ThemeService.BuiltInDark.Base);
    }

    [Fact]
    public void Key_set_covers_spec_groups()
    {
        foreach (var k in new[]
                 {
                     "app.window", "app.sidebar", "app.tab", "app.toolbar", "app.accent",
                     "editor.background", "editor.foreground", "editor.keyword", "editor.string", "editor.number",
                     "editor.comment", "editor.type", "editor.function", "editor.operator", "editor.selection",
                     "editor.currentQueryBackground", "editor.errorUnderline", "editor.caret", "editor.lineNumbers",
                     "grid.background", "grid.altRowBackground", "grid.lines", "grid.headerBackground", "grid.selectionBackground",
                     "grid.cellSelectionBorder", "grid.cellSelectionBackground",
                     "grid.pendingNewBackground", "grid.pendingModifiedBackground", "grid.pendingDeletedBackground", "grid.nullForeground",
                 })
            Assert.Contains(k, ThemeKeys.All);
    }

    [Theory]
    [InlineData("#112233", 255, 0x11, 0x22, 0x33)]
    [InlineData("#80112233", 0x80, 0x11, 0x22, 0x33)]
    [InlineData("#abc", 255, 0xAA, 0xBB, 0xCC)]
    public void ThemeColor_parses(string text, byte a, byte r, byte g, byte b)
    {
        Assert.True(ThemeColor.TryParse(text, out var c));
        Assert.Equal((a, r, g, b), (c.A, c.R, c.G, c.B));
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    public void ThemeColor_rejects_bad_values(string text) => Assert.False(ThemeColor.TryParse(text, out _));

    [Fact]
    public void Auto_mode_follows_system_flag()
    {
        using var tmp = new TempDir();
        var svc = new ThemeService(tmp.Paths);
        var settings = new ThemeSettings { Mode = ThemeMode.Auto };
        Assert.Equal("Dark", svc.Resolve(settings, systemIsDark: true).Name);
        Assert.Equal("Light", svc.Resolve(settings, systemIsDark: false).Name);
        Assert.Equal("Dark", svc.Resolve(settings with { Mode = ThemeMode.Dark }, systemIsDark: false).Name);
        Assert.Equal("Light", svc.Resolve(settings with { Mode = ThemeMode.Light }, systemIsDark: true).Name);
    }

    [Fact]
    public void Overrides_apply_on_top_of_active_theme()
    {
        using var tmp = new TempDir();
        var svc = new ThemeService(tmp.Paths);
        var settings = new ThemeSettings
        {
            Mode = ThemeMode.Light,
            Overrides = new Dictionary<string, string> { ["editor.keyword"] = "#FF0000", ["bad.key"] = "nope" },
        };
        var t = svc.Resolve(settings, false);
        Assert.Equal("#FF0000", t.Colors["editor.keyword"]);
        Assert.Equal(ThemeService.BuiltInLight.Colors["editor.string"], t.Colors["editor.string"]);
    }

    [Fact]
    public void Invalid_override_color_is_ignored_with_warning()
    {
        using var tmp = new TempDir();
        var svc = new ThemeService(tmp.Paths);
        var t = svc.Resolve(new ThemeSettings { Mode = ThemeMode.Light, Overrides = new() { ["editor.keyword"] = "nope" } }, false);
        Assert.Equal(ThemeService.BuiltInLight.Colors["editor.keyword"], t.Colors["editor.keyword"]);
        Assert.NotEmpty(svc.Warnings);
    }

    [Fact]
    public void User_theme_is_loaded_with_nested_groups_and_falls_back_to_base()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.Paths.ThemesDir);
        File.WriteAllText(Path.Combine(tmp.Paths.ThemesDir, "solar.json"), """
            {
              "name": "Solar",
              "base": "dark",
              "colors": {
                "editor": { "keyword": "#B58900", "background": "#002B36" },
                "grid.nullForeground": "#586E75"
              }
            }
            """);
        var svc = new ThemeService(tmp.Paths);
        Assert.Contains("Solar", svc.AvailableThemes);
        Assert.Contains("Light", svc.AvailableThemes);
        var t = svc.Get("Solar");
        Assert.Equal(ThemeBase.Dark, t.Base);
        Assert.Equal("#B58900", t.Colors["editor.keyword"]);
        Assert.Equal("#586E75", t.Colors["grid.nullForeground"]);
        Assert.Equal(ThemeService.BuiltInDark.Colors["editor.string"], t.Colors["editor.string"]);

        var resolved = svc.Resolve(new ThemeSettings { Mode = ThemeMode.Dark, Dark = "Solar" }, false);
        Assert.Equal("Solar", resolved.Name);
    }

    [Fact]
    public void Unknown_theme_name_falls_back_to_builtin_for_mode()
    {
        using var tmp = new TempDir();
        var svc = new ThemeService(tmp.Paths);
        var t = svc.Resolve(new ThemeSettings { Mode = ThemeMode.Dark, Dark = "Missing" }, false);
        Assert.Equal("Dark", t.Name);
        Assert.NotEmpty(svc.Warnings);
    }

    [Fact]
    public void Broken_user_theme_file_is_skipped_with_warning()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.Paths.ThemesDir);
        File.WriteAllText(Path.Combine(tmp.Paths.ThemesDir, "broken.json"), "{ nope");
        var svc = new ThemeService(tmp.Paths);
        Assert.DoesNotContain("broken", svc.AvailableThemes);
        Assert.NotEmpty(svc.Warnings);
    }

    [Fact]
    public void Theme_name_defaults_to_file_name()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.Paths.ThemesDir);
        File.WriteAllText(Path.Combine(tmp.Paths.ThemesDir, "Mint.json"), """{ "base": "light", "colors": {} }""");
        var svc = new ThemeService(tmp.Paths);
        Assert.Equal(ThemeBase.Light, svc.Get("Mint").Base);
        Assert.Equal(ThemeService.BuiltInLight.Colors["app.window"], svc.Get("mint").Colors["app.window"]);
    }
}
