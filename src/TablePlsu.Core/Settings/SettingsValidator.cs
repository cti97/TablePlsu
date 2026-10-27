using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TablePlsu.Core.Keymap;

namespace TablePlsu.Core.Settings;

public enum DiagnosticSeverity { Error, Warning }

/// <summary>One validation finding for settings.json. <see cref="Line"/>/<see cref="Column"/> are 1-based.</summary>
public sealed record SettingsDiagnostic(DiagnosticSeverity Severity, string Path, string Message, int Line, int Column)
{
    public bool IsError => Severity == DiagnosticSeverity.Error;

    public override string ToString() => $"{Line}:{Column} {(IsError ? "error" : "warning")} {(Path.Length > 0 ? Path + ": " : "")}{Message}";
}

/// <summary>
/// Validates settings.json text against the embedded settings.schema.json, supporting the subset the schema uses:
/// <c>type</c> (incl. integer and type lists), <c>enum</c>, <c>minimum</c>/<c>maximum</c>, <c>pattern</c>,
/// <c>properties</c>, <c>additionalProperties</c> (false → unknown-key warning, or a schema), <c>propertyNames</c>
/// (enum → unknown-name warning), <c>items</c>, <c>oneOf</c> and local <c>$ref</c>. Syntax errors are reported with
/// their position. When the JSON parses, keymap problems (bad chords, disallowed chords, conflicts) are added as warnings.
/// </summary>
public sealed class SettingsValidator
{
    private static readonly Lazy<SettingsValidator> DefaultInstance = new(() => new SettingsValidator(SettingsService.SchemaJson));

    private readonly JsonObject _schema;
    private readonly Dictionary<string, Regex> _patterns = new(StringComparer.Ordinal);

    public SettingsValidator(string schemaJson)
    {
        _schema = JsonNode.Parse(schemaJson, documentOptions: JsonNodeMerge.DocumentOptions) as JsonObject
                  ?? throw new ArgumentException("Schema root must be an object.", nameof(schemaJson));
    }

    /// <summary>A validator for the embedded schema.</summary>
    public static SettingsValidator Default => DefaultInstance.Value;

    public JsonObject Schema => _schema;

    public static IReadOnlyList<SettingsDiagnostic> ValidateText(string text) => Default.Validate(text);

    public IReadOnlyList<SettingsDiagnostic> Validate(string text)
    {
        var result = new List<SettingsDiagnostic>();
        JsonTreeNode root;
        try
        {
            root = string.IsNullOrWhiteSpace(text) ? new JsonTreeNode { Kind = JsonValueKind.Object } : JsonTree.Parse(text);
        }
        catch (JsonTreeException ex)
        {
            result.Add(new SettingsDiagnostic(DiagnosticSeverity.Error, "", ex.Message, ex.Line, ex.Column));
            return result;
        }

        var ctx = new Ctx(text, result);
        Check(root, _schema, "", ctx);
        CheckDuplicates(root, "", ctx);

        if (!result.Any(d => d.IsError)) AddKeymapWarnings(text, root, ctx);
        result.Sort((a, b) => a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Column.CompareTo(b.Column));
        return result;
    }

    private sealed class Ctx(string text, List<SettingsDiagnostic> output)
    {
        public List<SettingsDiagnostic> Output { get; } = output;

        public void Add(DiagnosticSeverity severity, string path, string message, int offset)
        {
            var (line, col) = JsonTree.LineColumn(text, offset);
            Output.Add(new SettingsDiagnostic(severity, path, message, line, col));
        }
    }

    private static string Join(string path, string name) => path.Length == 0 ? name : path + "." + name;

    private JsonObject Resolve(JsonObject schema)
    {
        var guard = 0;
        while (schema["$ref"]?.GetValue<string>() is { } reference && guard++ < 16)
        {
            if (!reference.StartsWith("#/", StringComparison.Ordinal)) break;
            JsonNode? node = _schema;
            foreach (var part in reference[2..].Split('/')) node = node?[part];
            if (node is not JsonObject target) break;
            // Sibling keywords (description, default) are annotations only; the target carries the constraints.
            schema = target;
        }
        return schema;
    }

