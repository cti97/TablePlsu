using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TablePlsu.App.Features.Settings;
using TablePlsu.App.Features.Workspace;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Commands;
using TablePlsu.Core.Connections;
using TablePlsu.Core.Keymap;
using TablePlsu.Core.Settings;
using TablePlsu.Core.Themes;

namespace TablePlsu.App.Tests;

[Collection(AppServicesCollection.Name)]
public class SettingsFeatureTests
{
    private static WorkspaceViewModel NewWorkspace()
    {
        TestApp.Init(new WorkspaceFeature(), new SettingsFeature());
        UiDispatch.Initialize(Dispatcher.CurrentDispatcher);
        return new WorkspaceViewModel(TestApp.Profile(), ConnectionSecrets.None, null, shell: null);
    }

    private static bool Run(string id, IWorkspace? ws) =>
        AppServices.Commands.TryExecute(id, new CommandContext { CommandId = id, Workspace = ws, Scope = CommandScope.Global });

    private static SettingsEditorViewModel OpenSettings(WorkspaceViewModel ws)
    {
        Assert.True(Run(CommandIds.SettingsOpen, ws));
        return Assert.IsType<SettingsEditorViewModel>(ws.ActiveTab);
    }

    private static void SetText(SettingsEditorViewModel vm, string text) => vm.Document.Text = text;

    [Fact]
    public void Ctrl_comma_opens_settings_tab_and_creates_starter_file()
    {
        var ws = NewWorkspace();
        Assert.False(File.Exists(AppServices.Paths.SettingsFile));
        var vm = OpenSettings(ws);
        Assert.Equal(TabKinds.Settings, vm.Kind);
        Assert.True(File.Exists(AppServices.Paths.SettingsFile));
        Assert.Equal(SettingsJsonEdits.StarterText, File.ReadAllText(AppServices.Paths.SettingsFile));
        Assert.Equal(SettingsJsonEdits.StarterText, vm.Document.Text);
        Assert.False(vm.IsDirty);
        Assert.Empty(vm.Diagnostics);

        // A second Ctrl+, activates the same tab.
        Assert.True(Run(CommandIds.SettingsOpen, ws));
        Assert.Single(ws.Tabs, t => t.Kind == TabKinds.Settings);
        Assert.Equal("Ctrl+,", AppServices.Keymap.GetBindingText(CommandIds.SettingsOpen));
    }

    [Fact]
    public void Save_writes_file_and_applies_settings_live()
    {
        var ws = NewWorkspace();
        var vm = OpenSettings(ws);
        SettingsChangedEventArgs? changed = null;
        AppServices.SettingsChanged += Handler;
        try
        {
            SetText(vm, "{\n  \"editor\": { \"fontSize\": 17, \"fontFamily\": \"Consolas\" },\n  \"grid\": { \"fontSize\": 15 },\n  \"sidebar\": { \"fontSize\": 14, \"rowPadding\": 5 },\n  \"tags\": { \"local\": \"#123456\" }\n}\n");
            Assert.True(vm.IsDirty);
            Assert.EndsWith("•", vm.Title);
            vm.Save();
        }
        finally
        {
            AppServices.SettingsChanged -= Handler;
        }

        Assert.False(vm.IsDirty);
        Assert.Equal("settings.json", vm.Title);
        Assert.NotNull(changed);
        Assert.Null(changed!.Error);
        Assert.Contains("17", File.ReadAllText(AppServices.Paths.SettingsFile));
        Assert.Equal(17, AppServices.Current.Editor.FontSize);
        Assert.Equal(15, AppServices.Current.Grid.FontSize);
        Assert.Equal("#123456", AppServices.Current.Tags["local"]);
        Assert.StartsWith("Saved and applied", vm.StatusText);
        Assert.Null(vm.Banner);

        var res = new ResourceDictionary();
        SettingsResources.ApplyTo(res, AppServices.Current);
        Assert.Equal(17.0, res[SettingsResources.EditorFontSizeKey]);
        Assert.StartsWith("Consolas", ((FontFamily)res[SettingsResources.EditorFontFamilyKey]).Source);
        Assert.Equal(14.0, res[SettingsResources.SidebarFontSizeKey]);
        Assert.Equal(new Thickness(0, 5, 0, 5), res[SettingsResources.SidebarRowPaddingKey]);
        Assert.True((double)res[SettingsResources.SidebarRowHeightKey] > 14);

        void Handler(object? s, SettingsChangedEventArgs e) => changed = e;
    }

