using System.Windows;
using System.Windows.Threading;
using TablePlsu.App.Features.Console;
using TablePlsu.App.Features.Results;
using TablePlsu.App.Features.SqlEditor;
using TablePlsu.App.Features.Workspace;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Connections;
using TablePlsu.Core.Settings;
using TablePlsu.Core.Themes;

namespace TablePlsu.App.Tests.SqlEditor;

/// <summary>Builds the real editor and console views in an off-screen window (XAML, resources, wiring).</summary>
[Collection(AppServicesCollection.Name)]
public class SqlEditorViewTests
{

    private static void RunSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ex; }
            finally { try { Dispatcher.CurrentDispatcher.InvokeShutdown(); } catch (NullReferenceException) { } }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException("STA body failed: " + error);
    }

    private static void DoEvents() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static Window Host(FrameworkElement content)
    {
        var window = new Window
        {
            Width = 1000, Height = 700, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        window.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/TablePlsu.App;component/Shell/Styles.xaml", UriKind.Relative)));
        ThemeApplier.ApplyTo(window.Resources, ThemeService.BuiltInDark);
        GridResources.ApplyTo(window.Resources, new GridSettings());
        window.Content = content;
        window.Show();
        DoEvents();
        return window;
    }

    [Fact]
    public void Editor_view_builds_highlights_and_runs_view_operations()
    {
        RunSta(() =>
        {
            TestApp.Init(new WorkspaceFeature(), new SqlEditorFeature(), new ConsoleFeature());
            var ws = new WorkspaceViewModel(TestApp.Profile(), ConnectionSecrets.None, null, shell: null);
            var tab = (SqlEditorTabViewModel)ws.NewTab();
            tab.Document.Text = "select 1;\nselect 2;";
            var view = (SqlEditorView)tab.CreateView();
            var window = Host(view);
            try
            {
                Assert.Same(tab.Document, view.TextEditor.Document);
                Assert.Same(view, tab.View);
                Assert.Equal(SqlHighlighting.Name, view.TextEditor.SyntaxHighlighting.Name);

                view.TextEditor.CaretOffset = 2;
                view.ToggleLineComment();
                Assert.Equal("-- select 1;\nselect 2;", tab.Document.Text);
                view.ToggleLineComment();
                Assert.Equal("select 1;\nselect 2;", tab.Document.Text);

                var size = view.TextEditor.FontSize;
                view.ZoomFont(1);
                Assert.Equal(size + 1, view.TextEditor.FontSize);

                tab.IsResultsVisible = false;
                DoEvents();
                Assert.Equal(Visibility.Collapsed, ((UIElement)view.FindName("ResultsPanel")).Visibility);
                tab.IsResultsVisible = true;
                Assert.Equal(Visibility.Visible, ((UIElement)view.FindName("ResultsPanel")).Visibility);

                // Restored caret/selection state flows back to the view-model.
                view.TextEditor.Select(0, 6);
                DoEvents();
                Assert.Equal((0, 6), (tab.SelectionStart, tab.SelectionLength));

                // Settings apply live.
                view.ApplySettings(new AppSettings { Editor = new EditorSettings { WordWrap = false, ShowInvisibles = true, TabSize = 2 } });
                Assert.False(view.TextEditor.WordWrap);
                Assert.True(view.TextEditor.Options.ShowSpaces);
                Assert.Equal(2, view.TextEditor.Options.IndentationSize);

                // The completion hook is invoked once per view.
                var calls = 0;
                SqlEditorHooks.AttachCompletion = (_, w) => { if (ReferenceEquals(w, ws)) calls++; };
                var tab2 = (SqlEditorTabViewModel)ws.NewTab();
                _ = tab2.CreateView();
                Assert.Equal(1, calls);
            }
            finally
            {
                SqlEditorHooks.AttachCompletion = null;
                window.Close();
                ws.CloseAsync().Wait();
            }
        });
    }

    [Fact]
    public void Console_panel_shows_workspace_log()
    {
        RunSta(() =>
        {
            TestApp.Init(new WorkspaceFeature(), new SqlEditorFeature(), new ConsoleFeature());
            var ws = new WorkspaceViewModel(TestApp.Profile(), ConnectionSecrets.None, null, shell: null);
            var panel = AppServices.Features.Panels.Create(WorkspacePanel.Console, ws);
            var view = Assert.IsType<ConsoleView>(panel);
            var window = Host(view);
            try
            {
                ws.Log("hello console");
                ws.Log("boom", ConsoleEntryKind.Error);
                DoEvents();
                var vm = (ConsoleViewModel)view.DataContext;
                Assert.Equal(2, vm.Entries.Count);
                Assert.Contains("hello console", ConsoleViewModel.Format(vm.Entries));
                vm.Clear();
                DoEvents();
                Assert.Empty(vm.Entries);
            }
            finally
            {
                window.Close();
                ws.CloseAsync().Wait();
            }
        });
    }
}
