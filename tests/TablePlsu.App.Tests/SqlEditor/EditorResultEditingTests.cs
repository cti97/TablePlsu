using System.Windows;
using System.Windows.Threading;
using Npgsql;
using TablePlsu.App.Features.Results;
using TablePlsu.App.Features.SafeMode;
using TablePlsu.App.Features.SqlEditor;
using TablePlsu.App.Features.Workspace;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Commands;
using TablePlsu.Core.Db;
using TablePlsu.Core.Settings;
using TablePlsu.Core.Themes;
using Xunit.Abstractions;

namespace TablePlsu.App.Tests.SqlEditor;

/// <summary>Spec §10 "Edit results inline": editing SQL editor results through the table tab's staging and commit flow.</summary>
[Collection(AppServicesCollection.Name)]
public class EditorResultEditingTests(ITestOutputHelper output)
{
    private static void OnUiThread(Func<Task> body)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var frame = new DispatcherFrame();
            var task = body();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            error = task.Exception?.GetBaseException();
            dispatcher.InvokeShutdown();
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        if (!t.Join(TimeSpan.FromSeconds(90))) throw new TimeoutException("UI-thread test body did not finish.");
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    /// <summary>Runs <paramref name="body"/> on an STA thread whose awaits resume on its dispatcher (pumped, for views).</summary>
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
                    var frame = new DispatcherFrame();
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
                    Dispatcher.PushFrame(frame);
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
        if (error is not null) throw new Xunit.Sdk.XunitException("STA body failed: " + error);
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

    private static void Exec(string sql) => Scalar(sql);

    private static CommandContext Ctx(string id) => new() { CommandId = id };

    [Fact]
    public void Editing_a_select_result_previews_commits_and_shows_the_committed_rows()
    {
        if (!TestPg.Available(output)) return;
        var table = "ed_res_" + Guid.NewGuid().ToString("N")[..10];
        Exec($"CREATE TABLE public.{table}(id serial primary key, name text not null, qty int default 7); " +
             $"INSERT INTO public.{table}(name, qty) VALUES ('a', 1), ('b', 2), ('c', 3)");
        try
        {
            OnUiThread(async () =>
            {
                var (ws, tab) = await ConnectAsync();
                try
                {
                    tab.Document.Text = $"select * from public.{table} order by id";
                    await tab.RunAllAsync();
                    var result = Assert.Single(tab.Results);
                    await Until(() => result.IsEditable);
                    Assert.Null(result.ReadOnlyReason);
                    Assert.False(result.IsReadOnlyGrid);
                    var rows = result.Rows.ToList();

                    // Edit a cell, insert a row (DEFAULT id and qty), delete a row; undo/redo through the editor tab.
                    Assert.Equal("zz", result.CommitCellEdit(rows[0], 1, "zz"));
                    Assert.Equal(RowState.Modified, result.GetRowState(rows[0]));
                    Assert.True(tab.HasPendingChanges);
                    Assert.True(result.TryExecuteCommand(CommandIds.GridInsertRow, Ctx(CommandIds.GridInsertRow)));
                    var inserted = result.Rows[^1];
                    Assert.Equal(RowState.New, result.GetRowState(inserted));
                    result.CommitCellEdit(inserted, 1, "new");
                    result.Pending!.DeleteRow(rows[2]);
                    Assert.True(tab.TryExecuteCommand(CommandIds.EditUndo, Ctx(CommandIds.EditUndo)));
                    Assert.Equal(RowState.Unchanged, result.GetRowState(rows[2]));
                    Assert.True(tab.TryExecuteCommand(CommandIds.EditRedo, Ctx(CommandIds.EditRedo)));
                    Assert.Equal(RowState.Deleted, result.GetRowState(rows[2]));
                    Assert.False(tab.TryExecuteCommand(CommandIds.EditUndo, new CommandContext { CommandId = CommandIds.EditUndo, IsTextInputFocused = true }));

                    // Preview: the exact statements, by primary key, reading the committed rows back.
                    var sql = result.GenerateStatements().Select(s => s.LiteralSql).ToList();
                    Assert.Equal(
                    [
                        $"UPDATE public.{table} SET name = 'zz' WHERE id = 1 RETURNING id, name, qty",
                        $"DELETE FROM public.{table} WHERE id = 3",
                        $"INSERT INTO public.{table} (name) VALUES ('new') RETURNING id, name, qty",
                    ], sql);

                    // Re-running with staged edits asks first; "keep" leaves everything as it was.
                    var asked = new List<string>();
                    tab.ConfirmDiscard = m => { asked.Add(m); return false; };
                    await tab.RunAllAsync();
                    Assert.Equal("Discard 3 pending changes to the results?", Assert.Single(asked));
                    Assert.Same(result, Assert.Single(tab.Results));
                    Assert.True(result.HasPendingChanges);
                    Assert.False(tab.CanCloseNow(TabCloseReason.User));
                    Assert.Equal(2, asked.Count);

                    // Ctrl+S (the workspace commit path): one transaction; the grid shows what the server stored.
                    await tab.CommitAsync();
                    Assert.False(result.HasPendingChanges);
                    Assert.False(tab.HasPendingChanges);
                    Assert.Null(result.LastCommitError);
                    Assert.Equal("zz", Scalar($"SELECT name FROM public.{table} WHERE id = 1"));
                    Assert.Equal(0L, Scalar($"SELECT count(*) FROM public.{table} WHERE id = 3"));
                    var newId = Convert.ToInt32(Scalar($"SELECT id FROM public.{table} WHERE name = 'new'"));
                    Assert.Equal(3, result.Rows.Count);
                    Assert.DoesNotContain(rows[2], result.Rows);
                    Assert.Equal("zz", rows[0][1]);
                    Assert.Equal(1, rows[0][2]);
                    Assert.Equal(newId, inserted[0]);  // DEFAULT became the real serial value
                    Assert.Equal(7, inserted[2]);      // and the column default is shown, not "DEFAULT"
                    Assert.Equal(RowState.Unchanged, result.GetRowState(inserted));
                    Assert.Equal(3, result.RowCount);
                    Assert.Contains(ws.History.Load(ws.Profile.Id), h => h.Source == "commit" && h.Sql.StartsWith($"UPDATE public.{table} SET name = 'zz'", StringComparison.Ordinal));

                    // The committed new row is now addressed by its real key.
                    result.CommitCellEdit(inserted, 2, "8");
                    Assert.Equal($"UPDATE public.{table} SET qty = '8' WHERE id = {newId} RETURNING id, name, qty", result.GenerateStatements()[0].LiteralSql);
                    Assert.True(await result.CommitCoreAsync(showErrors: false));
                    Assert.Equal(8, Convert.ToInt32(Scalar($"SELECT qty FROM public.{table} WHERE id = {newId}")));
                    Assert.Equal(8, inserted[2]);

                    // A failing commit keeps the edits staged and reports the statement.
                    result.CommitCellEdit(rows[1], 1, "");
                    result.Pending!.SetCell(rows[1], 1, null); // NOT NULL
                    Assert.False(await result.CommitCoreAsync(showErrors: false));
                    Assert.True(result.HasPendingChanges);
                    Assert.Equal(0, result.LastFailedStatement);
                    tab.DiscardChanges();
                    Assert.False(tab.HasPendingChanges);
                    Assert.Equal("b", rows[1][1]);

                    // With nothing staged, re-running does not ask.
                    asked.Clear();
                    await tab.RunAllAsync();
                    Assert.Empty(asked);
                    Assert.NotSame(result, Assert.Single(tab.Results));
                }
                finally
                {
                    await ws.CloseAsync();
                }
            });
        }
        finally
        {
            Exec($"DROP TABLE IF EXISTS public.{table}");
        }
    }

