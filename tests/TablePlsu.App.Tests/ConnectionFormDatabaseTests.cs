using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TablePlsu.App.Features.Welcome;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Connections;
using TablePlsu.Core.Themes;
using Xunit.Abstractions;

namespace TablePlsu.App.Tests;

/// <summary>
/// The connection form's Database dropdown and the "your data may be in X" hint. The test server's <c>postgres</c>
/// database is empty, which is exactly the case this feature is for.
/// </summary>
[Collection(AppServicesCollection.Name)]
public class ConnectionFormDatabaseTests(ITestOutputHelper output)
{
    /// <summary>A form filled from TABLEPLSU_TEST_PG, with the given Database field.</summary>
    private static ConnectionFormViewModel TestServerForm(string database)
    {
        TestApp.Init();
        var (profile, secrets) = TestPg.Profile();
        var vm = new ConnectionFormViewModel(null, allowConnect: false)
        {
            Name = "form",
            Host = profile.Host,
            Port = profile.Port.ToString(),
            User = profile.User,
            PasswordMode = profile.PasswordMode,
            Password = secrets.Password ?? "",
            SslMode = SslModeOption.Disable,
            Database = database,
        };
        return vm;
    }

    private static ConnectionFormViewModel FakeForm()
    {
        TestApp.Init();
        return new ConnectionFormViewModel(null, allowConnect: false)
        {
            Host = "db.example",
            Port = "5432",
            User = "alice",
            PasswordMode = PasswordMode.Store,
            Password = "pw",
        };
    }

    private static List<string> Names(ConnectionFormViewModel vm) => vm.DatabaseItems.Where(i => i.IsDatabase).Select(i => i.Text).ToList();

    [Fact]
    public async Task Successful_test_on_postgres_fills_the_list_and_hints_the_user_database()
    {
        if (!TestPg.Available(output)) return;
        var vm = TestServerForm("postgres");
        await vm.TestCommand.ExecuteAsync(null);
        output.WriteLine(vm.StatusMessage);
        Assert.True(vm.StatusOk);

        var names = Names(vm);
        output.WriteLine(string.Join(", ", names));
        Assert.Contains("postgres", names);
        Assert.Contains("tableplsu_test", names);
        Assert.DoesNotContain("template0", names);
        Assert.DoesNotContain("template1", names);

        // The hint follows the rule over the server's real list.
        var expected = DatabaseSuggestion.Suggest("postgres", names);
        Assert.Equal(expected, vm.SuggestedDatabase);
        if (expected is null) return;
        Assert.Equal($"Your data may be in “{expected}” — use it", vm.DatabaseHint);
        vm.UseSuggestedDatabaseCommand.Execute(null);
        Assert.Equal(expected, vm.Database);
        Assert.Null(vm.SuggestedDatabase);
        Assert.Null(vm.DatabaseHint);

        // Back to empty (= server default): the hint returns; any other name hides it.
        vm.Database = "";
        Assert.Equal(expected, vm.SuggestedDatabase);
        vm.Database = "something_else";
        Assert.Null(vm.DatabaseHint);
    }

    [Fact]
    public async Task Test_on_the_user_database_shows_no_hint()
    {
        if (!TestPg.Available(output)) return;
        var vm = TestServerForm("tableplsu_test");
        await vm.TestCommand.ExecuteAsync(null);
        Assert.True(vm.StatusOk);
        Assert.Contains("tableplsu_test", Names(vm));
        Assert.Null(vm.SuggestedDatabase);
    }

    [Fact]
    public async Task Opening_the_dropdown_lists_the_server_databases()
    {
        if (!TestPg.Available(output)) return;
        var vm = TestServerForm("");
        var task = vm.OpenDatabaseList();
        Assert.Equal(DatabaseItemKind.Loading, Assert.Single(vm.DatabaseItems).Kind);
        await task;
        Assert.Contains("tableplsu_test", Names(vm));
        Assert.All(vm.DatabaseItems, i => Assert.True(i.IsDatabase));
        Assert.Null(vm.StatusMessage); // never touches the status line
        Assert.Null(vm.SuggestedDatabase); // the hint is a Test result

        // Loaded once per server: opening again does not refetch.
        var again = vm.OpenDatabaseList();
        Assert.True(again.IsCompleted);
        Assert.Contains("tableplsu_test", Names(vm));
    }

    [Fact]
    public async Task Dropdown_lists_databases_while_the_typed_name_does_not_exist()
    {
        if (!TestPg.Available(output)) return;
        var vm = TestServerForm("tableplsu_no_such_db_" + Guid.NewGuid().ToString("N")[..8]);
        await vm.OpenDatabaseList();
        Assert.Contains("tableplsu_test", Names(vm));

        // Test fails on the missing database, and still fills the dropdown to pick from.
        var vm2 = TestServerForm("tableplsu_no_such_db_" + Guid.NewGuid().ToString("N")[..8]);
        await vm2.TestCommand.ExecuteAsync(null);
        Assert.False(vm2.StatusOk);
        await vm2.DatabaseListTask;
        Assert.Contains("tableplsu_test", Names(vm2));
    }

