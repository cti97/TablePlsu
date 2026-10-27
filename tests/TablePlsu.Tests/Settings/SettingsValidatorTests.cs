using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using TablePlsu.Core.Commands;
using TablePlsu.Core.Settings;
using TablePlsu.Core.Themes;

namespace TablePlsu.Tests.Settings;

public class SettingsValidatorTests
{
    private static IReadOnlyList<SettingsDiagnostic> V(string text) => SettingsValidator.ValidateText(text);

    [Fact]
    public void Empty_and_starter_files_are_valid()
    {
        Assert.Empty(V(""));
        Assert.Empty(V("{}"));
        Assert.Empty(V(SettingsJsonEdits.StarterText));
    }

    [Fact]
    public void Embedded_defaults_validate_cleanly()
    {
        Assert.Empty(V(SettingsService.DefaultsJson));
    }

    [Fact]
    public void Resident_mode_keys_are_known_booleans()
    {
        Assert.Empty(V("{ \"general\": { \"keepRunningInTray\": true, \"startWithWindows\": false } }"));
        Assert.Equal("general.keepRunningInTray", Assert.Single(V("{ \"general\": { \"keepRunningInTray\": \"yes\" } }")).Path);
        Assert.Equal("general.startWithWindows", Assert.Single(V("{ \"general\": { \"startWithWindows\": 1 } }")).Path);
    }

    [Fact]
    public void Syntax_error_reports_line_and_column()
    {
        var d = Assert.Single(V("{\n  \"editor\": {\n    \"fontSize\": 12,,\n  }\n}"));
        Assert.True(d.IsError);
        Assert.Equal(3, d.Line);
    }

    [Fact]
    public void Wrong_type_is_an_error_at_the_value()
    {
        var d = Assert.Single(V("{\n  \"editor\": { \"fontSize\": \"big\" }\n}"));
        Assert.True(d.IsError);
        Assert.Equal("editor.fontSize", d.Path);
        Assert.Equal(2, d.Line);
        Assert.Equal(27, d.Column);
        Assert.Contains("a number", d.Message);
    }

    [Fact]
    public void Integer_type_rejects_decimals()
    {
        var d = Assert.Single(V("{ \"editor\": { \"tabSize\": 2.5 } }"));
        Assert.Equal("editor.tabSize", d.Path);
        Assert.Empty(V("{ \"editor\": { \"tabSize\": 2 } }"));
    }

    [Fact]
    public void Minimum_and_maximum_are_checked()
    {
        var d = Assert.Single(V("{ \"editor\": { \"fontSize\": 200 } }"));
        Assert.Contains("≤ 100", d.Message);
        d = Assert.Single(V("{ \"editor\": { \"fontSize\": 5 } }"));
        Assert.Contains("≥ 9", d.Message);
        d = Assert.Single(V("{ \"grid\": { \"fontSize\": 5 } }")); // via $ref
        Assert.Contains("≥ 6", d.Message);
    }

    [Fact]
    public void Enum_values_are_checked()
    {
        var d = Assert.Single(V("{ \"theme\": { \"mode\": \"night\" } }"));
        Assert.True(d.IsError);
        Assert.Contains("\"auto\"", d.Message);
    }

    [Fact]
    public void Unknown_keys_are_warnings_with_suggestions()
    {
        var d = Assert.Single(V("{\n  \"editor\": {\n    \"fontSzie\": 14\n  }\n}"));
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Equal("editor.fontSzie", d.Path);
        Assert.Equal(3, d.Line);
        Assert.Equal(5, d.Column);
        Assert.Contains("fontSize", d.Message);

        d = Assert.Single(V("{ \"bogus\": 1 }"));
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
    }

    [Fact]
    public void Colours_are_checked_by_pattern()
    {
        var d = Assert.Single(V("{ \"tags\": { \"prod\": \"red\" } }"));
        Assert.True(d.IsError);
        Assert.Contains("#RRGGBB", d.Message);
        Assert.Empty(V("{ \"tags\": { \"prod\": \"#FF0000\" } }"));
    }

    [Fact]
    public void Theme_override_keys_must_be_known_colour_keys()
    {
        Assert.Empty(V("{ \"theme\": { \"overrides\": { \"editor.keyword\": \"#FF0000\" } } }"));
        var d = Assert.Single(V("{ \"theme\": { \"overrides\": { \"editor.keywrd\": \"#FF0000\" } } }"));
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Contains("editor.keyword", d.Message);
    }

    [Fact]
    public void Keymap_accepts_string_array_and_null()
    {
        Assert.Empty(V("""{ "keymap": { "query.runCurrent": "Ctrl+R", "tab.next": ["Ctrl+PageDown"], "tab.prev": null } }"""));
    }

