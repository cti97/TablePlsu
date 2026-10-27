using System.Text.Json;
using System.Text.Json.Nodes;
using TablePlsu.Core.Paths;
using TablePlsu.Core.Settings;

namespace TablePlsu.Core.Themes;

/// <summary>
/// Built-in Light/Dark themes (embedded JSON) plus user themes from <c>themes\*.json</c>. A theme file is
/// <c>{ "name", "base": "light"|"dark", "colors": { "editor": { "keyword": "#…" }, "grid.nullForeground": "#…" } }</c>;
/// nested groups and flat dotted keys both work, and missing keys come from the built-in theme of the same base.
/// <see cref="Resolve"/> picks the theme for the settings (Auto uses the caller's system-dark flag) and applies overrides.
/// </summary>
public sealed class ThemeService
{
    private static readonly Lazy<ThemeDefinition> Light = new(() => LoadBuiltIn("TablePlsu.Core.Themes.light.json", "Light", ThemeBase.Light));
    private static readonly Lazy<ThemeDefinition> Dark = new(() => LoadBuiltIn("TablePlsu.Core.Themes.dark.json", "Dark", ThemeBase.Dark));

    private readonly AppPaths _paths;
    private readonly Dictionary<string, ThemeDefinition> _themes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _warnings = [];

    public ThemeService(AppPaths paths)
    {
        _paths = paths;
        Reload();
    }

    public static ThemeDefinition BuiltInLight => Light.Value;
    public static ThemeDefinition BuiltInDark => Dark.Value;

    public IReadOnlyList<string> AvailableThemes => _themes.Values.Select(t => t.Name).ToList();

    /// <summary>Problems found while loading user themes or resolving the last settings.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Re-scans the user themes folder.</summary>
    public void Reload()
    {
        _themes.Clear();
        _warnings.Clear();
        _themes[BuiltInLight.Name] = BuiltInLight;
        _themes[BuiltInDark.Name] = BuiltInDark;

        if (!Directory.Exists(_paths.ThemesDir)) return;
        foreach (var file in Directory.EnumerateFiles(_paths.ThemesDir, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var theme = ParseThemeFile(File.ReadAllText(file), Path.GetFileNameWithoutExtension(file), _warnings);
                _themes[theme.Name] = theme;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _warnings.Add($"Theme '{Path.GetFileName(file)}' skipped: {ex.Message}");
            }
        }
    }

    public ThemeDefinition? Find(string name) => _themes.GetValueOrDefault(name);

    /// <summary>The named theme, or the built-in Light theme when it does not exist.</summary>
    public ThemeDefinition Get(string name) => Find(name) ?? BuiltInLight;

    /// <summary>Picks the active theme for <paramref name="settings"/> and applies <see cref="ThemeSettings.Overrides"/>.</summary>
    public ThemeDefinition Resolve(ThemeSettings settings, bool systemIsDark)
    {
        _warnings.RemoveAll(w => w.StartsWith("theme.", StringComparison.Ordinal));
        var dark = settings.Mode switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => systemIsDark,
        };
        var name = dark ? settings.Dark : settings.Light;
        var theme = Find(name);
        if (theme is null)
        {
            _warnings.Add($"theme.{(dark ? "dark" : "light")}: theme '{name}' not found; using built-in.");
            theme = dark ? BuiltInDark : BuiltInLight;
        }

        if (settings.Overrides.Count == 0) return theme;
        var colors = new Dictionary<string, string>(theme.Colors, StringComparer.Ordinal);
        foreach (var (key, value) in settings.Overrides)
        {
            if (!ThemeColor.TryParse(value, out _))
            {
                _warnings.Add($"theme.overrides.{key}: '{value}' is not a colour (#RRGGBB or #AARRGGBB).");
                continue;
            }
            if (!colors.ContainsKey(key)) _warnings.Add($"theme.overrides.{key}: unknown colour key.");
            colors[key] = value;
        }
        return theme with { Colors = colors };
    }

    /// <summary>Parses theme JSON and completes it with the built-in theme of its base.</summary>
    public static ThemeDefinition ParseThemeFile(string json, string fallbackName, List<string>? warnings = null)
    {
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                   ?? throw new JsonException("Theme root must be an object.");
        var name = root["name"]?.GetValue<string>() is { Length: > 0 } n ? n : fallbackName;
        var baseText = root["base"]?.GetValue<string>() ?? "light";
        var @base = baseText.Equals("dark", StringComparison.OrdinalIgnoreCase) ? ThemeBase.Dark : ThemeBase.Light;

        var colors = new Dictionary<string, string>(@base == ThemeBase.Dark ? BuiltInDark.Colors : BuiltInLight.Colors, StringComparer.Ordinal);
        if (root["colors"] is JsonObject colorsObj)
        {
            var flat = new Dictionary<string, string>(StringComparer.Ordinal);
            Flatten(colorsObj, "", flat);
            foreach (var (k, v) in flat)
            {
                if (!ThemeColor.TryParse(v, out _))
                {
                    warnings?.Add($"Theme '{name}': {k} = '{v}' is not a colour.");
                    continue;
                }
                colors[k] = v;
            }
        }
        return new ThemeDefinition(name, @base, colors);
    }

    private static void Flatten(JsonObject obj, string prefix, Dictionary<string, string> into)
    {
        foreach (var (k, v) in obj)
        {
            var key = prefix.Length == 0 ? k : prefix + "." + k;
            switch (v)
            {
                case JsonObject child:
                    Flatten(child, key, into);
                    break;
                case JsonValue value when value.TryGetValue<string>(out var s):
                    into[key] = s;
                    break;
            }
        }
    }

    private static ThemeDefinition LoadBuiltIn(string resource, string name, ThemeBase @base)
    {
        using var stream = typeof(ThemeService).Assembly.GetManifestResourceStream(resource)
                           ?? throw new InvalidOperationException($"Missing embedded theme {resource}.");
        using var reader = new StreamReader(stream);
        var root = (JsonObject)JsonNode.Parse(reader.ReadToEnd(), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })!;
        var flat = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root["colors"] is JsonObject colors) Flatten(colors, "", flat);
        return new ThemeDefinition(name, @base, flat);
    }
}
