using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Search;
using TablePlsu.App.Features.Completion;
using TablePlsu.App.Features.Results;
using TablePlsu.App.Features.Settings;
using TablePlsu.App.Features.Sidebar;
using TablePlsu.App.Features.SqlEditor;
using TablePlsu.App.Features.TableData;
using TablePlsu.App.Features.Welcome;
using TablePlsu.App.Features.Workspace;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Commands;
using TablePlsu.Core.Connections;
using TablePlsu.Core.Db;
using TablePlsu.Core.Keymap;
using TablePlsu.Core.Metadata;
using TablePlsu.Core.Settings;
using TablePlsu.Core.Themes;

namespace TablePlsu.App.Tests;

/// <summary>
/// "Customisation actually works": every settings.json key group is changed through <see cref="SettingsService"/> (the same
/// path as saving the file) and the visible effect is asserted on live objects that already exist. Also checks that the
/// dark theme reaches the WPF parts whose default styles stay light.
/// </summary>
[Collection(AppServicesCollection.Name)]
public class CustomisationTests
{
    private static void RunSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ex; }
            finally
            {
                GridResources.TestTarget = null;
                SettingsResources.TestTarget = null;
                UiDispatch.Initialize(null!);
                try { Dispatcher.CurrentDispatcher.InvokeShutdown(); } catch (NullReferenceException) { }
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException("STA body failed: " + error);
    }

    private static void DoEvents() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    /// <summary>Saves settings.json like the settings tab does (reload + Changed + AppServices.ApplySettings on this thread).</summary>
    private static void Save(string json)
    {
        AppServices.Settings.SaveUserJson(json);
        Assert.Null(AppServices.Settings.LastError);
        DoEvents();
    }

    /// <summary>An off-screen window standing in for Application.Resources: shared styles + theme + settings resources, live.</summary>
    private static Window Host(FrameworkElement content, double width = 900, double height = 600) => Host(() => content, width, height);

    private static Window Host(Func<FrameworkElement> create, double width = 900, double height = 600)
    {
        var window = new Window
        {
            Width = width, Height = height, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        window.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/TablePlsu.App;component/Shell/Styles.xaml", UriKind.Relative)));
        AppServices.Theme.Attach(window.Resources);
        GridResources.TestTarget = window.Resources;
        SettingsResources.TestTarget = window.Resources;
        GridResources.Apply();
        SettingsResources.Apply();
        window.Content = new ContentControl();
        window.Show();
        // Views that use StaticResource need the window's dictionaries while they are parsed (in the app: Application.Resources).
        ((ContentControl)window.Content).Content = create();
        DoEvents();
        return window;
    }

    private static void InitSta(params IFeature[] features)
    {
        TestApp.Init(features);
        UiDispatch.Initialize(Dispatcher.CurrentDispatcher);
        // Earlier tests leave views subscribed to the static settings events on threads that are gone.
        AppServices.ClearSubscribersForTests();
        GridResources.ResetForTests();
        SettingsResources.ResetForTests();
        GridResources.EnsureInitialized();
        SettingsResources.EnsureInitialized();
    }

    private static T Find<T>(DependencyObject root, Func<T, bool>? predicate = null) where T : DependencyObject =>
        FindAll<T>(root).First(x => predicate?.Invoke(x) ?? true);

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in FindAll<T>(child)) yield return d;
        }
    }

    /// <summary>Relative luminance (0..1) of the rendered pixel at <paramref name="at"/> inside <paramref name="target"/>.</summary>
    private static double Luminance(Window? _, FrameworkElement target, Point at)
    {
        DependencyObject top = target;
        while (VisualTreeHelper.GetParent(top) is { } parent) top = parent;
        var root = top is Window w ? (FrameworkElement)VisualTreeHelper.GetChild(w, 0) : (FrameworkElement)top;
        var p = target.TranslatePoint(at, root);
        var rtb = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        var px = new byte[4];
        rtb.CopyPixels(new Int32Rect((int)p.X, (int)p.Y, 1, 1), px, 4, 0);
        return (0.2126 * px[2] + 0.7152 * px[1] + 0.0722 * px[0]) / 255.0;
    }

    private static WorkspaceViewModel NewWorkspace(ConnectionProfile? profile = null) =>
        new(profile ?? TestApp.Profile(), ConnectionSecrets.None, null, shell: null);

    // ---- editor.* ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Editor_view_settings_apply_live()
    {
        RunSta(() =>
        {
            InitSta(new WorkspaceFeature(), new SqlEditorFeature());
            var ws = NewWorkspace();
            var tab = (SqlEditorTabViewModel)ws.NewTab();
            tab.Document.Text = "select 1;\n\tselect 2;";
            var view = (SqlEditorView)tab.CreateView();
            var window = Host(view);
            var editor = view.TextEditor;
            var line1 = tab.Document.GetLineByNumber(1);
            double LineHeight() => editor.TextArea.TextView.GetOrConstructVisualLine(line1).Height;
            var normal = LineHeight();

            Save("""
                { "editor": { "fontFamily": "Consolas", "fontSize": 20, "lineHeight": 2.0, "tabSize": 2, "insertSpaces": false,
                  "wordWrap": false, "showInvisibles": true, "highlightCurrentQuery": false } }
                """);
            Assert.StartsWith("Consolas", editor.FontFamily.Source);
            Assert.Equal(20, editor.FontSize);
            Assert.Equal(2, editor.Options.IndentationSize);
            Assert.False(editor.Options.ConvertTabsToSpaces);
            Assert.False(editor.WordWrap);
            Assert.True(editor.Options.ShowTabs && editor.Options.ShowSpaces && editor.Options.ShowEndOfLine);
            Assert.False(view.HighlightsCurrentQuery);
            DoEvents();
            // lineHeight 2.0 at 20px: the line is about twice as tall as a 1.0 line at 20px (~ normal * 20 / 12 at 1.2).
            var doubled = LineHeight();
            var single20 = normal / 12.0 * 20.0 / 1.0;
            Assert.True(doubled > single20 * 1.5, $"lineHeight not applied: {doubled:F1}px vs ~{single20:F1}px for one line");

            Save("""{ "editor": { "lineHeight": 1.0, "highlightCurrentQuery": true, "wordWrap": true, "showInvisibles": false } }""");
            DoEvents();
            Assert.True(view.HighlightsCurrentQuery);
            Assert.True(editor.WordWrap);
            Assert.False(editor.Options.ShowTabs);
            Assert.Equal(12, editor.FontSize);
            Assert.True(LineHeight() < normal * 1.05, "lineHeight 1.0 should not be taller than the default 1.2 line");
            window.Close();
            tab.OnClosed(TabCloseReason.User);
        });
    }

    [Fact]
    public void Editor_autosave_debounce_follows_settings_live()
    {
        RunSta(() =>
        {
            InitSta(new WorkspaceFeature(), new SqlEditorFeature());
            var ws = NewWorkspace();
            var tab = (SqlEditorTabViewModel)ws.NewTab();
            tab.Document.Insert(0, "a");
            Assert.True(tab.AutoSaveDelay <= TimeSpan.FromMilliseconds(500));
            Save("""{ "editor": { "autoSaveDebounceMs": 2000 } }""");
            tab.Document.Insert(0, "b");
            Assert.True(tab.AutoSaveDelay > TimeSpan.FromMilliseconds(1500), $"debounce still {tab.AutoSaveDelay}");
            tab.OnClosed(TabCloseReason.User);
        });
    }

    [Fact]
    public void Editor_default_row_limit_follows_settings_until_the_connection_remembers_one()
    {
        RunSta(() =>
        {
            InitSta(new WorkspaceFeature(), new SqlEditorFeature());
            var ws = NewWorkspace();
            var tab = (SqlEditorTabViewModel)ws.NewTab();
            Assert.Equal(1000, tab.RowLimit);
            Save("""{ "editor": { "defaultRowLimit": 300 } }""");
            Assert.Equal(300, EditorSessionManager.For(ws).RowLimit);
            Assert.Equal(300, tab.RowLimit);
            Assert.Equal(300, ((SqlEditorTabViewModel)ws.NewTab()).RowLimit);

            // Once the user picks a limit for this connection, the default no longer overrides it.
            tab.RowLimit = 5000;
            Save("""{ "editor": { "defaultRowLimit": 100 } }""");
            Assert.Equal(5000, tab.RowLimit);
        });
    }

    [Fact]
    public void Autocomplete_settings_apply_live_to_an_attached_editor()
    {
        RunSta(() =>
        {
            InitSta(new WorkspaceFeature());
            var editor = new ICSharpCode.AvalonEdit.TextEditor { FontFamily = new FontFamily("Consolas"), FontSize = 13 };
            var window = Host(editor, 600, 400);
            var md = new DbMetadata
            {
                SearchPath = ["public"],
                Schemas = [new SchemaInfo { Name = "public", IsLoaded = true, Tables = [new TableInfo { Schema = "public", Name = "users" }] }],
            };
            using var completion = SqlCompletion.Attach(editor, () => md);
            editor.Text = "sel";
            editor.CaretOffset = 3;
            editor.TextArea.Focus();

            completion.Open();
            Assert.Contains(completion.LastResult!.Items, i => i.InsertText.StartsWith("SELECT", StringComparison.Ordinal));
            completion.Close();

            Save("""{ "editor": { "autocomplete": { "uppercaseKeywords": false, "addSpace": true } } }""");
            completion.Open();
            Assert.Contains(completion.LastResult!.Items, i => i.InsertText == "select ");
            completion.Close();

            Save("""{ "editor": { "autocomplete": { "keywords": false } } }""");
            completion.Open();
            Assert.DoesNotContain(completion.LastResult!.Items, i => i.InsertText.Trim().Equals("select", StringComparison.OrdinalIgnoreCase));
            completion.Close();

            Save("""{ "editor": { "autocomplete": { "disabled": true } } }""");
            completion.Open();
            Assert.False(completion.IsOpen);
            window.Close();
        });
    }

    // ---- grid.* --------------------------------------------------------------------------------------------------------

    [Fact]
    public void Grid_settings_apply_live_to_an_open_grid()
    {
        RunSta(() =>
        {
            InitSta(new WorkspaceFeature());
            var grid = new ResultGrid();
            var window = Host(grid);
            grid.ResultColumns = [new ResultColumn(0, "id", "int4", 23), new ResultColumn(1, "note", "text", 25)];
            grid.Rows = new List<object?[]> { new object?[] { 1, null }, new object?[] { 2, "x" } };
            DoEvents();
            var inner = (DataGrid)grid.FindName("InnerGrid");
            CellText NullCell() => FindAll<CellText>(inner).First(c => c.IsNull);
            Assert.Equal("NULL", NullCell().Text);

            Save("""{ "grid": { "fontFamily": "Consolas", "fontSize": 18, "rowPadding": 6, "nullDisplay": "∅" } }""");
            DoEvents();
            Assert.Equal("∅", NullCell().Text);
            Assert.Equal(18, inner.FontSize);
            Assert.Equal("Consolas", inner.FontFamily.Source);
            Assert.Equal(GridResources.RowHeight(new FontFamily("Consolas"), 18, 6), inner.RowHeight);
            window.Close();
        });
    }

    // ---- sidebar.* -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Sidebar_settings_apply_live()
    {
        RunSta(() =>
        {
            InitSta(new WorkspaceFeature(), new SidebarFeature());
            var ws = NewWorkspace();
            var vm = SidebarFeature.For(ws);
            var view = (SidebarView)AppServices.Features.Panels.Create(WorkspacePanel.Sidebar, ws)!;
            var window = Host(view, 300, 500);
            Save("""
                { "sidebar": { "fontFamily": "Consolas", "fontSize": 17, "rowPadding": 7, "matching": "contains",
                  "lazyLoadSchemas": false, "loadSystemSchemas": true } }
                """);
            Assert.Equal(17, view.FontSize);
            Assert.Equal("Consolas", view.FontFamily.Source);
            Assert.Equal(7, vm.RowPadding.Top);
            Assert.Equal(SidebarMatching.Contains, vm.Matching);
            // Schema loading options reach the open workspace's metadata cache (used by the next refresh).
            Assert.False(ws.Metadata.Options.LazySchemas);
            Assert.True(ws.Metadata.Options.IncludeSystemSchemas);
            window.Close();
        });
    }

    // ---- table.*, connections.*, security.*, history.*, general.* ------------------------------------------------------

    [Fact]
    public void Table_page_size_follows_settings_until_changed_in_the_tab()
    {
        RunSta(() =>
        {
            InitSta(new WorkspaceFeature(), new TableDataFeature());
            var ws = NewWorkspace();
            var tab = Assert.IsType<TableTabViewModel>(ws.OpenTab(new TabRequest { Kind = TabKinds.Table, Schema = "public", Name = "t" }));
            Assert.Equal(300, tab.PageSize);
            Save("""{ "table": { "pageSize": 50 } }""");
            Assert.Equal(50, tab.PageSize);
            tab.GoTo(77, 0);
            Save("""{ "table": { "pageSize": 60 } }""");
            Assert.Equal(77, tab.PageSize);
        });
    }

    [Fact]
    public void Connection_history_safe_mode_and_new_tab_settings_apply()
    {
        RunSta(() =>
        {
            InitSta(new WorkspaceFeature(), new SqlEditorFeature());
            var ws = NewWorkspace();
            Assert.True(ws.Session.KeepAliveEnabled);
            Save("""
                { "connections": { "keepAlive": false }, "history": { "enabled": false, "maxEntries": 5 },
                  "security": { "defaultSafeMode": "alert2" }, "general": { "newTabOpens": "empty" } }
                """);
            Assert.False(ws.Session.KeepAliveEnabled);
            Assert.False(AppServices.History.Enabled);
            Assert.Equal(5, AppServices.History.MaxEntries);
            Assert.Equal(SafeModeLevel.Alert2, new ConnectionFormViewModel(null, allowConnect: false).SafeMode);
            Assert.IsType<EmptyTabViewModel>(ws.NewTab());
        });
    }

    // ---- theme.* -------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("light", "Light", "Light", false)]
    [InlineData("dark", "Dark", "Dark", true)]
    [InlineData("dark", "Nord", "Nord", true)]
    [InlineData("light", "Solarized Light", "Solarized Light", false)]
    public void Theme_settings_switch_every_built_in_theme_live(string mode, string name, string expected, bool dark)
    {
        RunSta(() =>
        {
            InitSta(new WorkspaceFeature());
            var window = Host(new Border());
            var key = dark ? "dark" : "light";
            Save($$"""{ "theme": { "mode": "{{mode}}", "{{key}}": "{{name}}" } }""");
            Assert.Equal(expected, AppServices.Theme.Current.Name);
            Assert.Equal(dark, AppServices.Theme.IsDark);
            var theme = AppServices.Themes.Find(name)!;
            foreach (var (k, v) in theme.Colors)
            {
                Assert.True(ThemeColor.TryParse(v, out var c));
                Assert.Equal(Color.FromArgb(c.A, c.R, c.G, c.B), ((SolidColorBrush)window.Resources["c." + k]).Color);
            }

            // Overrides apply on top, and removing them restores the theme colour.
            Save($$"""{ "theme": { "mode": "{{mode}}", "{{key}}": "{{name}}", "overrides": { "app.accent": "#123456" } } }""");
            Assert.Equal(Color.FromRgb(0x12, 0x34, 0x56), ((SolidColorBrush)window.Resources["c.app.accent"]).Color);
            Save($$"""{ "theme": { "mode": "{{mode}}", "{{key}}": "{{name}}" } }""");
            Assert.True(ThemeColor.TryParse(theme.Colors["app.accent"], out var accent));
            Assert.Equal(Color.FromRgb(accent.R, accent.G, accent.B), ((SolidColorBrush)window.Resources["c.app.accent"]).Color);
            window.Close();
        });
    }

    // ---- keymap, tags, toolbar -----------------------------------------------------------------------------------------

    [Fact]
    public void Keymap_rebind_and_unbind_apply_live()
    {
        RunSta(() =>
        {
            InitSta(new WorkspaceFeature());
            Assert.Equal(CommandIds.TabNext, AppServices.Keymap.Resolve(KeyChord.Parse("Ctrl+Tab"), CommandScope.Workspace));
            Save("""{ "keymap": { "tab.next": null, "tab.prev": ["Ctrl+Alt+Left", "Ctrl+Shift+Tab"] } }""");
            Assert.Null(AppServices.Keymap.Resolve(KeyChord.Parse("Ctrl+Tab"), CommandScope.Workspace));
            Assert.Empty(AppServices.Keymap.GetBindings(CommandIds.TabNext));
            Assert.Equal(CommandIds.TabPrev, AppServices.Keymap.Resolve(KeyChord.Parse("Ctrl+Alt+Left"), CommandScope.Workspace));
            Assert.Equal("", AppServices.Gestures[CommandIds.TabNext] ?? "");
        });
    }

    [Fact]
    public void Tags_and_toolbar_items_apply_live()
    {
        RunSta(() =>
        {
            // WorkspaceView needs Application-level StaticResources, so the toolbar is checked at the layout the view
            // rebuilds from on every SettingsChanged (WorkspaceView.BuildToolbar).
            InitSta(new WorkspaceFeature());
            var ws = NewWorkspace(TestApp.Profile() with { Tag = "production" });
            Save("""{ "toolbar": { "items": ["newSql", "toggleSidebar", "status"] }, "tags": { "production": "#00FF00" } }""");
            var layout = AppServices.Features.Toolbar.Layout(AppServices.Current.Toolbar.Items).Select(i => i.Id).ToList();
            Assert.Equal(["newSql", "toggleSidebar", "status"], layout);

            ws.RefreshTagBrush();
            Assert.Equal(Colors.Lime, ((SolidColorBrush)ws.TagBrush!).Color);
        });
    }

    // ---- dark theme readability ---------------------------------------------------------------------------------------

    [Fact]
    public void Dark_theme_reaches_scrollbars_grid_corner_tooltips_and_search_panel()
    {
        RunSta(() =>
        {
            InitSta(new WorkspaceFeature(), new SqlEditorFeature());
            var panel = new DockPanel();
            var scroll = new ScrollViewer { Height = 200, Width = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Visible, HorizontalScrollBarVisibility = ScrollBarVisibility.Visible, Content = new Border { Height = 5000, Width = 5000 } };
            var grid = new ResultGrid { Width = 300, Height = 200 };
            var tip = new ToolTip { Content = "tip" };
            var menu = new ContextMenu();
            menu.Items.Add(new MenuItem { Header = "A", InputGestureText = "Ctrl+Shift+Alt+F12" });
            menu.Items.Add(new MenuItem { Header = "Longer header", InputGestureText = "F2" });
            var ws = NewWorkspace();
            var tab = (SqlEditorTabViewModel)ws.NewTab();
            var editor = (SqlEditorView)tab.CreateView();
            editor.Width = 600;
            editor.Height = 300;
            _ = tip;
            _ = menu;
            var left = new StackPanel { Children = { scroll, grid } };
            panel.Children.Add(left);
            panel.Children.Add(editor);
            var window = Host(new Border { Background = Brushes.Black, Child = panel }, 1100, 800);
            Save("""{ "theme": { "mode": "dark" } }""");
            grid.ResultColumns = [new ResultColumn(0, "id", "int4", 23)];
            grid.Rows = new List<object?[]> { new object?[] { 1 } };
            editor.OpenFind(replace: false);
            foreach (var popup in new FrameworkElement[] { tip, menu }) { popup.Resources.MergedDictionaries.Add(window.Resources); popup.Style = (Style)window.FindResource(popup.GetType()); popup.Measure(new Size(400, 400)); popup.Arrange(new Rect(popup.DesiredSize)); popup.UpdateLayout(); }
            // Popups are separate HWNDs; render the tooltip and menu standalone with the same (window) resources.
            DoEvents();

            var vbar = Find<ScrollBar>(scroll, b => b.Orientation == Orientation.Vertical);
            Assert.True(Luminance(window, vbar, new Point(vbar.ActualWidth / 2, vbar.ActualHeight - 30)) < 0.3, "scrollbar track is light");
            var thumb = Find<Thumb>(vbar);
            var thumbL = Luminance(window, thumb, new Point(thumb.ActualWidth / 2, thumb.ActualHeight / 2));
            Assert.True(thumbL > Luminance(window, vbar, new Point(vbar.ActualWidth / 2, vbar.ActualHeight - 30)) + 0.05, "thumb not visible on the track");
            Assert.True(Luminance(window, scroll, new Point(scroll.ActualWidth - 3, scroll.ActualHeight - 3)) < 0.3, "scroll corner is light");

            var inner = (DataGrid)grid.FindName("InnerGrid");
            Assert.True(Luminance(window, inner, new Point(4, 4)) < 0.3, "grid select-all corner is light");
            Assert.True(Luminance(window, tip, new Point(3, 3)) < 0.3, "tooltip is light");

            var gestures = FindAll<TextBlock>(menu).Where(t => t.Text is "Ctrl+Shift+Alt+F12" or "F2").ToList();
            Assert.Equal(2, gestures.Count);
            Assert.Equal(gestures[0].TranslatePoint(default, menu).X, gestures[1].TranslatePoint(default, menu).X, 1);

            var search = FindAll<SearchPanel>(window).Single();
            var searchBorder = Find<Border>(search);
            Assert.True(Luminance(window, searchBorder, new Point(2, 2)) < 0.3, "search panel is light");
            window.Close();
            tab.OnClosed(TabCloseReason.User);
        });
    }

    [Fact]
    public void Dark_theme_reaches_the_completion_popup()
    {
        RunSta(() =>
        {
            InitSta(new WorkspaceFeature());
            var editor = new ICSharpCode.AvalonEdit.TextEditor { FontFamily = new FontFamily("Consolas"), FontSize = 13 };
            var window = Host(editor, 600, 400);
            Save("""{ "theme": { "mode": "dark" } }""");
            var md = new DbMetadata
            {
                SearchPath = ["public"],
                Schemas = [new SchemaInfo { Name = "public", IsLoaded = true, Tables = [.. Enumerable.Range(0, 60).Select(i => new TableInfo { Schema = "public", Name = "t" + i })] }],
            };
            using var completion = SqlCompletion.Attach(editor, () => md);
            editor.Text = "select * from t";
            editor.CaretOffset = editor.Text.Length;
            editor.TextArea.Focus();
            completion.Open();
            var popup = completion.Window!;
            // Application.Resources stand-in: in the app the popup window resolves the same dictionary.
            popup.Resources.MergedDictionaries.Add(window.Resources);
            DoEvents();
            var bar = Find<ScrollBar>(popup, b => b.Orientation == Orientation.Vertical && b.IsVisible);
            var rtb = new RenderTargetBitmap((int)popup.ActualWidth, (int)popup.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            var root = (FrameworkElement)VisualTreeHelper.GetChild(popup, 0);
            rtb.Render(root);
            var p = bar.TranslatePoint(new Point(bar.ActualWidth / 2, bar.ActualHeight - 10), root);
            var px = new byte[4];
            rtb.CopyPixels(new Int32Rect((int)p.X, (int)p.Y, 1, 1), px, 4, 0);
            Assert.True((0.2126 * px[2] + 0.7152 * px[1] + 0.0722 * px[0]) / 255.0 < 0.3, "completion scrollbar is light");
            completion.Close();
            window.Close();
        });
    }
}