    [Fact]
    public async Task Connection_error_becomes_a_disabled_item()
    {
        TestApp.Init();
        var vm = new ConnectionFormViewModel(null, allowConnect: false)
        {
            Host = "127.0.0.1",
            Port = "1",
            User = "postgres",
            PasswordMode = PasswordMode.None,
            SslMode = SslModeOption.Disable,
        };
        await vm.OpenDatabaseList();
        var item = Assert.Single(vm.DatabaseItems);
        output.WriteLine(item.Text);
        Assert.Equal(DatabaseItemKind.Message, item.Kind);
        Assert.False(item.IsDatabase);
        Assert.StartsWith("Could not list databases: ", item.Text);
        Assert.Null(vm.StatusMessage);

        // Not cached: the next open tries again.
        var retry = vm.OpenDatabaseList();
        Assert.Equal(DatabaseItemKind.Loading, Assert.Single(vm.DatabaseItems).Kind);
        await retry;
    }

    [Fact]
    public async Task Lister_failure_and_timeout_are_disabled_items()
    {
        var vm = FakeForm();
        vm.DatabaseLister = (_, _, _) => Task.FromException<IReadOnlyList<string>>(new InvalidOperationException("permission denied"));
        await vm.OpenDatabaseList();
        Assert.Equal(new DatabaseItem("Could not list databases: permission denied", DatabaseItemKind.Message), Assert.Single(vm.DatabaseItems));

        // A server that never answers: the 5 s budget (shortened here) ends in a message row, not a hang.
        vm.DatabaseListTimeout = TimeSpan.FromMilliseconds(100);
        vm.DatabaseLister = (_, _, _) => new TaskCompletionSource<IReadOnlyList<string>>().Task; // ignores the token
        await vm.OpenDatabaseList().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new DatabaseItem("Timed out listing databases.", DatabaseItemKind.Message), Assert.Single(vm.DatabaseItems));
    }

    [Theory]
    [InlineData(nameof(ConnectionFormViewModel.Host))]
    [InlineData(nameof(ConnectionFormViewModel.Port))]
    [InlineData(nameof(ConnectionFormViewModel.User))]
    [InlineData(nameof(ConnectionFormViewModel.Password))]
    public async Task Editing_a_server_field_cancels_the_fetch_in_flight(string field)
    {
        var vm = FakeForm();
        var started = new TaskCompletionSource<CancellationToken>();
        var calls = 0;
        vm.DatabaseLister = async (profile, secrets, ct) =>
        {
            calls++;
            started.TrySetResult(ct);
            await Task.Delay(Timeout.Infinite, ct);
            return ["never"];
        };
        var fetch = vm.OpenDatabaseList();
        var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DatabaseItemKind.Loading, Assert.Single(vm.DatabaseItems).Kind);
        Assert.Same(fetch, vm.OpenDatabaseList()); // opening again while loading joins the same fetch

        switch (field)
        {
            case nameof(vm.Host): vm.Host = "db2.example"; break;
            case nameof(vm.Port): vm.Port = "5433"; break;
            case nameof(vm.User): vm.User = "bob"; break;
            case nameof(vm.Password): vm.Password = "pw2"; break;
        }

        Assert.True(token.IsCancellationRequested);
        Assert.Empty(vm.DatabaseItems);
        await fetch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(vm.DatabaseItems); // no "Could not list"/"Timed out" row from the cancelled fetch
        Assert.Equal(1, calls);

        // The next open fetches for the new server.
        vm.DatabaseLister = (profile, _, _) => Task.FromResult<IReadOnlyList<string>>([profile.Host, "shop"]);
        await vm.OpenDatabaseList();
        Assert.Equal(new[] { "shop", vm.Host }.Order(), Names(vm).Order());
    }

    [Fact]
    public async Task Editing_a_server_field_drops_a_loaded_list()
    {
        var vm = FakeForm();
        vm.DatabaseLister = (_, _, _) => Task.FromResult<IReadOnlyList<string>>(["postgres", "shop"]);
        await vm.OpenDatabaseList();
        Assert.Equal(["postgres", "shop"], Names(vm));
        vm.Database = "shop"; // the Database field itself is not a server field
        Assert.Equal(2, vm.DatabaseItems.Count);
        vm.Host = "elsewhere";
        Assert.Empty(vm.DatabaseItems);
    }

    [Fact]
    public async Task Without_credentials_the_dropdown_explains_instead_of_prompting()
    {
        var vm = FakeForm();
        var called = false;
        vm.DatabaseLister = (_, _, _) => { called = true; return Task.FromResult<IReadOnlyList<string>>([]); };

        vm.Password = "";
        await vm.OpenDatabaseList();
        Assert.False(called);
        Assert.Equal(DatabaseItemKind.Message, Assert.Single(vm.DatabaseItems).Kind);

        vm.Password = "pw";
        vm.PasswordMode = PasswordMode.Ask; // would show a dialog: not on dropdown open
        await vm.OpenDatabaseList();
        Assert.False(called);

        vm.PasswordMode = PasswordMode.None;
        await vm.OpenDatabaseList();
        Assert.True(called);
        Assert.Equal(DatabaseItemKind.Message, Assert.Single(vm.DatabaseItems).Kind); // "No databases found"
    }

    // ---- the view ------------------------------------------------------------------------------------------------

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

    /// <summary>Pumps the dispatcher until the task is done (its continuations run on this thread, as in the app).</summary>
    private static void Pump(Task task)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < until) DoEvents();
        task.GetAwaiter().GetResult();
        DoEvents();
    }

    [Fact]
    public void Database_combo_keeps_free_text_and_survives_list_changes()
    {
        var vm = FakeForm();
        vm.DatabaseLister = (_, _, _) => Task.FromResult<IReadOnlyList<string>>(["postgres", "shop"]);
        RunSta(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var window = new Window
            {
                Width = 700, Height = 900, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Content = new ContentControl(),
            };
            window.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/TablePlsu.App;component/Shell/Styles.xaml", UriKind.Relative)));
            ThemeApplier.ApplyTo(window.Resources, ThemeService.BuiltInDark);
            window.Show();
            // ConnectionFormView itself needs Application.Resources while it parses (StaticResource), which tests do not
            // have; this is its DatabaseBox with the same settings and bindings, styled by the app's Styles.xaml.
            var box = new ComboBox
            {
                IsEditable = true, IsTextSearchEnabled = false, DataContext = vm,
                ItemContainerStyle = new Style(typeof(ComboBoxItem))
                {
                    Setters = { new Setter(UIElement.IsEnabledProperty, new System.Windows.Data.Binding(nameof(DatabaseItem.IsDatabase))) },
                },
            };
            box.SetBinding(ItemsControl.ItemsSourceProperty, nameof(vm.DatabaseItems));
            box.SetBinding(ComboBox.TextProperty, new System.Windows.Data.Binding(nameof(vm.Database))
            {
                UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged,
            });
            ConnectionFormView.AttachDatabaseBox(box, vm);
            ((ContentControl)window.Content).Content = new StackPanel { Children = { box } };
            DoEvents();
            try
            {
                Assert.True(box.IsEditable);

                // Free text, including a prefix of a listed name, reaches the VM as typed (no autocomplete).
                box.Text = "sh";
                DoEvents();
                Assert.Equal("sh", vm.Database);

                // Opening the dropdown fetches; the rows render, and picking one sets the field.
                box.IsDropDownOpen = true;
                Pump(vm.DatabaseListTask);
                Assert.Equal(2, box.Items.Count);
                box.SelectedIndex = 1;
                box.IsDropDownOpen = false;
                DoEvents();
                Assert.Equal("shop", vm.Database);

                // Editing the host clears the list but never the chosen database.
                vm.Host = "elsewhere";
                DoEvents();
                Assert.Empty(box.Items);
                Assert.Equal("shop", vm.Database);
                Assert.Equal("shop", box.Text);

                // Pick, then type over it (the old item stays selected), then the list changes: the typed text stays.
                vm.DatabaseLister = (_, _, _) => Task.FromResult<IReadOnlyList<string>>(["postgres", "shop"]);
                box.IsDropDownOpen = true;
                Pump(vm.DatabaseListTask);
                box.SelectedIndex = 0;
                box.IsDropDownOpen = false;
                DoEvents();
                Assert.Equal("postgres", vm.Database);
                box.Text = "shop";
                DoEvents();
                vm.Port = "5433";
                DoEvents();
                Assert.Empty(box.Items);
                Assert.Equal("shop", vm.Database);
                Assert.Equal("shop", box.Text);

                // Disabled rows cannot be picked.
                vm.DatabaseLister = (_, _, _) => Task.FromException<IReadOnlyList<string>>(new InvalidOperationException("nope"));
                box.IsDropDownOpen = true;
                Pump(vm.DatabaseListTask);
                Assert.Single(box.Items);
                var row = (ComboBoxItem)box.ItemContainerGenerator.ContainerFromIndex(0);
                Assert.True(row is null || !row.IsEnabled);
                Assert.Equal("shop", vm.Database);
                Assert.Equal("shop", box.Text);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
