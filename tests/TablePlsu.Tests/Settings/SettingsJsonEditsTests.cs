using TablePlsu.Core.Settings;
using TablePlsu.Core.Themes;

namespace TablePlsu.Tests.Settings;

public class SettingsJsonEditsTests
{
    [Fact]
    public void Inserts_section_into_empty_root()
    {
        var edit = SettingsJsonEdits.EnsureObjectSection("{\n}\n", "keymap")!;
        Assert.True(edit.Changed);
        Assert.Equal("{\n  \"keymap\": {\n    \n  }\n}\n", edit.Text);
        Assert.Equal("{\n  \"keymap\": {\n    ".Length, edit.SelectionStart);
        Assert.Empty(SettingsValidator.ValidateText(edit.Text));
    }

    [Fact]
    public void Inserts_section_into_compact_root()
    {
        var edit = SettingsJsonEdits.EnsureObjectSection("{}", "keymap")!;
        Assert.Equal("{\n  \"keymap\": {\n    \n  }\n}", edit.Text);
        Assert.Empty(SettingsValidator.ValidateText(edit.Text));
    }

    [Fact]
    public void Inserts_first_with_comma_when_root_has_keys_and_keeps_comments()
    {
        var text = SettingsJsonEdits.StarterText.Replace("{\n}", "{\n  // mine\n  \"editor\": { \"fontSize\": 14 }\n}");
        var edit = SettingsJsonEdits.EnsureObjectSection(text, "keymap")!;
        Assert.Contains("// mine", edit.Text);
        Assert.Contains("\"keymap\": {\n    \n  },", edit.Text);
        Assert.Empty(SettingsValidator.ValidateText(edit.Text));
        Assert.Equal(14, SettingsService.Parse(edit.Text).Editor.FontSize);
        // Caret sits on the empty line inside the new object.
        Assert.Equal('\n', edit.Text[edit.SelectionStart]);
    }

    [Fact]
    public void Selects_existing_section_contents()
    {
        var text = "{\n  \"keymap\": {\n    \"tab.next\": \"Ctrl+]\"\n  }\n}";
        var edit = SettingsJsonEdits.EnsureObjectSection(text, "keymap")!;
        Assert.False(edit.Changed);
        Assert.Equal(text, edit.Text);
        Assert.Equal("\"tab.next\": \"Ctrl+]\"", text.Substring(edit.SelectionStart, edit.SelectionLength));
    }

    [Fact]
    public void Opens_up_existing_empty_section()
    {
        var edit = SettingsJsonEdits.EnsureObjectSection("{ \"keymap\": {} }", "keymap")!;
        Assert.True(edit.Changed);
        Assert.Equal("{ \"keymap\": {\n    \n  } }", edit.Text);
        Assert.Equal("{ \"keymap\": {\n    ".Length, edit.SelectionStart);
    }

    [Fact]
    public void Returns_null_for_invalid_json()
    {
        Assert.Null(SettingsJsonEdits.EnsureObjectSection("{ \"a\": ", "keymap"));
        Assert.Null(SettingsJsonEdits.EnsureObjectSection("[]", "keymap"));
    }

    [Fact]
    public void Positions_handle_non_ascii_text()
    {
        var text = "{\n  // héllo wörld ✓\n  \"tags\": { \"prød\": \"#FFF\" },\n  \"keymap\": { \"tab.next\": \"Ctrl+]\" }\n}";
        var edit = SettingsJsonEdits.EnsureObjectSection(text, "keymap")!;
        Assert.Equal("\"tab.next\": \"Ctrl+]\"", text.Substring(edit.SelectionStart, edit.SelectionLength));
    }
}

public class BuiltInThemeTests
{
    [Theory]
    [InlineData("Nord", true)]
    [InlineData("Solarized Light", false)]
    public void Extra_built_in_themes_resolve_with_every_key(string name, bool dark)
    {
        using var tmp = new Infrastructure.TempDir();
        var svc = new ThemeService(tmp.Paths);
        Assert.Contains(name, svc.AvailableThemes);
        var settings = new ThemeSettings { Mode = dark ? ThemeMode.Dark : ThemeMode.Light, Dark = name, Light = name };
        var theme = svc.Resolve(settings, systemIsDark: false);
        Assert.Equal(name, theme.Name);
        Assert.Equal(dark, theme.IsDark);
        Assert.Empty(svc.Warnings);
        foreach (var key in ThemeKeys.All)
        {
            Assert.True(theme.Colors.ContainsKey(key), key);
            Assert.True(ThemeColor.TryParse(theme.Colors[key], out _), key);
        }
        // Distinct from the base theme.
        Assert.NotEqual((dark ? ThemeService.BuiltInDark : ThemeService.BuiltInLight).Colors["app.window"], theme.Colors["app.window"]);
    }

    [Fact]
    public void Built_in_theme_json_files_define_every_key_explicitly()
    {
        foreach (var file in new[] { "nord.json", "solarized-light.json" })
        {
            var asm = typeof(ThemeService).Assembly;
            using var s = asm.GetManifestResourceStream("TablePlsu.Core.Themes." + file)!;
            var text = new StreamReader(s).ReadToEnd();
            var parsed = ThemeService.ParseThemeFile(text, file);
            var root = System.Text.Json.Nodes.JsonNode.Parse(text)!["colors"]!.AsObject();
            var count = root.Sum(g => g.Value!.AsObject().Count);
            Assert.Equal(ThemeKeys.All.Count, count);
            Assert.Equal(ThemeKeys.All.Count, parsed.Colors.Count);
        }
    }

    [Fact]
    public void User_theme_with_built_in_name_wins()
    {
        using var tmp = new Infrastructure.TempDir();
        Directory.CreateDirectory(tmp.Paths.ThemesDir);
        File.WriteAllText(Path.Combine(tmp.Paths.ThemesDir, "nord.json"), "{ \"name\": \"Nord\", \"base\": \"dark\", \"colors\": { \"app.window\": \"#010203\" } }");
        var svc = new ThemeService(tmp.Paths);
        Assert.Equal("#010203", svc.Find("Nord")!.Colors["app.window"]);
        Assert.Single(svc.AvailableThemes, n => n == "Nord");
    }
}