    /// <summary>Validates <paramref name="node"/>; returns false when an error (not a warning) was found.</summary>
    private bool Check(JsonTreeNode node, JsonObject schemaIn, string path, Ctx ctx)
    {
        var schema = Resolve(schemaIn);
        var ok = true;

        if (schema["oneOf"] is JsonArray oneOf)
        {
            var branches = oneOf.OfType<JsonObject>().Select(Resolve).ToList();
            foreach (var branch in branches)
            {
                var trial = new Ctx("", []);
                if (Check(node, branch, path, trial) && !trial.Output.Any(d => d.IsError))
                {
                    // Re-run on the real context to keep this branch's warnings.
                    Check(node, branch, path, ctx);
                    goto afterOneOf;
                }
            }
            var typed = branches.FirstOrDefault(b => TypeMatches(node, b));
            if (typed is not null) return Check(node, typed, path, ctx);
            ctx.Add(DiagnosticSeverity.Error, path, $"expected {DescribeTypes(branches)}, got {node.Describe()}.", node.Start);
            return false;
        }
        afterOneOf:

        if (schema["type"] is not null && !TypeMatches(node, schema))
        {
            ctx.Add(DiagnosticSeverity.Error, path, $"expected {DescribeTypes([schema])}, got {node.Describe()}.", node.Start);
            return false;
        }

        if (schema["enum"] is JsonArray values && !values.Any(v => ValueEquals(node, v)))
        {
            var allowed = string.Join(", ", values.Select(v => v?.ToJsonString() ?? "null"));
            ctx.Add(DiagnosticSeverity.Error, path, $"must be one of {allowed}.", node.Start);
            ok = false;
        }

        if (node.Kind == JsonValueKind.Number)
        {
            if (schema["minimum"] is JsonValue min && node.Number < min.GetValue<double>())
            {
                ctx.Add(DiagnosticSeverity.Error, path, $"must be ≥ {Num(min.GetValue<double>())} (got {node.NumberText}).", node.Start);
                ok = false;
            }
            if (schema["maximum"] is JsonValue max && node.Number > max.GetValue<double>())
            {
                ctx.Add(DiagnosticSeverity.Error, path, $"must be ≤ {Num(max.GetValue<double>())} (got {node.NumberText}).", node.Start);
                ok = false;
            }
        }

        if (node.Kind == JsonValueKind.String && schema["pattern"]?.GetValue<string>() is { } pattern && !Pattern(pattern).IsMatch(node.String ?? ""))
        {
            var hint = schema["description"]?.GetValue<string>();
            ctx.Add(DiagnosticSeverity.Error, path, $"'{node.String}' is not valid{(hint is null ? $" (pattern {pattern})" : ": " + hint)}.", node.Start);
            ok = false;
        }

        if (node.Kind == JsonValueKind.Object)
        {
            var props = schema["properties"] as JsonObject;
            var additional = schema["additionalProperties"];
            var names = schema["propertyNames"] is JsonObject pn ? Resolve(pn) : null;
            foreach (var prop in node.Properties)
            {
                var childPath = Join(path, prop.Name);
                if (names?["enum"] is JsonArray nameEnum && !nameEnum.Any(v => v?.GetValue<string>() == prop.Name))
                {
                    var what = names["description"]?.GetValue<string>() ?? "key";
                    var suggestion = Suggest(prop.Name, nameEnum.Select(v => v!.GetValue<string>()));
                    ctx.Add(DiagnosticSeverity.Warning, childPath, $"unknown {what} '{prop.Name}'{(suggestion is null ? "" : $" (did you mean '{suggestion}'?)")}; it is ignored.", prop.NameStart);
                    continue;
                }
                if (props?[prop.Name] is JsonObject childSchema)
                {
                    ok &= Check(prop.Value, childSchema, childPath, ctx);
                }
                else if (additional is JsonValue v && v.GetValueKind() == JsonValueKind.False)
                {
                    var suggestion = props is null ? null : Suggest(prop.Name, props.Select(p => p.Key));
                    ctx.Add(DiagnosticSeverity.Warning, childPath, $"unknown key '{prop.Name}'{(suggestion is null ? "" : $" (did you mean '{suggestion}'?)")}; it is ignored.", prop.NameStart);
                }
                else if (additional is JsonObject additionalSchema)
                {
                    ok &= Check(prop.Value, additionalSchema, childPath, ctx);
                }
            }
        }

        if (node.Kind == JsonValueKind.Array && schema["items"] is JsonObject itemSchema)
        {
            for (var i = 0; i < node.Items.Count; i++)
                ok &= Check(node.Items[i], itemSchema, $"{path}[{i}]", ctx);
        }

        return ok;
    }

