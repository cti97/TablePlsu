using TablePlsu.Core.Commands;
using TablePlsu.Core.Keymap;

namespace TablePlsu.Tests.Keymap;

public class KeymapServiceTests
{
    [Theory]
    [InlineData(CommandIds.QueryRunCurrent, "Ctrl+Enter", CommandScope.Editor)]
    [InlineData(CommandIds.QueryRunAll, "Ctrl+Shift+Enter", CommandScope.Editor)]
    [InlineData(CommandIds.QueryCancel, "Ctrl+Shift+.", CommandScope.Workspace)]
    [InlineData(CommandIds.TabNew, "Ctrl+T", CommandScope.Workspace)]
    [InlineData(CommandIds.TabClose, "Ctrl+W", CommandScope.Workspace)]
    [InlineData(CommandIds.TabReopenClosed, "Ctrl+Shift+T", CommandScope.Workspace)]
    [InlineData(CommandIds.TabNext, "Ctrl+Tab", CommandScope.Workspace)]
    [InlineData(CommandIds.TabPrev, "Ctrl+Shift+Tab", CommandScope.Workspace)]
    [InlineData("tab.goto1", "Ctrl+1", CommandScope.Workspace)]
    [InlineData("tab.goto9", "Ctrl+9", CommandScope.Workspace)]
    [InlineData(CommandIds.PaletteOpenAnything, "Ctrl+P", CommandScope.Workspace)]
    [InlineData(CommandIds.PaletteOpenDatabase, "Ctrl+K", CommandScope.Workspace)]
    [InlineData(CommandIds.PaletteOpenConnection, "Ctrl+Shift+K", CommandScope.Global)]
    [InlineData(CommandIds.ChangesCommit, "Ctrl+S", CommandScope.Workspace)]
    [InlineData(CommandIds.ChangesPreview, "Ctrl+Shift+P", CommandScope.Workspace)]
    [InlineData(CommandIds.ChangesDiscard, "Ctrl+Shift+Delete", CommandScope.Workspace)]
    [InlineData(CommandIds.EditUndo, "Ctrl+Z", CommandScope.Workspace)]
    [InlineData(CommandIds.EditRedo, "Ctrl+Y", CommandScope.Workspace)]
    [InlineData(CommandIds.SidebarToggle, "Ctrl+0", CommandScope.Workspace)]
    [InlineData(CommandIds.SidebarSearch, "Ctrl+Shift+F", CommandScope.Workspace)]
    [InlineData(CommandIds.TableFilterToggle, "Ctrl+F", CommandScope.Grid)]
    [InlineData(CommandIds.TableShowData, "Ctrl+Alt+1", CommandScope.Workspace)]
    [InlineData(CommandIds.TableShowStructure, "Ctrl+Alt+2", CommandScope.Workspace)]
    [InlineData(CommandIds.TableShowIndexes, "Ctrl+Alt+3", CommandScope.Workspace)]
    [InlineData(CommandIds.GridInsertRow, "Ctrl+I", CommandScope.Grid)]
    [InlineData(CommandIds.GridDuplicateRow, "Ctrl+D", CommandScope.Grid)]
    [InlineData(CommandIds.GridDeleteRow, "Delete", CommandScope.Grid)]
    [InlineData(CommandIds.GridSetValueMenu, "Alt+Enter", CommandScope.Grid)]
    [InlineData(CommandIds.EditorToggleLineComment, "Ctrl+/", CommandScope.Editor)]
    [InlineData(CommandIds.EditorToggleBlockComment, "Ctrl+Shift+/", CommandScope.Editor)]
    [InlineData(CommandIds.EditorFontBigger, "Ctrl+=", CommandScope.Editor)]
    [InlineData(CommandIds.EditorFontSmaller, "Ctrl+-", CommandScope.Editor)]
    [InlineData(CommandIds.EditorToggleResults, "Ctrl+`", CommandScope.Editor)]
    [InlineData(CommandIds.ConsoleToggle, "Ctrl+Alt+C", CommandScope.Workspace)]
    [InlineData(CommandIds.SettingsOpen, "Ctrl+,", CommandScope.Global)]
    [InlineData(CommandIds.WorkspaceReload, "Ctrl+R", CommandScope.Workspace)]
    [InlineData(CommandIds.WorkspaceReconnect, "Ctrl+Shift+R", CommandScope.Workspace)]
    [InlineData(CommandIds.FileOpen, "Ctrl+O", CommandScope.Workspace)]
    [InlineData(CommandIds.FileSaveAs, "Ctrl+Shift+S", CommandScope.Editor)]
    [InlineData(CommandIds.PageNext, "Ctrl+Right", CommandScope.Grid)]
    [InlineData(CommandIds.PagePrev, "Ctrl+Left", CommandScope.Grid)]
    [InlineData(CommandIds.WindowNew, "Ctrl+N", CommandScope.Global)]
    public void Defaults_cover_spec_shortcuts(string id, string binding, CommandScope scope)
    {
        var def = CommandIds.Get(id);
        Assert.NotNull(def);
        Assert.Equal(scope, def!.Scope);
        Assert.Equal(binding, def.DefaultBindings[0]);

        var km = new KeymapService();
        Assert.Equal(binding, km.GetBindingText(id));
        Assert.Equal(id, km.Resolve(KeyChord.Parse(binding), scope));
    }

