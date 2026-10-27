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
