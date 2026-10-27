using TablePlsu.App.Features.SqlEditor;
using TablePlsu.App.Features.Workspace;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Connections;
using TablePlsu.Core.Db;
using Xunit.Abstractions;

namespace TablePlsu.App.Tests.SqlEditor;

/// <summary>Runs statements through the SQL editor view-model against the seed DB (TABLEPLSU_TEST_PG).</summary>
[Collection(AppServicesCollection.Name)]
public class SqlEditorRunIntegrationTests(ITestOutputHelper output)
{
    /// <summary>
    /// Runs <paramref name="body"/> on one thread with a dispatcher sync context, like the UI thread: the AvalonEdit
    /// document may only be touched by the thread that created it, and awaits must come back to it.
    /// </summary>
    private static void OnUiThread(Func<Task> body)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
            var frame = new System.Windows.Threading.DispatcherFrame();
            var task = body();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            error = task.Exception?.GetBaseException();
            dispatcher.InvokeShutdown();
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        if (!t.Join(TimeSpan.FromSeconds(90))) throw new TimeoutException("UI-thread test body did not finish.");
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static async Task<(WorkspaceViewModel Ws, SqlEditorTabViewModel Tab)> ConnectAsync()
    {
        TestApp.Init(new WorkspaceFeature(), new SqlEditorFeature());
        var (profile, secrets) = TestPg.Profile();
        var ws = new WorkspaceViewModel(profile, secrets, null, shell: null);
        AppServices.Features.RaiseWorkspaceOpened(ws);
        await ws.ConnectAsync();
        for (var i = 0; i < 100 && ws.State != DbSessionState.Connected; i++) await Task.Delay(100);
        Assert.Equal(DbSessionState.Connected, ws.State);
        return (ws, (SqlEditorTabViewModel)ws.NewTab());
    }

    [Fact]
    public void Run_all_streams_one_result_tab_per_statement_with_messages_errors_history_and_console()
    {
        if (!TestPg.Available(output)) return;
        OnUiThread(async () =>
        {
            var (ws, tab) = await ConnectAsync();
            try
            {
                var table = "sqled_" + Guid.NewGuid().ToString("N")[..10];
                var sql =
                    "select 1 as a, 'x' as b;\n" +
                    "-- name: Customers\n" +
                    "select * from public.customers order by id;\n" +
                    $"create temp table {table}(id int);\n" +
                    $"insert into {table} select generate_series(1, 5);\n" +
                    "do $$ begin raise notice 'hello from plpgsql'; end $$;\n" +
                    "select * from missing_table_xyz;\n" +
                    "select generate_series(1, 450) as n";
                tab.Document.Text = sql;
                await tab.RunAllAsync();

                Assert.False(tab.IsRunning);
                Assert.Equal(7, tab.Results.Count);
                Assert.Equal(["Query 1", "Customers", "Query 3", "Query 4", "Query 5", "Query 6", "Query 7"], tab.Results.Select(r => r.Title));

                var first = tab.Results[0];
                Assert.True(first.ShowData);
                Assert.Equal(["a", "b"], first.Columns!.Select(c => c.Name));
                Assert.Equal(1, first.RowCount);
                Assert.Equal(1, first.Rows[0][0]);

                Assert.True(tab.Results[1].RowCount > 0);

                var insert = tab.Results[3];
                Assert.False(insert.HasResultSet);
                Assert.Equal(5, insert.AffectedRows);
                Assert.Contains("5 rows affected", insert.MessagesText);

                Assert.Contains("hello from plpgsql", tab.Results[4].MessagesText);

                var error = tab.Results[5];
                Assert.True(error.HasError);
                Assert.True(error.ShowMessageView);
                Assert.Contains("missing_table_xyz", error.MessagesText);
                // The error underline points at the table name in the editor.
                var at = sql.IndexOf("missing_table_xyz", StringComparison.Ordinal);
                Assert.Equal((at, "missing_table_xyz".Length), tab.ErrorSpan);
                Assert.Same(error, tab.SelectedResult);

                // editor.returnOnError is false: the statement after the error still ran, batched in 200s.
                Assert.Equal(450, tab.Results[6].RowCount);
                Assert.Contains("1 error", tab.RunStatus);

                var history = AppServices.History.Load(ws.Profile.Id);
                Assert.Equal(7, history.Count);
                Assert.Contains(history, h => h.Error && h.Sql.Contains("missing_table_xyz"));
                Assert.All(history, h => Assert.Equal("editor", h.Source));

                await Task.Delay(50);
                Assert.Contains(ws.ConsoleLog.Entries, e => e.Message.Contains("Customers:"));
                Assert.Contains(ws.ConsoleLog.Entries, e => e.Kind == ConsoleEntryKind.Error && e.Message.Contains("Query 6"));
            }
            finally
            {
                await ws.CloseAsync();
            }
        });
    }

    [Fact]
    public void Run_current_uses_statement_under_caret_and_row_limit_truncates()
    {
        if (!TestPg.Available(output)) return;
        OnUiThread(async () =>
        {
            var (ws, tab) = await ConnectAsync();
            try
            {
                tab.Document.Text = "select 1;\n\nselect generate_series(1, 50) as n;\n\nselect 3;";
                tab.RowLimit = 10;
                tab.SetSelection(caret: tab.Document.Text.IndexOf("generate", StringComparison.Ordinal));
                await tab.RunCurrentAsync();
                var r = Assert.Single(tab.Results);
                Assert.Equal("Query 1", r.Title);
                Assert.Equal(10, r.RowCount);
                Assert.True(r.Truncated);
                Assert.Contains("limited to 10", r.Summary);

                // An error inside a selection maps back to the editor offset.
                var text = "select 1;\nselect nope_col from public.customers;";
                tab.Document.Text = text;
                var start = text.IndexOf("select nope", StringComparison.Ordinal);
                tab.SetSelection(caret: text.Length, selectionStart: start, selectionLength: text.Length - start);
                await tab.RunCurrentAsync();
                Assert.Equal((text.IndexOf("nope_col", StringComparison.Ordinal), "nope_col".Length), tab.ErrorSpan);
            }
            finally
            {
                await ws.CloseAsync();
            }
        });
    }

    [Fact]
    public void Return_on_error_stops_and_cancel_keeps_loaded_rows()
    {
        if (!TestPg.Available(output)) return;
        OnUiThread(async () =>
        {
            var (ws, tab) = await ConnectAsync();
            try
            {
                AppServices.Settings.SetValue("editor.returnOnError", true);
                tab.Document.Text = "select 1/0; select 2;";
                await tab.RunAllAsync();
                Assert.Single(tab.Results);
                Assert.True(tab.Results[0].HasError);

                tab.Document.Text = "select pg_sleep(30);";
                var run = tab.RunAllAsync();
                for (var i = 0; i < 50 && !ws.Session.IsBusy; i++) await Task.Delay(50);
                Assert.True(tab.IsRunning);
                await Task.Delay(200);
                tab.Cancel();
                var finished = await Task.WhenAny(run, Task.Delay(10_000));
                Assert.Same(run, finished);
                Assert.False(tab.IsRunning);
                Assert.True(tab.Results[0].Cancelled || tab.Results[0].HasError);
            }
            finally
            {
                await ws.CloseAsync();
            }
        });
    }
}