    [Fact]
    public void Bad_json_shows_banner_and_keeps_last_good_settings()
    {
        var ws = NewWorkspace();
        var vm = OpenSettings(ws);
        SetText(vm, "{ \"editor\": { \"fontSize\": 20 } }");
        vm.Save();
        Assert.Equal(20, AppServices.Current.Editor.FontSize);

        SetText(vm, "{ \"editor\": { \"fontSize\": 30, } ,, }");
        vm.Save();
        Assert.True(vm.HasBanner);
        Assert.Contains("last good settings", vm.Banner);
        Assert.Equal(20, AppServices.Current.Editor.FontSize);
        var d = Assert.Single(vm.Diagnostics);
        Assert.True(d.IsError);
        Assert.Equal(1, d.Line);
        Assert.Contains("not applied", vm.StatusText);
        // The broken text is still saved: it is the user's file.
        Assert.Contains(",,", File.ReadAllText(AppServices.Paths.SettingsFile));

        SetText(vm, "{ \"editor\": { \"fontSize\": 21 } }");
        vm.Save();
        Assert.False(vm.HasBanner);
        Assert.Equal(21, AppServices.Current.Editor.FontSize);
    }

    [Fact]
    public void Validation_lists_schema_problems_with_lines()
    {
        var ws = NewWorkspace();
        var vm = OpenSettings(ws);
        SetText(vm, "{\n  \"editor\": {\n    \"fontSzie\": 14,\n    \"tabSize\": \"four\"\n  },\n  \"theme\": { \"mode\": \"night\" }\n}");
        var ds = vm.Diagnostics;
        Assert.Equal(3, ds.Count);
        Assert.Equal([3, 4, 6], ds.Select(d => d.Line));
        Assert.Equal(DiagnosticSeverity.Warning, ds[0].Severity);
        Assert.True(ds[1].IsError);
        Assert.Equal("2 errors, 1 warning", vm.DiagnosticsSummary);

        (int, int)? jumped = null;
        vm.JumpRequested += (_, at) => jumped = at;
        vm.JumpCommand.Execute(ds[2]);
        Assert.Equal((6, 22), jumped);
    }

    [Fact]
    public void Unknown_theme_name_is_reported_after_save()
    {
        var ws = NewWorkspace();
        var vm = OpenSettings(ws);
        SetText(vm, "{\n  \"theme\": {\n    \"mode\": \"dark\",\n    \"dark\": \"Nope\"\n  }\n}");
        vm.Save();
        Assert.Contains(vm.Diagnostics, d => d.Message.Contains("'Nope' not found") && d.Line == 4);
    }

    [Fact]
    public void Theme_settings_resolve_built_in_extras_and_overrides()
    {
        var ws = NewWorkspace();
        var vm = OpenSettings(ws);
        SetText(vm, "{ \"theme\": { \"mode\": \"dark\", \"dark\": \"Nord\", \"overrides\": { \"app.accent\": \"#FF0000\" } } }");
        vm.Save();
        var theme = AppServices.Themes.Resolve(AppServices.Current.Theme, systemIsDark: false);
        Assert.Equal("Nord", theme.Name);
        Assert.Equal("#FF0000", theme.Colors["app.accent"]);
        var res = new ResourceDictionary();
        ThemeApplier.ApplyTo(res, theme);
        Assert.Equal(Color.FromRgb(0x2E, 0x34, 0x40), ((SolidColorBrush)res["c.app.window"]).Color);
    }

    [Fact]
    public void Open_keymap_inserts_section_and_opens_reference()
    {
        var ws = NewWorkspace();
        Assert.True(Run(CommandIds.KeymapOpen, ws));
        var editor = Assert.Single(ws.Tabs.OfType<SettingsEditorViewModel>());
        var reference = Assert.Single(ws.Tabs.OfType<KeymapReferenceViewModel>());
        // RevealSection is posted to the dispatcher; run it.
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.Contains("\"keymap\": {", editor.Document.Text);
        Assert.True(editor.IsDirty);
        Assert.Empty(editor.Diagnostics);

        Assert.Equal(CommandIds.All.Count, reference.Rows.Count);
        var row = reference.Rows.Single(r => r.Id == CommandIds.QueryRunCurrent);
        Assert.Equal("Ctrl+Enter", row.Bindings);
        Assert.Equal("Editor", row.Scope);
        Assert.False(row.IsCustomized);

        // Rebind and save: the reference and the router's keymap follow live.
        var text = editor.Document.Text.Replace("\"keymap\": {\n    \n  }", "\"keymap\": {\n    \"query.runCurrent\": \"Ctrl+R\",\n    \"tab.next\": null\n  }");
        editor.Document.Text = text;
        Assert.Empty(editor.Diagnostics);
        editor.Save();
        Assert.Equal(KeyChord.Parse("Ctrl+R"), AppServices.Keymap.GetBindings(CommandIds.QueryRunCurrent).Single());
        row = reference.Rows.Single(r => r.Id == CommandIds.QueryRunCurrent);
        Assert.Equal("Ctrl+R", row.Bindings);
        Assert.True(row.IsCustomized);
        Assert.True(reference.Rows.Single(r => r.Id == CommandIds.TabNext).IsUnbound);
        Assert.Equal(CommandIds.QueryRunCurrent, AppServices.Keymap.Resolve(KeyChord.Parse("Ctrl+R"), CommandScope.Editor));

        reference.FilterText = "runcur";
        Assert.Single(reference.Rows);
    }

