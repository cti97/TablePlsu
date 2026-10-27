using System.Text.Json;
using System.Text.Json.Nodes;
using TablePlsu.Core.Connections;
using TablePlsu.Core.Settings;
using TablePlsu.Tests.Infrastructure;

namespace TablePlsu.Tests.Settings;

public class SettingsServiceTests
{
    [Fact]
    public void Defaults_load_without_user_file()
    {
        using var tmp = new TempDir();
        using var svc = new SettingsService(tmp.Paths);
        svc.Load();
        var s = svc.Current;
        Assert.Null(svc.LastError);

        Assert.True(s.General.ReopenWorkspacesOnStartup);
        Assert.Equal(NewTabOpens.SqlEditor, s.General.NewTabOpens);
        Assert.False(s.General.KeepRunningInTray);
        Assert.False(s.General.StartWithWindows);

        Assert.Equal("Cascadia Mono", s.Editor.FontFamily);
        Assert.Equal(12, s.Editor.FontSize);
        Assert.InRange(s.Editor.LineHeight, 1.0, 5.0);
        Assert.Equal(4, s.Editor.TabSize);
        Assert.True(s.Editor.InsertSpaces);
        Assert.True(s.Editor.WordWrap);
        Assert.False(s.Editor.ShowInvisibles);
        Assert.True(s.Editor.HighlightCurrentQuery);
        Assert.Equal(CompleteKey.Both, s.Editor.CompleteKey);
        Assert.True(s.Editor.Autocomplete.Tables);
        Assert.True(s.Editor.Autocomplete.Functions);
        Assert.True(s.Editor.Autocomplete.Keywords);
        Assert.False(s.Editor.Autocomplete.Disabled);
        Assert.False(s.Editor.Autocomplete.AddSpace);
        Assert.True(s.Editor.Autocomplete.PrefixSchema);
        Assert.True(s.Editor.Autocomplete.UppercaseKeywords);
        Assert.True(s.Editor.Autocomplete.AutoClosePairs);
        Assert.Equal(AutoSaveTabs.All, s.Editor.AutoSaveTabs);
        Assert.Equal(500, s.Editor.AutoSaveDebounceMs);
        Assert.False(s.Editor.ReturnOnError);
        Assert.Equal(ResultViewMode.Split, s.Editor.ResultViewMode);
        Assert.Equal(1000, s.Editor.DefaultRowLimit);

        Assert.Equal("NULL", s.Grid.NullDisplay);
        Assert.False(string.IsNullOrEmpty(s.Grid.FontFamily));
        Assert.True(s.Grid.FontSize > 0);
        Assert.False(string.IsNullOrEmpty(s.Grid.DateFormat));

        Assert.True(s.Sidebar.LazyLoadSchemas);
        Assert.False(s.Sidebar.LoadSystemSchemas);
        Assert.Equal(SidebarMatching.Fuzzy, s.Sidebar.Matching);

        Assert.Equal(300, s.Table.PageSize);
        Assert.Equal(500_000, s.Table.EstimateAboveRows);
        Assert.True(s.Connections.KeepAlive);
        Assert.Equal(0, s.Connections.CommandTimeoutSeconds);
        Assert.Equal(SafeModeLevel.Silent, s.Security.DefaultSafeMode);
        Assert.True(s.History.Enabled);
        Assert.Equal(10_000, s.History.MaxEntries);

        Assert.Equal(ThemeMode.Auto, s.Theme.Mode);
        Assert.Equal("Light", s.Theme.Light);
        Assert.Equal("Dark", s.Theme.Dark);
        Assert.Empty(s.Theme.Overrides);

        Assert.Empty(s.Keymap);
        Assert.Contains("production", s.Tags.Keys);
        Assert.Contains("local", s.Tags.Keys);
        Assert.NotEmpty(s.Toolbar.Items);
    }