    private void CheckDuplicates(JsonTreeNode node, string path, Ctx ctx)
    {
        if (node.Kind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in node.Properties)
            {
                if (!seen.Add(p.Name))
                    ctx.Add(DiagnosticSeverity.Warning, Join(path, p.Name), "duplicate key; the last one wins.", p.NameStart);
                CheckDuplicates(p.Value, Join(path, p.Name), ctx);
            }
        }
        else if (node.Kind == JsonValueKind.Array)
        {
            foreach (var item in node.Items) CheckDuplicates(item, path, ctx);
        }
    }

    private static void AddKeymapWarnings(string text, JsonTreeNode root, Ctx ctx)
    {
        if (root.Find("keymap")?.Value is not { Kind: JsonValueKind.Object } keymapNode) return;
        AppSettings settings;
        try
        {
            settings = SettingsService.Parse(text);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or NotSupportedException)
        {
            ctx.Add(DiagnosticSeverity.Error, "", ex.Message, root.Start);
            return;
        }

        var keymap = new KeymapService(settings.Keymap);
        foreach (var warning in keymap.Warnings)
        {
            if (warning.StartsWith("keymap: unknown command", StringComparison.Ordinal)) continue; // reported by the schema
            var id = settings.Keymap.Keys.Where(k => warning.Contains($"'{k}'", StringComparison.Ordinal)).OrderByDescending(k => k.Length).FirstOrDefault();
            var at = id is not null && keymapNode.Find(id) is { } p ? p.NameStart : keymapNode.Start;
            var message = warning.StartsWith("keymap: ", StringComparison.Ordinal) ? warning["keymap: ".Length..] : warning;
            ctx.Add(DiagnosticSeverity.Warning, id is null ? "keymap" : "keymap." + id, message, at);
        }
    }

    private static bool TypeMatches(JsonTreeNode node, JsonObject schema)
    {
        var type = schema["type"];
        if (type is null) return true;
        IEnumerable<string> types = type is JsonArray arr ? arr.Select(t => t!.GetValue<string>()) : [type.GetValue<string>()];
        return types.Any(t => t switch
        {
            "object" => node.Kind == JsonValueKind.Object,
            "array" => node.Kind == JsonValueKind.Array,
            "string" => node.Kind == JsonValueKind.String,
            "number" => node.Kind == JsonValueKind.Number,
            "integer" => node.Kind == JsonValueKind.Number && (node.IsInteger || node.Number == Math.Floor(node.Number)),
            "boolean" => node.Kind is JsonValueKind.True or JsonValueKind.False,
            "null" => node.Kind == JsonValueKind.Null,
            _ => true,
        });
    }

    private static string DescribeTypes(IEnumerable<JsonObject> schemas)
    {
        var names = new List<string>();
        foreach (var s in schemas)
        {
            if (s["type"] is JsonArray arr) names.AddRange(arr.Select(t => t!.GetValue<string>()));
            else if (s["type"] is JsonValue v) names.Add(v.GetValue<string>());
            else if (s["enum"] is not null) names.Add("one of the allowed values");
        }
        var distinct = names.Distinct().Select(n => n switch
        {
            "object" => "an object",
            "array" => "an array",
            "string" => "a string",
            "number" => "a number",
            "integer" => "an integer",
            "boolean" => "true or false",
            "null" => "null",
            _ => n,
        }).ToList();
        return distinct.Count switch
        {
            0 => "a different value",
            1 => distinct[0],
            _ => string.Join(", ", distinct[..^1]) + " or " + distinct[^1],
        };
    }

    private static bool ValueEquals(JsonTreeNode node, JsonNode? value)
    {
        if (value is null) return node.Kind == JsonValueKind.Null;
        return value.GetValueKind() switch
        {
            JsonValueKind.String => node.Kind == JsonValueKind.String && node.String == value.GetValue<string>(),
            JsonValueKind.Number => node.Kind == JsonValueKind.Number && node.Number == value.GetValue<double>(),
            JsonValueKind.True => node.Kind == JsonValueKind.True,
            JsonValueKind.False => node.Kind == JsonValueKind.False,
            JsonValueKind.Null => node.Kind == JsonValueKind.Null,
            _ => false,
        };
    }

    private Regex Pattern(string pattern)
    {
        if (!_patterns.TryGetValue(pattern, out var regex))
            _patterns[pattern] = regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        return regex;
    }

    private static string Num(double d) => d.ToString(CultureInfo.InvariantCulture);

    /// <summary>The closest candidate by edit distance (case-insensitive), when it is close enough to be a typo.</summary>
    internal static string? Suggest(string name, IEnumerable<string> candidates)
    {
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var c in candidates)
        {
            var d = Distance(name.ToLowerInvariant(), c.ToLowerInvariant());
            if (d < bestDistance)
            {
                bestDistance = d;
                best = c;
            }
        }
        return best is not null && bestDistance <= Math.Max(2, name.Length / 4) ? best : null;
    }

    private static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>Removes // and /* */ comments (outside strings); used to treat a comment-only file as empty.</summary>
    internal static string StripComments(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        var inString = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < text.Length) sb.Append(text[++i]);
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; sb.Append(c); continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                sb.Append('\n');
                continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/')) i++;
                i++;
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