    [Fact]
    public void Keymap_unknown_command_is_a_warning()
    {
        var d = Assert.Single(V("""{ "keymap": { "query.runCurent": "Ctrl+Enter" } }"""));
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Contains("query.runCurrent", d.Message);
    }

    [Fact]
    public void Keymap_wrong_value_type_is_an_error()
    {
        var d = Assert.Single(V("""{ "keymap": { "tab.next": 5 } }"""));
        Assert.True(d.IsError);
        Assert.Contains("a string", d.Message);
    }

    [Fact]
    public void Keymap_bad_chords_and_conflicts_are_warnings_on_their_line()
    {
        var text = "{\n  \"keymap\": {\n    \"tab.next\": \"Q\",\n    \"tab.prev\": \"Ctrl+Bogus+\",\n    \"tab.new\": \"Ctrl+W\"\n  }\n}";
        var ds = V(text);
        Assert.All(ds, d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
        Assert.Contains(ds, d => d.Line == 3 && d.Path == "keymap.tab.next" && d.Message.Contains("Ctrl or Alt"));
        Assert.Contains(ds, d => d.Line == 4 && d.Path == "keymap.tab.prev");
        Assert.Contains(ds, d => d.Message.Contains("Ctrl+W", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Duplicate_keys_warn()
    {
        var ds = V("{ \"editor\": { \"fontSize\": 12, \"fontSize\": 13 } }");
        var d = Assert.Single(ds);
        Assert.Contains("duplicate", d.Message);
    }

    [Fact]
    public void Diagnostics_are_sorted_by_position()
    {
        var ds = V("{\n \"zzz\": 1,\n \"editor\": { \"fontSize\": \"x\" },\n \"aaa\": 2\n}");
        Assert.Equal(ds.OrderBy(d => d.Line).Select(d => d.Line), ds.Select(d => d.Line));
        Assert.Equal(3, ds.Count);
    }

    // ---- schema coverage ----

    private static JsonObject SchemaRoot => (JsonObject)JsonNode.Parse(SettingsService.SchemaJson)!;

    [Fact]
    public void Schema_covers_every_AppSettings_property()
    {
        var missing = new List<string>();
        CheckType(typeof(AppSettings), SchemaRoot, "", missing);
        Assert.Empty(missing);
    }

    private static void CheckType(Type type, JsonObject schema, string path, List<string> missing)
    {
        var props = schema["properties"] as JsonObject;
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || p.Name == "EqualityContract") continue;
            var name = JsonNamingPolicy.CamelCase.ConvertName(p.Name);
            var key = path.Length == 0 ? name : path + "." + name;
            if (props?[name] is not JsonObject child)
            {
                missing.Add(key);
                continue;
            }
            var t = p.PropertyType;
            if (t.IsClass && t != typeof(string) && t.Namespace == typeof(AppSettings).Namespace && !t.IsGenericType)
                CheckType(t, child, key, missing);
        }
    }

    [Fact]
    public void Schema_keymap_enum_matches_CommandIds()
    {
        var ids = SchemaRoot["$defs"]!["commandId"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(CommandIds.All.Select(c => c.Id).OrderBy(x => x, StringComparer.Ordinal), ids.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void Schema_theme_key_enum_matches_ThemeKeys()
    {
        var keys = SchemaRoot["$defs"]!["themeKey"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(ThemeKeys.All.OrderBy(x => x, StringComparer.Ordinal), keys.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void Schema_enums_match_csharp_enums()
    {
        static List<string> Names<T>() where T : struct, Enum => Enum.GetNames<T>().Select(JsonNamingPolicy.CamelCase.ConvertName).ToList();
        static List<string> SchemaEnum(string path)
        {
            JsonNode? n = SchemaRoot;
            foreach (var part in path.Split('.')) n = n!["properties"]![part];
            return n!["enum"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        }
        Assert.Equal(Names<NewTabOpens>(), SchemaEnum("general.newTabOpens"));
        Assert.Equal(Names<CompleteKey>(), SchemaEnum("editor.completeKey"));
        Assert.Equal(Names<AutoSaveTabs>(), SchemaEnum("editor.autoSaveTabs"));
        Assert.Equal(Names<ResultViewMode>(), SchemaEnum("editor.resultViewMode"));
        Assert.Equal(Names<SidebarMatching>(), SchemaEnum("sidebar.matching"));
        Assert.Equal(Names<ThemeMode>(), SchemaEnum("theme.mode"));
        Assert.Equal(Names<TablePlsu.Core.Connections.SafeModeLevel>(), SchemaEnum("security.defaultSafeMode"));
    }
}