    [Fact]
    public void Resident_mode_keys_are_read_from_user_settings()
    {
        using var tmp = new TempDir();
        tmp.Paths.EnsureCreated();
        File.WriteAllText(tmp.Paths.SettingsFile, """{ "general": { "keepRunningInTray": true, "startWithWindows": true } }""");
        using var svc = new SettingsService(tmp.Paths);
        svc.Load();
        Assert.Null(svc.LastError);
        Assert.True(svc.Current.General.KeepRunningInTray);
        Assert.True(svc.Current.General.StartWithWindows);
        Assert.True(svc.Current.General.ReopenWorkspacesOnStartup);
    }

    [Fact]
    public void User_settings_override_key_by_key()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.Paths.Roaming);
        File.WriteAllText(tmp.Paths.SettingsFile, """
            {
              // comments are allowed
              "editor": { "fontSize": 16, "autocomplete": { "keywords": false } },
              "theme": { "mode": "dark", "overrides": { "editor.keyword": "#FF0000" } },
              "keymap": { "query.runCurrent": "F5", "tab.close": null, "tab.next": ["Ctrl+Tab", "Ctrl+PageDown"] },
              "tags": { "qa": "#00FF00" },
            }
            """);
        using var svc = new SettingsService(tmp.Paths);
        svc.Load();
        var s = svc.Current;
        Assert.Null(svc.LastError);
        Assert.Equal(16, s.Editor.FontSize);
        Assert.Equal("Cascadia Mono", s.Editor.FontFamily); // untouched sibling keeps default
        Assert.False(s.Editor.Autocomplete.Keywords);
        Assert.True(s.Editor.Autocomplete.Tables);
        Assert.Equal(ThemeMode.Dark, s.Theme.Mode);
        Assert.Equal("#FF0000", s.Theme.Overrides["editor.keyword"]);
        Assert.Equal(["F5"], s.Keymap["query.runCurrent"]!);
        Assert.True(s.Keymap.ContainsKey("tab.close"));
        Assert.Null(s.Keymap["tab.close"]);
        Assert.Equal(["Ctrl+Tab", "Ctrl+PageDown"], s.Keymap["tab.next"]!);
        Assert.Equal("#00FF00", s.Tags["qa"]);
        Assert.Contains("production", s.Tags.Keys); // merged with defaults
    }

    [Fact]
    public void Null_for_a_defaulted_key_keeps_default()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.Paths.Roaming);
        File.WriteAllText(tmp.Paths.SettingsFile, """{ "editor": { "fontSize": null } }""");
        using var svc = new SettingsService(tmp.Paths);
        svc.Load();
        Assert.Null(svc.LastError);
        Assert.Equal(12, svc.Current.Editor.FontSize);
    }

    [Fact]
    public void Out_of_range_values_are_clamped()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.Paths.Roaming);
        File.WriteAllText(tmp.Paths.SettingsFile, """{ "editor": { "fontSize": 500, "lineHeight": 0.2 } }""");
        using var svc = new SettingsService(tmp.Paths);
        svc.Load();
        Assert.Equal(100, svc.Current.Editor.FontSize);
        Assert.Equal(1.0, svc.Current.Editor.LineHeight);
    }

    [Fact]
    public void Bad_json_keeps_last_good_config_and_sets_LastError()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.Paths.Roaming);
        File.WriteAllText(tmp.Paths.SettingsFile, """{ "editor": { "fontSize": 14 } }""");
        using var svc = new SettingsService(tmp.Paths);
        svc.Load();
        Assert.Equal(14, svc.Current.Editor.FontSize);

        File.WriteAllText(tmp.Paths.SettingsFile, """{ "editor": { "fontSize": 18 """);
        svc.Reload();
        Assert.NotNull(svc.LastError);
        Assert.Equal(14, svc.Current.Editor.FontSize);

        File.WriteAllText(tmp.Paths.SettingsFile, """{ "editor": { "fontSize": 18 } }""");
        svc.Reload();
        Assert.Null(svc.LastError);
        Assert.Equal(18, svc.Current.Editor.FontSize);
    }

    [Fact]
    public void Wrong_type_or_enum_value_is_an_error()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.Paths.Roaming);
        File.WriteAllText(tmp.Paths.SettingsFile, """{ "theme": { "mode": "purple" } }""");
        using var svc = new SettingsService(tmp.Paths);
        svc.Load();
        Assert.NotNull(svc.LastError);
        Assert.Equal(ThemeMode.Auto, svc.Current.Theme.Mode); // falls back to defaults on first load
    }

    [Fact]
    public void Bad_file_on_first_load_falls_back_to_defaults()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.Paths.Roaming);
        File.WriteAllText(tmp.Paths.SettingsFile, "not json");
        using var svc = new SettingsService(tmp.Paths);
        svc.Load();
        Assert.NotNull(svc.LastError);
        Assert.Equal(12, svc.Current.Editor.FontSize);
    }

    [Fact]
    public void Changed_event_fires_on_reload_with_new_settings()
    {
        using var tmp = new TempDir();
        using var svc = new SettingsService(tmp.Paths);
        svc.Load();
        AppSettings? received = null;
        svc.Changed += (_, e) => received = e.Settings;
        Directory.CreateDirectory(tmp.Paths.Roaming);
        File.WriteAllText(tmp.Paths.SettingsFile, """{ "table": { "pageSize": 50 } }""");
        svc.Reload();
        Assert.Equal(50, received?.Table.PageSize);
    }

    [Fact]
    public async Task FileSystemWatcher_hot_reloads()
    {
        using var tmp = new TempDir();
        using var svc = new SettingsService(tmp.Paths);
        svc.Load();
        svc.StartWatching();
        var tcs = new TaskCompletionSource<AppSettings>(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.Changed += (_, e) => { if (e.Settings.Table.PageSize == 77) tcs.TrySetResult(e.Settings); };

        TablePlsu.Core.IO.AtomicFile.WriteAllText(tmp.Paths.SettingsFile, """{ "table": { "pageSize": 77 } }""");
        var done = await Task.WhenAny(tcs.Task, Task.Delay(5000));
        Assert.Same(tcs.Task, done);
        Assert.Equal(77, svc.Current.Table.PageSize);
    }

    [Fact]
    public void SaveUserJson_writes_atomically_and_reloads()
    {
        using var tmp = new TempDir();
        using var svc = new SettingsService(tmp.Paths);
        svc.Load();
        svc.SaveUserJson("""{ "grid": { "nullDisplay": "∅" } }""");
        Assert.Equal("∅", svc.Current.Grid.NullDisplay);
        Assert.Contains("nullDisplay", File.ReadAllText(tmp.Paths.SettingsFile));
    }

    [Fact]
    public void SetValue_updates_one_key_in_user_file_and_preserves_others()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.Paths.Roaming);
        File.WriteAllText(tmp.Paths.SettingsFile, """{ "table": { "pageSize": 50 } }""");
        using var svc = new SettingsService(tmp.Paths);
        svc.Load();
        svc.SetValue("editor.fontSize", JsonValue.Create(20));
        Assert.Equal(20, svc.Current.Editor.FontSize);
        Assert.Equal(50, svc.Current.Table.PageSize);
        var json = JsonNode.Parse(File.ReadAllText(tmp.Paths.SettingsFile))!;
        Assert.Equal(20, json["editor"]!["fontSize"]!.GetValue<int>());
        Assert.Equal(50, json["table"]!["pageSize"]!.GetValue<int>());
    }

    [Fact]
    public void SetValue_keeps_comments_and_formatting_of_the_user_file()
    {
        // Ctrl+wheel zoom persists editor.fontSize through SetValue; it must not wipe the user's comments.
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.Paths.Roaming);
        var original = SettingsJsonEdits.StarterText.Replace("{\n}\n", "{\n  // my font\n  \"editor\": { \"fontSize\": 13, \"wordWrap\": false },\n  \"table\": { \"pageSize\": 50, }, // trailing comma\n}\n");
        File.WriteAllText(tmp.Paths.SettingsFile, original);
        using var svc = new SettingsService(tmp.Paths);
        svc.Load();
        svc.SetValue("editor.fontSize", JsonValue.Create(20));
        svc.SetValue("table.estimateAboveRows", JsonValue.Create(7));
        svc.SetValue("grid.nullDisplay", JsonValue.Create("<null>"));
        Assert.Null(svc.LastError);
        Assert.Equal(20, svc.Current.Editor.FontSize);
        Assert.False(svc.Current.Editor.WordWrap);
        Assert.Equal(50, svc.Current.Table.PageSize);
        Assert.Equal(7, svc.Current.Table.EstimateAboveRows);
        Assert.Equal("<null>", svc.Current.Grid.NullDisplay);
        var text = File.ReadAllText(tmp.Paths.SettingsFile);
        Assert.StartsWith("// TablePlsu settings.", text);
        Assert.Contains("// my font", text);
        Assert.Contains("// trailing comma", text);
        Assert.Contains("\"editor\": { \"fontSize\": 20, \"wordWrap\": false }", text);
    }

    [Theory]
    [InlineData("{}", "editor.fontSize", "14", "{\n  \"editor\": {\n    \"fontSize\": 14\n  }\n}")]
    [InlineData("{\n  \"a\": 1\n}", "editor.fontSize", "14", "{\n  \"a\": 1,\n  \"editor\": {\n    \"fontSize\": 14\n  }\n}")]
    [InlineData("{ \"editor\": {} }", "editor.fontSize", "14", "{ \"editor\": {\n    \"fontSize\": 14\n  } }")]
    [InlineData("{ \"editor\": 5 }", "editor.fontSize", "14", "{ \"editor\": {\n    \"fontSize\": 14\n  } }")]
    [InlineData("{\n  \"editor\": {\n    \"x\": 1, // c\n  }\n}", "editor.fontSize", "14", "{\n  \"editor\": {\n    \"x\": 1,\n    \"fontSize\": 14, // c\n  }\n}")]
    public void SetValueText_edits_text_in_place(string text, string key, string value, string expected)
    {
        var result = SettingsJsonEdits.SetValue(text, key, value);
        Assert.Equal(expected, result);
        using var _ = JsonDocument.Parse(result!, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
    }

    [Fact]
    public void Embedded_defaults_and_schema_are_valid_json()
    {
        using var defaults = JsonDocument.Parse(SettingsService.DefaultsJson, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        using var schema = JsonDocument.Parse(SettingsService.SchemaJson);
        Assert.Equal(JsonValueKind.Object, defaults.RootElement.ValueKind);
        Assert.True(schema.RootElement.TryGetProperty("properties", out var props));
        foreach (var section in new[] { "general", "editor", "grid", "sidebar", "table", "connections", "security", "history", "theme", "keymap", "tags", "toolbar" })
        {
            Assert.True(defaults.RootElement.TryGetProperty(section, out _), "defaults missing " + section);
            Assert.True(props.TryGetProperty(section, out _), "schema missing " + section);
        }
    }

    [Fact]
    public void Schema_lists_every_default_leaf_key()
    {
        using var defaults = JsonDocument.Parse(SettingsService.DefaultsJson, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        using var schema = JsonDocument.Parse(SettingsService.SchemaJson);
        void Walk(JsonElement d, JsonElement s, string path)
        {
            if (!s.TryGetProperty("properties", out var props)) return; // free-form maps (keymap, tags, overrides)
            foreach (var p in d.EnumerateObject())
            {
                Assert.True(props.TryGetProperty(p.Name, out var child), $"schema missing {path}{p.Name}");
                if (p.Value.ValueKind == JsonValueKind.Object) Walk(p.Value, child, path + p.Name + ".");
            }
        }
        Walk(defaults.RootElement, schema.RootElement, "");
    }
}