    [Fact]
    public void Open_keymap_selects_existing_section()
    {
        var ws = NewWorkspace();
        File.WriteAllText(AppServices.Paths.SettingsFile, "{\n  \"keymap\": { \"tab.next\": \"Ctrl+]\" }\n}");
        var vm = OpenSettings(ws);
        (int Offset, int Length)? selected = null;
        vm.SelectRequested += (_, r) => selected = r;
        Assert.True(vm.RevealSection("keymap"));
        Assert.False(vm.IsDirty);
        Assert.Equal("\"tab.next\": \"Ctrl+]\"", vm.Document.Text.Substring(selected!.Value.Offset, selected.Value.Length));
    }

    [Fact]
    public void Defaults_tab_is_read_only_and_matches_embedded_defaults()
    {
        var ws = NewWorkspace();
        Assert.True(Run(SettingsFeature.OpenDefaultsCommand, ws));
        var vm = Assert.IsType<SettingsEditorViewModel>(ws.ActiveTab);
        Assert.True(vm.IsReadOnly);
        Assert.Equal(SettingsService.DefaultsJson, vm.Document.Text);
        vm.Save(); // no-op
        Assert.False(File.Exists(AppServices.Paths.SettingsFile));
    }

    [Fact]
    public void External_edit_reloads_clean_editor_but_not_dirty_one()
    {
        var ws = NewWorkspace();
        var vm = OpenSettings(ws);
        File.WriteAllText(AppServices.Paths.SettingsFile, "{ \"grid\": { \"nullDisplay\": \"∅\" } }");
        AppServices.Settings.Reload();
        Assert.Contains("∅", vm.Document.Text);
        Assert.False(vm.IsDirty);

        vm.Document.Insert(0, "// mine\n");
        File.WriteAllText(AppServices.Paths.SettingsFile, "{}");
        AppServices.Settings.Reload();
        Assert.StartsWith("// mine", vm.Document.Text);
    }

    [Fact]
    public void Palette_offers_settings_extras()
    {
        var ws = NewWorkspace();
        var items = AppServices.Features.Palette.GetItems(new PaletteQuery(PaletteMode.Anything, ws, null)).ToList();
        Assert.Contains(items, i => i.Title == "Open Themes Folder");
        var keymapRef = items.Single(i => i.Title == "Keymap Reference");
        keymapRef.Invoke(new PaletteInvocation(new PaletteQuery(PaletteMode.Anything, ws, null), false, false));
        Assert.IsType<KeymapReferenceViewModel>(ws.ActiveTab);
    }

    [Fact]
    public void Editor_view_renders_with_highlighting_and_jumps()
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                TestApp.Init(new WorkspaceFeature(), new SettingsFeature());
                UiDispatch.Initialize(Dispatcher.CurrentDispatcher);
                var vm = new SettingsEditorViewModel();
                vm.Document.Text = "{\n  \"editor\": {\n    \"fontSize\": \"big\"\n  }\n}";
                var view = (SettingsEditorView)vm.CreateView();
                var window = new Window { Content = view, Width = 800, Height = 600, Left = -20000, ShowActivated = false, ShowInTaskbar = false };
                ThemeApplier.ApplyTo(window.Resources, ThemeService.BuiltInDark);
                SettingsResources.ApplyTo(window.Resources, new AppSettings());
                window.Show();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                var editor = (ICSharpCode.AvalonEdit.TextEditor)view.FindName("Editor");
                Assert.Equal("SettingsJson", editor.SyntaxHighlighting.Name);
                Assert.Same(vm.Document, editor.Document);
                var problems = (ListBox)view.FindName("ProblemList");
                Assert.Single(problems.Items);
                vm.JumpCommand.Execute(vm.Diagnostics[0]);
                Assert.Equal(3, editor.TextArea.Caret.Line);
                Assert.Equal(17, editor.TextArea.Caret.Column);
                window.Close();
                vm.OnClosed(TabCloseReason.User); // unsubscribes from the static settings events
            }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException("STA body failed: " + error);
    }
}