    [Fact]
    public void Tab_next_and_prev_have_bracket_alternates()
    {
        var km = new KeymapService();
        Assert.Equal(CommandIds.TabNext, km.Resolve(KeyChord.Parse("Ctrl+]"), CommandScope.Workspace));
        Assert.Equal(CommandIds.TabPrev, km.Resolve(KeyChord.Parse("Ctrl+["), CommandScope.Workspace));
        Assert.Equal(CommandIds.EditRedo, km.Resolve(KeyChord.Parse("Ctrl+Shift+Z"), CommandScope.Workspace));
    }

    [Fact]
    public void All_defaults_parse_are_allowed_and_conflict_free()
    {
        foreach (var def in CommandIds.All)
            foreach (var b in def.DefaultBindings)
            {
                Assert.True(KeyChord.TryParse(b, out var c), $"{def.Id}: {b}");
                Assert.True(c.IsAllowedBinding, $"{def.Id}: {b}");
            }

        var km = new KeymapService();
        Assert.Empty(km.Conflicts);
        Assert.Empty(km.Warnings);
        Assert.Equal(CommandIds.All.Count, CommandIds.All.Select(d => d.Id).Distinct().Count());
    }

    [Fact]
    public void Every_command_has_a_title() =>
        Assert.All(CommandIds.All, d => Assert.False(string.IsNullOrWhiteSpace(d.Title)));

    [Fact]
    public void User_override_replaces_default()
    {
        var km = new KeymapService(new Dictionary<string, string[]?> { [CommandIds.QueryRunCurrent] = ["F5"] });
        Assert.Equal("F5", km.GetBindingText(CommandIds.QueryRunCurrent));
        Assert.Equal(CommandIds.QueryRunCurrent, km.Resolve(KeyChord.Parse("F5"), CommandScope.Editor));
        Assert.Null(km.Resolve(KeyChord.Parse("Ctrl+Enter"), CommandScope.Editor));
    }

    [Fact]
    public void Null_unbinds()
    {
        var km = new KeymapService(new Dictionary<string, string[]?> { [CommandIds.TabClose] = null });
        Assert.Empty(km.GetBindings(CommandIds.TabClose));
        Assert.Null(km.GetBindingText(CommandIds.TabClose));
        Assert.Null(km.Resolve(KeyChord.Parse("Ctrl+W"), CommandScope.Workspace));
    }

    [Fact]
    public void Conflicts_are_reported_and_first_binding_wins()
    {
        // tab.new comes before tab.close in CommandIds.All.
        var km = new KeymapService(new Dictionary<string, string[]?> { [CommandIds.TabClose] = ["Ctrl+T"] });
        var conflict = Assert.Single(km.Conflicts);
        Assert.Equal(KeyChord.Parse("Ctrl+T"), conflict.Chord);
        Assert.Equal(CommandScope.Workspace, conflict.Scope);
        Assert.Contains(CommandIds.TabNew, conflict.CommandIds);
        Assert.Contains(CommandIds.TabClose, conflict.CommandIds);
        Assert.Equal(CommandIds.TabNew, km.Resolve(KeyChord.Parse("Ctrl+T"), CommandScope.Workspace));
        Assert.Contains(km.Warnings, w => w.Contains("Ctrl+T"));
    }

    [Fact]
    public void Same_chord_in_different_scopes_is_not_a_conflict()
    {
        var km = new KeymapService();
        Assert.Equal(CommandIds.GridInsertRow, km.Resolve(KeyChord.Parse("Ctrl+I"), CommandScope.Grid));
        Assert.Equal(CommandIds.FilterAddRow, km.Resolve(KeyChord.Parse("Ctrl+I"), CommandScope.FilterBar));
    }

    [Fact]
    public void Resolve_with_scope_chain_picks_first_scope_with_binding()
    {
        var km = new KeymapService();
        var chord = KeyChord.Parse("Ctrl+N");
        Assert.Equal(CommandIds.WindowNew, km.Resolve(chord, [CommandScope.Editor, CommandScope.Workspace, CommandScope.Global]));
        Assert.Equal(CommandIds.QueryRunCurrent,
            km.Resolve(KeyChord.Parse("Ctrl+Enter"), [CommandScope.Editor, CommandScope.Workspace, CommandScope.Global]));
    }

    [Fact]
    public void Invalid_and_unknown_overrides_produce_warnings_and_are_ignored()
    {
        var km = new KeymapService(new Dictionary<string, string[]?>
        {
            ["no.such.command"] = ["Ctrl+Q"],
            [CommandIds.TabNew] = ["Ctrl+Nope"],
            [CommandIds.TabClose] = ["Q"],
        });
        Assert.Equal(3, km.Warnings.Count);
        Assert.Equal("Ctrl+T", km.GetBindingText(CommandIds.TabNew)); // invalid override keeps default
        Assert.Equal("Ctrl+W", km.GetBindingText(CommandIds.TabClose)); // disallowed binding keeps default
    }

    [Fact]
    public void Multiple_bindings_in_override()
    {
        var km = new KeymapService(new Dictionary<string, string[]?> { [CommandIds.TabNew] = ["Ctrl+T", "Ctrl+Alt+T"] });
        Assert.Equal(2, km.GetBindings(CommandIds.TabNew).Count);
        Assert.Equal(CommandIds.TabNew, km.Resolve(KeyChord.Parse("Ctrl+Alt+T"), CommandScope.Workspace));
    }
}
