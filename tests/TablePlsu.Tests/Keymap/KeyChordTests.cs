using TablePlsu.Core.Keymap;

namespace TablePlsu.Tests.Keymap;

public class KeyChordTests
{
    [Theory]
    [InlineData("Ctrl+Enter", "Ctrl+Enter")]
    [InlineData("ctrl+shift+enter", "Ctrl+Shift+Enter")]
    [InlineData("Shift+Ctrl+Enter", "Ctrl+Shift+Enter")]
    [InlineData("Control+Return", "Ctrl+Enter")]
    [InlineData("Ctrl+Alt+Shift+K", "Ctrl+Alt+Shift+K")]
    [InlineData("Ctrl+Shift+.", "Ctrl+Shift+.")]
    [InlineData("Ctrl+Shift+OemPeriod", "Ctrl+Shift+.")]
    [InlineData("Ctrl+OemComma", "Ctrl+,")]
    [InlineData("Ctrl+,", "Ctrl+,")]
    [InlineData("Ctrl+[", "Ctrl+[")]
    [InlineData("Ctrl+OemOpenBrackets", "Ctrl+[")]
    [InlineData("Ctrl+]", "Ctrl+]")]
    [InlineData("Ctrl+OemCloseBrackets", "Ctrl+]")]
    [InlineData("Ctrl+/", "Ctrl+/")]
    [InlineData("Ctrl+OemQuestion", "Ctrl+/")]
    [InlineData("Ctrl+Oem2", "Ctrl+/")]
    [InlineData("Ctrl+\\", "Ctrl+\\")]
    [InlineData("Ctrl+OemPipe", "Ctrl+\\")]
    [InlineData("Ctrl+`", "Ctrl+`")]
    [InlineData("Ctrl+OemTilde", "Ctrl+`")]
    [InlineData("Ctrl+=", "Ctrl+=")]
    [InlineData("Ctrl+OemPlus", "Ctrl+=")]
    [InlineData("Ctrl++", "Ctrl+=")]
    [InlineData("Ctrl+-", "Ctrl+-")]
    [InlineData("Ctrl+OemMinus", "Ctrl+-")]
    [InlineData("Ctrl+1", "Ctrl+1")]
    [InlineData("Ctrl+D1", "Ctrl+1")]
    [InlineData("Ctrl+Alt+NumPad3", "Ctrl+Alt+3")]
    [InlineData("F5", "F5")]
    [InlineData("shift+f3", "Shift+F3")]
    [InlineData("F12", "F12")]
    [InlineData("Delete", "Delete")]
    [InlineData("Del", "Delete")]
    [InlineData("Ctrl+Shift+Delete", "Ctrl+Shift+Delete")]
    [InlineData("Tab", "Tab")]
    [InlineData("Ctrl+Shift+Tab", "Ctrl+Shift+Tab")]
    [InlineData("Alt+Enter", "Alt+Enter")]
    [InlineData("Space", "Space")]
    [InlineData("Esc", "Escape")]
    [InlineData("Ctrl+Right", "Ctrl+Right")]
    [InlineData("Ctrl+t", "Ctrl+T")]
    [InlineData(" Ctrl + T ", "Ctrl+T")]
    public void Parse_and_format_round_trip(string input, string expected)
    {
        Assert.True(KeyChord.TryParse(input, out var chord), input);
        Assert.Equal(expected, chord.ToString());
        Assert.Equal(chord, KeyChord.Parse(chord.ToString()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+Nope")]
    [InlineData("Ctrl+A+B")]
    [InlineData("F13")]
    public void Invalid_input_fails(string input)
    {
        Assert.False(KeyChord.TryParse(input, out _));
        Assert.Throws<FormatException>(() => KeyChord.Parse(input));
    }

    [Fact]
    public void Equality_is_by_value()
    {
        Assert.Equal(KeyChord.Parse("Ctrl+Shift+T"), KeyChord.Parse("shift+ctrl+t"));
        Assert.NotEqual(KeyChord.Parse("Ctrl+T"), KeyChord.Parse("Ctrl+Shift+T"));
        Assert.Equal(KeyModifiers.Ctrl | KeyModifiers.Shift, KeyChord.Parse("Ctrl+Shift+T").Modifiers);
        Assert.Equal("T", KeyChord.Parse("Ctrl+Shift+T").Key);
    }

    [Theory]
    [InlineData("Ctrl+T", true)]
    [InlineData("Alt+Enter", true)]
    [InlineData("F5", true)]
    [InlineData("Shift+F3", true)]
    [InlineData("Delete", true)]
    [InlineData("Space", true)]
    [InlineData("Enter", true)]
    [InlineData("T", false)]
    [InlineData("Shift+T", false)]
    public void Binding_rule_requires_ctrl_or_alt_unless_function_or_single_key(string chord, bool ok) =>
        Assert.Equal(ok, KeyChord.Parse(chord).IsAllowedBinding);
}
