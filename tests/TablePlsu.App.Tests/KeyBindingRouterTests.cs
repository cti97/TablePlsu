using System.Windows;
using System.Windows.Input;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Commands;
using TablePlsu.Core.Keymap;
using static TablePlsu.App.Infrastructure.KeyBindingRouter;

namespace TablePlsu.App.Tests;

public class KeyBindingRouterTests
{
    private readonly KeymapService _keymap = new();
    private readonly CommandRegistry _registry = new();
    private readonly List<string> _ran = [];

    private void Handle(params string[] ids)
    {
        foreach (var id in ids) _registry.Register(id, c => _ran.Add($"{c.CommandId}@{c.Scope}"));
    }

    private bool Route(string chord, bool text, params CommandScope[] scopes) =>
        Route(chord, text, [.. scopes.Select(s => new ScopeEntry(s, null, null))]);

    private bool Route(string chord, bool text, List<ScopeEntry> chain)
    {
        chain.Add(new ScopeEntry(CommandScope.Global, null, null));
        return Dispatch(KeyChord.Parse(chord), chain, text, _keymap, _registry,
            (id, e) => new CommandContext { CommandId = id, Scope = e.Scope, Target = e.Target });
    }

    [Theory]
    [InlineData(Key.OemQuestion, ModifierKeys.Control, "Ctrl+/")]
    [InlineData(Key.D1, ModifierKeys.Control, "Ctrl+1")]
    [InlineData(Key.Return, ModifierKeys.Control | ModifierKeys.Shift, "Ctrl+Shift+Enter")]
    [InlineData(Key.OemPeriod, ModifierKeys.Control | ModifierKeys.Alt, "Ctrl+Alt+.")]
    [InlineData(Key.Delete, ModifierKeys.Alt, "Alt+Delete")]
    [InlineData(Key.OemTilde, ModifierKeys.Control, "Ctrl+`")]
    [InlineData(Key.OemPlus, ModifierKeys.Control, "Ctrl+=")]
    [InlineData(Key.Oem6, ModifierKeys.Control, "Ctrl+]")]
    [InlineData(Key.F3, ModifierKeys.Shift, "Shift+F3")]
    [InlineData(Key.Tab, ModifierKeys.Control, "Ctrl+Tab")]
    public void Wpf_keys_map_to_keymap_chords(Key key, ModifierKeys mods, string expected)
    {
        Assert.True(TryGetChord(key, mods, out var chord));
        Assert.Equal(expected, chord.ToString());
    }

    [Theory]
    [InlineData(Key.LeftCtrl)]
    [InlineData(Key.RightShift)]
    [InlineData(Key.LWin)]
    public void Bare_modifiers_are_not_chords(Key key) => Assert.False(TryGetChord(key, ModifierKeys.Control, out _));

    [Fact]
    public void Focused_scope_wins_over_outer_scopes()
    {
        Handle(CommandIds.EditorFind, CommandIds.TableFilterToggle);
        Assert.True(Route("Ctrl+F", false, CommandScope.Editor, CommandScope.Workspace));
        Assert.Equal(["editor.find@Editor"], _ran);

        _ran.Clear();
        Assert.True(Route("Ctrl+F", false, CommandScope.Grid, CommandScope.Workspace));
        Assert.Equal(["table.filterToggle@Grid"], _ran);
    }

    [Fact]
    public void Unbound_in_focused_scope_falls_through_to_workspace()
    {
        Handle(CommandIds.TabClose);
        Assert.True(Route("Ctrl+W", false, CommandScope.Editor, CommandScope.Workspace));
        Assert.Equal(["tab.close@Workspace"], _ran);
    }

    [Fact]
    public void Global_commands_run_last()
    {
        Handle(CommandIds.WindowNew);
        Assert.True(Route("Ctrl+N", false, CommandScope.Workspace));
        Assert.Equal(["window.new@Global"], _ran);
    }

    [Fact]
    public void Unhandled_command_is_not_marked_handled()
    {
        Assert.False(Route("Ctrl+W", false, CommandScope.Workspace));
    }

    [Fact]
    public void Plain_keys_in_text_inputs_are_not_routed()
    {
        Handle(CommandIds.GridDeleteRow, CommandIds.SidebarOpenItem);
        Assert.False(Route("Delete", true, CommandScope.Grid));
        Assert.False(Route("Space", true, CommandScope.Sidebar));
        Assert.Empty(_ran);
        Assert.True(Route("Delete", false, CommandScope.Grid));
    }

    [Fact]
    public void Text_editing_chords_stay_with_text_inputs()
    {
        Handle(CommandIds.EditUndo, CommandIds.GridCopy);
        Assert.False(Route("Ctrl+Z", true, CommandScope.Workspace));
        Assert.False(Route("Ctrl+C", true, CommandScope.Grid));
        Assert.True(Route("Ctrl+Z", false, CommandScope.Workspace));
    }

    [Fact]
    public void Ctrl_and_function_keys_route_from_text_inputs()
    {
        Handle(CommandIds.QueryRunCurrent, CommandIds.EditorFindNext);
        Assert.True(Route("Ctrl+Enter", true, CommandScope.Editor, CommandScope.Workspace));
        Assert.True(Route("F3", true, CommandScope.Editor, CommandScope.Workspace));
        Assert.Equal(["query.runCurrent@Editor", "editor.findNext@Editor"], _ran);
    }

    [Fact]
    public void Ctrl_enter_outside_editor_or_grid_opens_new_editor()
    {
        Handle(CommandIds.EditorNew, CommandIds.QueryRunCurrent);
        Assert.True(Route("Ctrl+Enter", false, CommandScope.Sidebar, CommandScope.Workspace));
        Assert.Equal(["editor.new@Workspace"], _ran);
    }

    [Fact]
    public void Ctrl_enter_in_grid_does_not_open_editor()
    {
        Handle(CommandIds.EditorNew);
        Assert.False(Route("Ctrl+Enter", false, CommandScope.Grid, CommandScope.Workspace));
        Assert.Empty(_ran);
    }

    [Fact]
    public void Command_target_gets_first_chance()
    {
        Handle(CommandIds.TabClose);
        var target = new Target(handles: true);
        Assert.True(Route("Ctrl+W", false, [new ScopeEntry(CommandScope.Workspace, null, target)]));
        Assert.Equal(["tab.close"], target.Seen);
        Assert.Empty(_ran);

        var passive = new Target(handles: false);
        Assert.True(Route("Ctrl+W", false, [new ScopeEntry(CommandScope.Workspace, null, passive)]));
        Assert.Equal(["tab.close@Workspace"], _ran);
    }

    [Fact]
    public void User_keymap_override_is_respected()
    {
        var keymap = new KeymapService(new Dictionary<string, string[]?> { [CommandIds.TabClose] = ["Ctrl+Q"] });
        Handle(CommandIds.TabClose);
        var chain = new List<ScopeEntry> { new(CommandScope.Workspace, null, null) };
        Assert.False(Dispatch(KeyChord.Parse("Ctrl+W"), chain, false, keymap, _registry, (id, e) => new CommandContext { CommandId = id }));
        Assert.True(Dispatch(KeyChord.Parse("Ctrl+Q"), chain, false, keymap, _registry, (id, e) => new CommandContext { CommandId = id }));
    }

    private sealed class Target(bool handles) : ICommandTarget
    {
        public List<string> Seen { get; } = [];

        public bool TryExecuteCommand(string commandId, CommandContext context)
        {
            Seen.Add(commandId);
            return handles;
        }
    }
}
