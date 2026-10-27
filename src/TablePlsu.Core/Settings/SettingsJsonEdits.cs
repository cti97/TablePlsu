using System.Text.Json;

namespace TablePlsu.Core.Settings;

/// <summary>A text edit plus the range to select afterwards (character offsets into the new text).</summary>
public sealed record SettingsTextEdit(string Text, int SelectionStart, int SelectionLength, bool Changed);

/// <summary>Text-level edits of settings.json that keep the user's formatting and comments.</summary>
public static class SettingsJsonEdits
{
    /// <summary>The starter content written when settings.json does not exist yet.</summary>
    public const string StarterText =
        "// TablePlsu settings. Keys here override the defaults key by key (\"Open defaults\" shows them all).\n" +
        "// Save (Ctrl+S) applies changes immediately. Example: \"editor\": { \"fontSize\": 14 }, \"theme\": { \"mode\": \"dark\" }\n" +
        "{\n}\n";

    /// <summary>
    /// Sets the dotted key (e.g. "editor.fontSize") to <paramref name="valueJson"/> (raw JSON text) by editing the text in
    /// place, so comments, formatting and every other key survive. Missing parent objects are created; a parent that is
    /// not an object is replaced. Returns null when <paramref name="text"/> is not valid JSON or its root is not an object.
    /// </summary>
    public static string? SetValue(string text, string dottedKey, string valueJson)
    {
        JsonTreeNode root;
        try
        {
            if (string.IsNullOrWhiteSpace(text)) text = "{}";
            root = JsonTree.Parse(text);
        }
        catch (JsonTreeException)
        {
            return null;
        }
        if (root.Kind != JsonValueKind.Object) return null;

        var parts = dottedKey.Split('.');
        var node = root;
        var indent = ChildIndent(text, root, "");
        for (var i = 0; i < parts.Length; i++)
        {
            var last = i == parts.Length - 1;
            var prop = node.Find(parts[i]);
            if (prop is null)
            {
                var value = last ? valueJson : Nested(parts.AsSpan(i + 1), valueJson, indent);
                return InsertProperty(text, node, indent, Quote(parts[i]) + ": " + value);
            }
            if (last) return Replace(text, prop.Value, valueJson);
            if (prop.Value.Kind != JsonValueKind.Object)
                return Replace(text, prop.Value, Nested(parts.AsSpan(i + 1), valueJson, indent));
            node = prop.Value;
            indent = ChildIndent(text, node, indent);
        }
        return text;
    }

    private static string Quote(string name) => JsonSerializer.Serialize(name);

    private static string Replace(string text, JsonTreeNode value, string replacement) =>
        text[..value.Start] + replacement + text[(value.End + 1)..];

    /// <summary>{ "a": { "b": value } } for the remaining key parts, indented under <paramref name="indent"/>.</summary>
    private static string Nested(ReadOnlySpan<string> parts, string valueJson, string indent)
    {
        var inner = indent + "  ";
        var value = parts.Length == 1 ? valueJson : Nested(parts[1..], valueJson, inner);
        return "{\n" + inner + Quote(parts[0]) + ": " + value + "\n" + indent + "}";
    }

    /// <summary>The indentation of <paramref name="obj"/>'s properties: taken from its first property when that one starts a line.</summary>
    private static string ChildIndent(string text, JsonTreeNode obj, string parentChildIndent)
    {
        if (obj.Properties.Count > 0)
        {
            var at = obj.Properties[0].NameStart;
            var lineStart = text.LastIndexOf('\n', Math.Max(0, at - 1)) + 1;
            var lead = text[lineStart..at];
            if (lead.Length > 0 && string.IsNullOrWhiteSpace(lead) || lineStart == at && lineStart > 0) return lead;
        }
        return parentChildIndent + "  ";
    }

    private static string InsertProperty(string text, JsonTreeNode obj, string indent, string property)
    {
        if (obj.Properties.Count == 0)
        {
            var closeIndent = indent.Length >= 2 ? indent[..^2] : "";
            return text[..(obj.Start + 1)] + "\n" + indent + property + "\n" + closeIndent + text[obj.End..];
        }
        var after = obj.Properties[^1].Value.End + 1;
        var i = after;
        while (i < obj.End && char.IsWhiteSpace(text[i])) i++;
        if (text[i] == ',') return text[..(i + 1)] + "\n" + indent + property + "," + text[(i + 1)..];
        return text[..after] + ",\n" + indent + property + text[after..];
    }

    /// <summary>
    /// Makes sure the root object has a top-level object property <paramref name="key"/> (for example "keymap"). Returns
    /// the (possibly) edited text with the inside of that object selected, or the caret placed on an empty line inside a
    /// newly inserted one. Returns null when <paramref name="text"/> is not valid JSON or its root is not an object.
    /// </summary>
    public static SettingsTextEdit? EnsureObjectSection(string text, string key, int indent = 2)
    {
        JsonTreeNode root;
        try
        {
            root = string.IsNullOrWhiteSpace(text) ? JsonTree.Parse("{}") : JsonTree.Parse(text);
            if (string.IsNullOrWhiteSpace(text)) text = "{}";
        }
        catch (JsonTreeException)
        {
            return null;
        }
        if (root.Kind != JsonValueKind.Object) return null;

        var pad = new string(' ', indent);
        if (root.Find(key) is { } existing)
        {
            var value = existing.Value;
            if (value.Kind != JsonValueKind.Object) return new SettingsTextEdit(text, value.Start, value.End - value.Start + 1, false);
            var innerStart = value.Start + 1;
            var innerEnd = value.End;
            if (value.Properties.Count == 0)
            {
                // Empty object: open it up and put the caret inside.
                var inner = "\n" + pad + pad + "\n" + pad;
                var replaced = text[..innerStart] + inner + text[innerEnd..];
                return new SettingsTextEdit(replaced, innerStart + 1 + 2 * indent, 0, true);
            }
            // Select from the first property to the end of the last value.
            var first = value.Properties[0].NameStart;
            var last = value.Properties[^1].Value.End + 1;
            return new SettingsTextEdit(text, first, last - first, false);
        }

        // Insert as the first property of the root, so no trailing-comma bookkeeping is needed at the end.
        var at = root.Start + 1;
        var hasOthers = root.Properties.Count > 0;
        var snippet = "\n" + pad + "\"" + key + "\": {\n" + pad + pad + "\n" + pad + "}" + (hasOthers ? "," : root.End == at ? "\n" : "");
        var newText = text[..at] + snippet + text[at..];
        var caret = at + ("\n" + pad + "\"" + key + "\": {\n" + pad + pad).Length;
        return new SettingsTextEdit(newText, caret, 0, true);
    }
}
