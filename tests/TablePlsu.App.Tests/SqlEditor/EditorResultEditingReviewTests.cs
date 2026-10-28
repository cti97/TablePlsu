using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Npgsql;
using TablePlsu.App.Features.Results;
using TablePlsu.App.Features.SafeMode;
using TablePlsu.App.Features.SqlEditor;
using TablePlsu.App.Features.Workspace;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Commands;
using TablePlsu.Core.Db;
using TablePlsu.Core.Keymap;
using TablePlsu.Core.Settings;
using TablePlsu.Core.Themes;
using Xunit.Abstractions;

namespace TablePlsu.App.Tests.SqlEditor;

/// <summary>Adversarial review (ux-state) of spec §10 "Edit results inline": cases where the user loses edits or sees wrong state.</summary>
[Collection(AppServicesCollection.Name)]
[Trait("Review", "ux-state")]
public class EditorResultEditingReviewTests(ITestOutputHelper output)
{
    private static void RunSta(Func<Task> body)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try
            {
                var task = body();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!task.IsCompleted && sw.Elapsed < TimeSpan.FromSeconds(90))
                {
                    DoEvents();
                    if (!task.IsCompleted) Thread.Sleep(5);
                }
                if (!task.IsCompleted) throw new TimeoutException("STA body did not finish.");
                task.GetAwaiter().GetResult();
            }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static async Task<(WorkspaceViewModel Ws, SqlEditorTabViewModel Tab)> ConnectAsync()
    {
        TestApp.Init(new WorkspaceFeature(), new SqlEditorFeature(), new SafeModeFeature());
        var (profile, secrets) = TestPg.Profile();
        var ws = new WorkspaceViewModel(profile, secrets, null, shell: null);
        AppServices.Features.RaiseWorkspaceOpened(ws);
        await ws.ConnectAsync();
        for (var i = 0; i < 100 && ws.State != DbSessionState.Connected; i++) await Task.Delay(100);
        Assert.Equal(DbSessionState.Connected, ws.State);
        return (ws, (SqlEditorTabViewModel)ws.NewTab());
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }

    private static object? Scalar(string sql)
    {
        using var admin = new NpgsqlConnection(TestPg.ConnectionString);
        admin.Open();
        using var c = new NpgsqlCommand(sql, admin);
        return c.ExecuteScalar();
    }

    private static (Window Window, SqlEditorView View) Show(SqlEditorTabViewModel tab, WorkspaceViewModel ws)
    {
        var view = (SqlEditorView)tab.CreateView();
        // Like the shell's workspace view: a Workspace scope root whose DataContext is the workspace.
        var host = new Border { DataContext = ws, Child = view };
        CommandScopes.SetScope(host, CommandScope.Workspace);
        var window = new Window
        {
            Width = 1000, Height = 700, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Content = host,
        };
        window.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
            new Uri("/TablePlsu.App;component/Shell/Styles.xaml", UriKind.Relative)));
        ThemeApplier.ApplyTo(window.Resources, ThemeService.BuiltInLight);
        GridResources.ApplyTo(window.Resources, new GridSettings());
        window.Show();
        return (window, view);
    }

    /// <summary>Starts editing (row, column) of the shown result grid and types <paramref name="text"/> into the cell editor.</summary>
    private static TextBox TypeIntoCell(ResultGrid grid, object?[] row, int column, string text)
    {
        grid.ScrollToRow(grid.Rows!.IndexOf(row), column);
        DoEvents();
        Assert.True(grid.BeginEditCurrentCell());
        DoEvents();
        var cell = (DataGridCell)grid.DataGrid.Columns[column].GetCellContent(row)!.Parent;
        var box = Assert.IsType<TextBox>(cell.Content);
        box.Text = text;
        box.Focus();
        DoEvents();
        return box;
    }

    private static string NewTable(string suffix)
    {
        var table = $"rv_{suffix}_" + Guid.NewGuid().ToString("N")[..8];
        Scalar($"CREATE TABLE public.{table}(id int primary key, name text); INSERT INTO public.{table} VALUES (1,'a'),(2,'b'),(3,'c')");
        return table;
    }

    /// <summary>
    /// Two editable results. The user edits result 1, switches to result 2 (which has no edits: no "pending" counter shows,
    /// since the counter is bound to the selected result) and presses Ctrl+Z in its grid. Expected: nothing to undo there.
    /// Actual: PendingResult falls back to result 1 and silently reverts the edit the user can no longer see.
    /// </summary>
    [Fact]
    public void Ctrl_Z_in_a_clean_result_does_not_undo_a_hidden_edit_in_another_result()
    {
        if (!TestPg.Available(output)) return;
        var a = NewTable("a");
        var b = NewTable("b");
        try
        {
            RunSta(async () =>
            {
                var (ws, tab) = await ConnectAsync();
                try
                {
                    tab.Document.Text = $"select * from public.{a} order by id;\nselect * from public.{b} order by id";
                    await tab.RunAllAsync();
                    Assert.Equal(2, tab.Results.Count);
                    var ra = tab.Results[0];
                    var rb = tab.Results[1];
                    await Until(() => ra.IsEditable && rb.IsEditable);

                    ra.CommitCellEdit(ra.Rows[0], 1, "edited");
                    Assert.True(ra.HasPendingChanges);
                    tab.SelectedResult = rb;
                    Assert.False(tab.SelectedResult.HasPendingChanges); // the view's "N pending" counter is hidden now

                    // Ctrl+Z with the (clean) result B grid focused.
                    var handled = tab.TryExecuteCommand(CommandIds.EditUndo, new CommandContext { CommandId = CommandIds.EditUndo });
                    Assert.False(handled);
                    Assert.True(ra.HasPendingChanges, "Ctrl+Z in result B silently reverted the edit staged in result A");
                    Assert.Equal("edited", ra.Rows[0][1]);
                }
                finally
                {
                    await ws.CloseAsync();
                }
            });
        }
        finally
        {
            Scalar($"DROP TABLE IF EXISTS public.{a}; DROP TABLE IF EXISTS public.{b}");
        }
    }

    /// <summary>
    /// The user types a new value into a cell and, while the cell editor is still open, clicks another result tab
    /// (changing SelectedResult). Expected: the typed value is staged in the first result (or kept). Actual: the grid
    /// rebinds to the other result, ResultGrid.RebuildColumns cancels the edit and the typed value is gone.
    /// </summary>
    [Fact]
    public void Switching_result_tab_while_a_cell_is_being_edited_keeps_the_typed_value()
    {
        if (!TestPg.Available(output)) return;
        var a = NewTable("c");
        var b = NewTable("d");
        try
        {
            RunSta(async () =>
            {
                UiDispatch.Initialize(Dispatcher.CurrentDispatcher);
                var (ws, tab) = await ConnectAsync();
                var (window, view) = Show(tab, ws);
                try
                {
                    tab.Document.Text = $"select * from public.{a} order by id;\nselect * from public.{b} order by id";
                    await tab.RunAllAsync();
                    var ra = tab.Results[0];
                    var rb = tab.Results[1];
                    await Until(() => ra.IsEditable && rb.IsEditable);
                    tab.SelectedResult = ra;
                    await Until(() => !view.ResultGrid.IsReadOnly && ReferenceEquals(view.ResultGrid.DataContext, ra));

                    TypeIntoCell(view.ResultGrid, ra.Rows[0], 1, "typed");
                    tab.SelectedResult = rb; // click on the other result tab
                    DoEvents();

                    Assert.True(ra.HasPendingChanges, "the value typed into result A's cell was dropped when switching result tabs");
                }
                finally
                {
                    window.Close();
                    await ws.CloseAsync();
                    UiDispatch.Initialize(null!);
                }
            });
        }
        finally
        {
            Scalar($"DROP TABLE IF EXISTS public.{a}; DROP TABLE IF EXISTS public.{b}");
        }
    }

    /// <summary>
    /// The user double-clicks a cell, types a value and presses Ctrl+S without pressing Enter first (the natural
    /// "edit and save" gesture). Expected: the typed value is committed. Actual: the cell edit is not staged, so the
    /// editor tab has no pending changes and Ctrl+S falls through to "save the SQL text"; the database is unchanged.
    /// </summary>
    [Fact]
    public void Ctrl_S_while_a_cell_is_being_edited_commits_the_typed_value()
    {
        if (!TestPg.Available(output)) return;
        var a = NewTable("e");
        try
        {
            RunSta(async () =>
            {
                UiDispatch.Initialize(Dispatcher.CurrentDispatcher);
                var (ws, tab) = await ConnectAsync();
                var (window, view) = Show(tab, ws);
                try
                {
                    tab.Document.Text = $"select * from public.{a} order by id";
                    await tab.RunAllAsync();
                    var ra = Assert.Single(tab.Results);
                    await Until(() => ra.IsEditable);
                    await Until(() => !view.ResultGrid.IsReadOnly);

                    var box = TypeIntoCell(view.ResultGrid, ra.Rows[0], 1, "saved");
                    Assert.True(KeyBindingRouter.Dispatch(window, KeyChord.Parse("Ctrl+S"), box, AppServices.Keymap, AppServices.Commands));
                    await Until(() => !ra.IsCommitting);
                    await Task.Delay(500);

                    Assert.Equal("saved", Scalar($"SELECT name FROM public.{a} WHERE id = 1"));
                }
                finally
                {
                    window.Close();
                    await ws.CloseAsync();
                    UiDispatch.Initialize(null!);
                }
            });
        }
        finally
        {
            Scalar($"DROP TABLE IF EXISTS public.{a}");
        }
    }
}
