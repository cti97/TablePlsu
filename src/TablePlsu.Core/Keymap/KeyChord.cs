using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace TablePlsu.Core.Keymap;

[Flags]
public enum KeyModifiers
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
}

/// <summary>
/// A key plus modifiers, independent of WPF. <see cref="Key"/> is a canonical name: "A"–"Z", "0"–"9", "F1"–"F12",
/// "Enter", "Tab", "Escape", "Space", "Backspace", "Delete", "Insert", "Home", "End", "PageUp", "PageDown",
/// "Up", "Down", "Left", "Right", or one of the punctuation keys <c>. , / \ ` = - [ ] ; '</c>.
/// Formatting is always "Ctrl+Alt+Shift+Key". WPF <c>Key</c> enum names (OemPeriod, D1, NumPad1, Return…) parse too,
/// so the App can map with <c>KeyChord.TryParse(modifiers + "+" + key.ToString())</c>.
/// </summary>
public readonly record struct KeyChord(KeyModifiers Modifiers, string Key)
{
    private static readonly FrozenDictionary<string, string> Aliases = BuildAliases();

    private static readonly FrozenSet<string> SingleKeysAllowed =
        new[] { "Space", "Delete", "Enter", "Escape" }.ToFrozenSet(StringComparer.Ordinal);

    public bool IsFunctionKey => Key.Length >= 2 && Key[0] == 'F' && char.IsDigit(Key[1]);

    /// <summary>The spec rule: a binding needs Ctrl or Alt unless it is an F-key or one of the single grid/sidebar keys.</summary>
    public bool IsAllowedBinding =>
        (Modifiers & (KeyModifiers.Ctrl | KeyModifiers.Alt)) != 0
        || IsFunctionKey
        || (Modifiers == KeyModifiers.None && SingleKeysAllowed.Contains(Key));

    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(KeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        parts.Add(Key);
        return string.Join("+", parts);
    }

    public static KeyChord Parse(string text) =>
        TryParse(text, out var chord) ? chord : throw new FormatException($"'{text}' is not a valid key binding.");

    public static bool TryParse([NotNullWhen(true)] string? text, out KeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();

        // A trailing "++" means the '+' key itself (same physical key as '=').
        string? plusKey = null;
        if (s.EndsWith("++", StringComparison.Ordinal))
        {
            plusKey = "=";
            s = s[..^2];
        }
        else if (s == "+")
        {
            chord = new KeyChord(KeyModifiers.None, "=");
            return true;
        }

        var parts = s.Split('+', StringSplitOptions.TrimEntries);
        var mods = KeyModifiers.None;
        string? key = plusKey;
        foreach (var p in parts)
        {
            if (p.Length == 0) return false;
            var mod = ParseModifier(p);
            if (mod != KeyModifiers.None)
            {
                mods |= mod;
                continue;
            }
            if (key is not null) return false; // two keys
            if (!TryNormalizeKey(p, out var k)) return false;
            key = k;
        }
        if (key is null) return false;
        chord = new KeyChord(mods, key);
        return true;
    }

    private static KeyModifiers ParseModifier(string p) => p.ToLowerInvariant() switch
    {
        "ctrl" or "control" or "ctl" => KeyModifiers.Ctrl,
        "alt" or "option" or "opt" => KeyModifiers.Alt,
        "shift" => KeyModifiers.Shift,
        _ => KeyModifiers.None,
    };

    public static bool TryNormalizeKey(string name, [NotNullWhen(true)] out string? key)
    {
        key = null;
        if (name.Length == 1)
        {
            var c = name[0];
            if (char.IsAsciiLetter(c)) { key = char.ToUpperInvariant(c).ToString(); return true; }
            if (char.IsAsciiDigit(c)) { key = name; return true; }
            if (".,/\\`=-[];'".Contains(c)) { key = name; return true; }
            return false;
        }
        if (Aliases.TryGetValue(name, out var alias)) { key = alias; return true; }
        // F1–F12
        if ((name[0] is 'F' or 'f') && int.TryParse(name.AsSpan(1), out var f) && f is >= 1 and <= 12 && name.Length <= 3)
        {
            key = "F" + f;
            return true;
        }
        return false;
    }

    private static FrozenDictionary<string, string> BuildAliases()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Enter"] = "Enter", ["Return"] = "Enter",
            ["Tab"] = "Tab",
            ["Escape"] = "Escape", ["Esc"] = "Escape",
            ["Space"] = "Space", ["Spacebar"] = "Space",
            ["Backspace"] = "Backspace", ["Back"] = "Backspace",
            ["Delete"] = "Delete", ["Del"] = "Delete",
            ["Insert"] = "Insert", ["Ins"] = "Insert",
            ["Home"] = "Home", ["End"] = "End",
            ["PageUp"] = "PageUp", ["PgUp"] = "PageUp", ["Prior"] = "PageUp",
            ["PageDown"] = "PageDown", ["PgDn"] = "PageDown", ["Next"] = "PageDown",
            ["Up"] = "Up", ["Down"] = "Down", ["Left"] = "Left", ["Right"] = "Right",
            ["OemPeriod"] = ".", ["Period"] = ".", ["Decimal"] = ".",
            ["OemComma"] = ",", ["Comma"] = ",",
            ["OemQuestion"] = "/", ["Oem2"] = "/", ["Slash"] = "/", ["Divide"] = "/",
            ["OemPipe"] = "\\", ["Oem5"] = "\\", ["OemBackslash"] = "\\", ["Backslash"] = "\\",
            ["OemTilde"] = "`", ["Oem3"] = "`", ["Backtick"] = "`", ["Grave"] = "`",
            ["OemPlus"] = "=", ["Plus"] = "=", ["Equals"] = "=", ["Equal"] = "=", ["Add"] = "=",
            ["OemMinus"] = "-", ["Minus"] = "-", ["Subtract"] = "-",
            ["OemOpenBrackets"] = "[", ["Oem4"] = "[", ["OpenBracket"] = "[",
            ["OemCloseBrackets"] = "]", ["Oem6"] = "]", ["CloseBracket"] = "]",
            ["OemSemicolon"] = ";", ["Oem1"] = ";", ["Semicolon"] = ";",
            ["OemQuotes"] = "'", ["Oem7"] = "'", ["Quote"] = "'",
        };
        for (var i = 0; i <= 9; i++)
        {
            d["D" + i] = i.ToString();
            d["NumPad" + i] = i.ToString();
        }
        return d.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}