    [Fact]
    public void A_join_result_is_read_only_and_says_why()
    {
        if (!TestPg.Available(output)) return;
        OnUiThread(async () =>
        {
            var (ws, tab) = await ConnectAsync();
            try
            {
                tab.Document.Text = "select o.id, o.total, c.name from sales.orders o join public.customers c on c.id = o.customer_id limit 5;\nselect 1 as one";
                await tab.RunAllAsync();
                var join = tab.Results[0];
                await Until(() => join.ReadOnlyReason is not null);
                Assert.False(join.IsEditable);
                Assert.True(join.IsReadOnlyGrid);
                Assert.Equal("Read-only — result combines columns from several tables", join.ReadOnlyText);

                // Trying to edit (F2 / insert / paste…) flashes the reason instead of doing nothing.
                Assert.True(join.TryExecuteCommand(CommandIds.GridEditCell, Ctx(CommandIds.GridEditCell)));
                Assert.True(join.TryExecuteCommand(CommandIds.GridInsertRow, Ctx(CommandIds.GridInsertRow)));
                Assert.Equal(2, join.ReadOnlyAttempts);
                Assert.Null(join.InsertRow());
                Assert.False(join.TryExecuteCommand(CommandIds.GridCopy, Ctx(CommandIds.GridCopy))); // copy still works (registry)

                var one = tab.Results[1];
                await Until(() => one.ReadOnlyReason is not null);
                Assert.Equal("Read-only — column one is computed, not read from a table", one.ReadOnlyText);
                Assert.False(tab.HasPendingChanges);
            }
            finally
            {
                await ws.CloseAsync();
            }
        });
    }

    [Fact]
    public void The_editor_view_shows_the_reason_bar_and_an_editable_grid()
    {
        if (!TestPg.Available(output)) return;
        RunSta(async () =>
        {
            UiDispatch.Initialize(Dispatcher.CurrentDispatcher); // streamed rows reach the bound grid on this thread
            var (ws, tab) = await ConnectAsync();
            var view = (SqlEditorView)tab.CreateView();
            var window = new Window
            {
                Width = 1000, Height = 700, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Content = view,
            };
            window.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
                new Uri("/TablePlsu.App;component/Shell/Styles.xaml", UriKind.Relative)));
            ThemeApplier.ApplyTo(window.Resources, ThemeService.BuiltInLight);
            GridResources.ApplyTo(window.Resources, new GridSettings());
            window.Show();
            try
            {
                var bar = (FrameworkElement)view.FindName("ReadOnlyBar");
                tab.Document.Text = "select o.id, c.name from sales.orders o join public.customers c on c.id = o.customer_id limit 5";
                await tab.RunAllAsync();
                var join = tab.Results[0];
                await Until(() => join.ReadOnlyReason is not null);
                await Until(() => bar.IsVisible);
                Assert.True(view.ResultGrid.IsReadOnly);
                Assert.Same(join, view.ResultGrid.DataContext);
                Assert.Equal("Read-only — result combines columns from several tables", ((System.Windows.Controls.TextBlock)view.FindName("ReadOnlyText")).Text);

                tab.Document.Text = "select * from public.customers order by id limit 10";
                await tab.RunAllAsync();
                var customers = tab.Results[0];
                await Until(() => customers.IsEditable);
                await Until(() => !view.ResultGrid.IsReadOnly);
                Assert.False(bar.IsVisible);
                Assert.Same(view.ResultGrid, customers.Grid);
            }
            finally
            {
                window.Close();
                await ws.CloseAsync();
                UiDispatch.Initialize(null!);
            }
        });
    }
}
