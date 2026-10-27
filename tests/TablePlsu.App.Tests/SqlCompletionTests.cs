using System.Diagnostics;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using TablePlsu.App.Features.Completion;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Metadata;
using TablePlsu.Core.Settings;
using Xunit.Abstractions;

namespace TablePlsu.App.Tests;

/// <summary>
/// SqlCompletion on a real (off-screen) window. Scenarios share one window per test because showing windows is slow when
/// a UI Automation client (screen reader, Power Automate…) is running on the machine.
/// </summary>
public class SqlCompletionTests(ITestOutputHelper output)
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
        t.IsBackground = true;
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        if (!t.Join(TimeSpan.FromSeconds(120))) throw new TimeoutException("STA test body did not finish in 120 s");
        if (error is not null) throw new Xunit.Sdk.XunitException("STA body failed: " + error);
    }

    /// <summary>Runs queued dispatcher work down to Background priority (layout, render, the completion refresh).</summary>
    private static void DoEvents()
    {
        var frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;
        var op = dispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        // Bounded: a starved Background queue (busy machine) must not hang the test.
        var timer = new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Send, (s, _) => { ((DispatcherTimer)s!).Stop(); frame.Continue = false; }, dispatcher);
        Dispatcher.PushFrame(frame);
        timer.Stop();
        op.Abort();
    }

    private static DbMetadata Md() => new()
    {
        SearchPath = ["public"],
        Schemas =
        [
            new SchemaInfo
            {
                Name = "public", IsLoaded = true,
                Tables =
                [
                    new TableInfo { Schema = "public", Name = "users", Columns = [new ColumnInfo { Name = "id", DataType = "integer" }, new ColumnInfo { Name = "email", DataType = "text" }] },
                    new TableInfo { Schema = "public", Name = "orders", Columns = [new ColumnInfo { Name = "id", DataType = "integer" }, new ColumnInfo { Name = "total", DataType = "numeric" }] },
                ],
            },
            new SchemaInfo { Name = "lazy", IsLoaded = false },
        ],
    };

    private sealed class Harness : IDisposable
    {
        public readonly Window Window;
        public readonly TextEditor Editor;
        public readonly SqlCompletion Completion;
        public EditorSettings Settings = new();
        public DbMetadata Metadata;
        public readonly List<string> Ensured = [];

        public Harness(DbMetadata md)
        {
            Metadata = md;
            Editor = new TextEditor { FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 13 };
            Window = new Window
            {
                Width = 800, Height = 500, Content = Editor, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
            };
            InputMethod.SetIsInputMethodEnabled(Editor.TextArea, false);
            Window.Show();
            Completion = SqlCompletion.Attach(Editor, () => Metadata, () => Settings, s =>
            {
                Ensured.Add(s);
                return Task.CompletedTask;
            });
            DoEvents();
        }

        /// <summary>Back to an empty editor, default settings, no popup.</summary>
        public void Reset()
        {
            Completion.Close();
            Settings = new EditorSettings();
            Editor.Text = "";
            DoEvents();
        }

        public void Type(string text)
        {
            foreach (var c in text)
            {
                Editor.TextArea.PerformTextInput(c.ToString());
                DoEvents();
            }
        }

        public void Key(Key key)
        {
            var source = PresentationSource.FromVisual(Editor.TextArea)!;
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Editor.TextArea.RaiseEvent(args);
            if (!args.Handled)
            {
                args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent };
                Editor.TextArea.RaiseEvent(args);
            }
            DoEvents();
        }

        public void SetText(string withCaret)
        {
            Completion.Close();
            var caret = withCaret.IndexOf('|');
            Editor.Text = withCaret.Remove(caret, 1);
            Editor.TextArea.Caret.Offset = caret;
            DoEvents();
        }

        public string TextWithCaret => Editor.Text.Insert(Editor.TextArea.Caret.Offset, "|");

        public List<string> Shown => Completion.Items.Select(d => d.Item.Display).ToList();

        public string? Selected => Completion.SelectedItem?.Item.Display;

        public void Dispose()
        {
            Completion.Dispose();
            Window.Close();
        }
    }

    [Fact]
    public void Popup_behaviour()
    {
        RunSta(() =>
        {
            using var h = new Harness(Md());
            var sw = Stopwatch.StartNew();
            void Step(string name)
            {
                output.WriteLine($"{name}: {sw.ElapsedMilliseconds} ms");
                h.Reset();
                sw.Restart();
            }

            // Typing an identifier opens the popup, ranked; Enter accepts and closes.
            h.SetText("SELECT * FROM |");
            h.Type("us");
            Assert.True(h.Completion.IsOpen);
            Assert.Equal("users", h.Selected);
            h.Key(Key.Enter);
            Assert.False(h.Completion.IsOpen);
            Assert.Equal("SELECT * FROM users|", h.TextWithCaret);
            Step("type+enter");

            // The list is recomputed while typing; a complete single match closes it.
            h.SetText("SELECT * FROM |");
            h.Type("o");
            Assert.Equal("orders", h.Selected);
            h.Type("rders");
            Assert.False(h.Completion.IsOpen);
            Step("recompute");

            // '.' opens the alias' columns; typing filters; Tab accepts (completeKey both).
            h.SetText("SELECT | FROM users u");
            h.Type("u.");
            Assert.True(h.Completion.IsOpen);
            Assert.Equal(["id", "email"], h.Shown);
            h.Type("em");
            Assert.Equal(["email"], h.Shown);
            h.Key(Key.Tab);
            Assert.Equal("SELECT u.email| FROM users u", h.TextWithCaret);
            Step("dot");

            // A space after FROM opens the tables.
            h.SetText("SELECT * FROM|");
            h.Type(" ");
            Assert.True(h.Completion.IsOpen);
            Assert.Contains("users", h.Shown);
            Step("space after FROM");

            // Esc closes; Esc again opens; selection moves and wraps.
            h.SetText("SELECT * FROM |");
            h.Type("us");
            h.Key(Key.Escape);
            Assert.False(h.Completion.IsOpen);
            h.Key(Key.Escape);
            Assert.True(h.Completion.IsOpen);
            h.SetText("SELECT * FROM |");
            h.Key(Key.Escape);
            var first = h.Selected;
            h.Completion.MoveSelection(1);
            Assert.NotEqual(first, h.Selected);
            h.Completion.MoveSelection(-1);
            Assert.Equal(first, h.Selected);
            h.Completion.MoveSelection(-1);
            Assert.Equal(h.Shown[^1], h.Selected);
            Step("escape + selection");

            // completeKey = tab: Enter closes the popup and is left to the editor.
            h.Settings = h.Settings with { CompleteKey = CompleteKey.Tab };
            h.SetText("SELECT * FROM |");
            h.Type("us");
            h.Key(Key.Enter);
            Assert.False(h.Completion.IsOpen);
            Assert.DoesNotContain("users", h.Editor.Text);
            Step("completeKey tab");

            // completeKey = enter: Tab is left to the editor.
            h.Settings = h.Settings with { CompleteKey = CompleteKey.Enter };
            h.SetText("SELECT * FROM |");
            h.Type("us");
            h.Key(Key.Tab);
            Assert.False(h.Completion.IsOpen);
            Assert.DoesNotContain("users", h.Editor.Text);
            Step("completeKey enter");

            // Disabled: never opens.
            h.Settings = h.Settings with { Autocomplete = new AutocompleteSettings { Disabled = true } };
            h.SetText("SELECT * FROM |");
            h.Type("us");
            Assert.False(h.Completion.IsOpen);
            h.Key(Key.Escape);
            Assert.False(h.Completion.IsOpen);
            Step("disabled");

            // Functions: caret lands inside the parens.
            h.SetText("SELECT | FROM orders");
            h.Type("coale");
            Assert.Equal("coalesce(value, ...)", h.Selected);
            h.Key(Key.Enter);
            Assert.Equal("SELECT coalesce(|) FROM orders", h.TextWithCaret);
            Step("function");

            // Accepting a schema inserts "schema." and asks for the lazy schema.
            h.SetText("SELECT * FROM |");
            h.Type("laz");
            Assert.Equal("lazy", h.Selected);
            h.Key(Key.Enter);
            Assert.Equal("SELECT * FROM lazy.|", h.TextWithCaret);
            Assert.Contains("lazy", h.Ensured);
            Step("schema");

            // addSpace.
            h.Settings = h.Settings with { Autocomplete = new AutocompleteSettings { AddSpace = true } };
            h.SetText("SELECT * FROM |");
            h.Type("us");
            h.Key(Key.Enter);
            Assert.Equal("SELECT * FROM users |", h.TextWithCaret);
            Step("addSpace");

            // Ctrl+N / Ctrl+P overrides apply only while this editor's popup is open.
            Assert.False(CompletionFeature.PopupOwnsKeys(new CommandContext { CommandId = "window.new" }));
            h.SetText("SELECT * FROM |");
            h.Key(Key.Escape);
            Assert.Same(h.Completion, SqlCompletion.Active);
            Assert.True(CompletionFeature.PopupOwnsKeys(new CommandContext { CommandId = "window.new", FocusedElement = h.Editor.TextArea }));
            h.Completion.Close();
            Assert.Null(SqlCompletion.Active);
        });
    }

    private static DbMetadata Many(int n) => new()
    {
        SearchPath = ["public"],
        Schemas = [new SchemaInfo { Name = "public", IsLoaded = true, Tables = [.. Enumerable.Range(0, n).Select(i => new TableInfo { Schema = "public", Name = $"t{i:D2}" })] }],
    };

    private static void Click(System.Windows.Controls.ListBoxItem item, int clickCount)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent };   // the input system's event; UIElement re-raises it as (Preview)MouseLeftButtonDown along the route
        typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(args, clickCount);
        item.RaiseEvent(args);
        DoEvents();
    }

    [Fact]
    public void Popup_lives_in_the_editor_window_navigates_and_closes()
    {
        RunSta(() =>
        {
            using var h = new Harness(Many(40));

            // In the editor's own window (adorner layer): no extra top-level window, and below the caret line.
            h.SetText("SELECT * FROM |");
            h.Type("t");
            var popup = h.Completion.Popup!;
            Assert.Same(h.Window, Window.GetWindow(popup));
            Assert.Single(PresentationSource.CurrentSources.OfType<System.Windows.Interop.HwndSource>(), s => s.Dispatcher == h.Window.Dispatcher && s.RootVisual is Window { IsVisible: true });   // this thread's (tests run in parallel)
            Assert.True(popup.TranslatePoint(default, h.Editor.TextArea).Y >= h.Editor.TextArea.TextView.DefaultLineHeight - 1);
            Assert.False(popup.IsKeyboardFocusWithin);

            // Arrow keys stop at the ends; PageDown / PageUp page; Home / End jump.
            Assert.Equal(0, popup.SelectedIndex);
            h.Key(Key.Up);
            Assert.Equal(0, popup.SelectedIndex);
            h.Key(Key.Down);
            h.Key(Key.Down);
            Assert.Equal(2, popup.SelectedIndex);
            h.Key(Key.PageDown);
            Assert.True(popup.SelectedIndex > 2);
            h.Key(Key.End);
            Assert.Equal(h.Shown.Count - 1, popup.SelectedIndex);
            h.Key(Key.Down);
            Assert.Equal(h.Shown.Count - 1, popup.SelectedIndex);
            h.Key(Key.PageUp);
            Assert.True(popup.SelectedIndex < h.Shown.Count - 1);
            h.Key(Key.Home);
            Assert.Equal(0, popup.SelectedIndex);
            Assert.Equal("SELECT * FROM t|", h.TextWithCaret);   // the editor never saw those keys

            // Rows read as their item to UI Automation (not a type name).
            var peer = UIElementAutomationPeer.CreatePeerForElement(popup.List);
            var names = peer.GetChildren().Select(c => c.GetName()).ToList();
            Assert.NotEmpty(names);
            Assert.All(names, n => Assert.StartsWith("t", n));
            Assert.Contains("t00, table · public", names);
            Assert.Equal("Completions", peer.GetName());

            // Click selects, double-click accepts.
            var containers = Enumerable.Range(0, popup.Items.Count)
                .Select(i => popup.List.ItemContainerGenerator.ContainerFromIndex(i) as System.Windows.Controls.ListBoxItem).Where(c => c is not null).ToList();
            Click(containers[1]!, 1);
            Assert.Equal(1, popup.SelectedIndex);
            Assert.True(h.Completion.IsOpen);
            Click(containers[1]!, 2);
            Assert.False(h.Completion.IsOpen);
            Assert.Equal("SELECT * FROM t01|", h.TextWithCaret);
            Step();

            // Moving the caret out of the word closes.
            h.SetText("SELECT * FROM |");
            h.Type("t0");
            Assert.True(h.Completion.IsOpen);
            h.Editor.TextArea.Caret.Offset = 2;
            DoEvents();
            Assert.False(h.Completion.IsOpen);

            // Backspace over the '.' in front of the word closes.
            h.SetText("SELECT * FROM |");
            h.Type("public.");
            Assert.True(h.Completion.IsOpen);
            h.Editor.Document.Remove(h.Editor.TextArea.Caret.Offset - 1, 1);
            DoEvents();
            Assert.False(h.Completion.IsOpen);

            // Losing keyboard focus closes.
            h.SetText("SELECT * FROM |");
            h.Type("t");
            h.Editor.TextArea.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, h.Editor.TextArea, null) { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
            Assert.False(h.Completion.IsOpen);

            // Scrolling the word out of view closes.
            h.SetText(string.Concat(Enumerable.Repeat("\n", 200)) + "SELECT * FROM |" + string.Concat(Enumerable.Repeat("\n", 200)));
            h.Editor.ScrollToLine(201);
            DoEvents();
            h.Type("t");
            Assert.True(h.Completion.IsOpen);
            h.Editor.ScrollToHome();
            DoEvents();
            Assert.False(h.Completion.IsOpen);

            // Hiding the editor closes.
            h.SetText("SELECT * FROM |");
            h.Type("t");
            h.Editor.Visibility = Visibility.Collapsed;
            DoEvents();
            Assert.False(h.Completion.IsOpen);
            h.Editor.Visibility = Visibility.Visible;
            DoEvents();

            void Step() => h.Reset();
        });
    }

    [Fact]
    public void One_popup_moves_between_editors()
    {
        RunSta(() =>
        {
            using var a = new Harness(Many(5));
            using var b = new Harness(Many(5));
            a.SetText("SELECT * FROM |");
            a.Type("t");
            var popup = a.Completion.Popup!;
            b.SetText("SELECT * FROM |");
            b.Type("t");
            Assert.False(a.Completion.IsOpen);
            Assert.True(b.Completion.IsOpen);
            Assert.Same(popup, b.Completion.Popup);
            Assert.Same(b.Window, Window.GetWindow(popup));
            Assert.Same(b.Completion, SqlCompletion.Active);
            a.Key(Key.Escape);   // opening in A takes it back
            Assert.True(a.Completion.IsOpen);
            Assert.False(b.Completion.IsOpen);
            Assert.Same(a.Window, Window.GetWindow(popup));
        });
    }

    [Fact]
    public void Typing_faster_than_the_dispatcher_keeps_every_character()
    {
        // Keystrokes that arrive faster than the completion refresh (a lagging dispatcher) must all land, in order.
        RunSta(() =>
        {
            using var h = new Harness(Md());
            const string sql = "select 1 as a; select 2 as b; select 3 as c";
            foreach (var chunk in new[] { 1, 2, 3, 5, 8, 100 })
            {
                h.Reset();
                for (var i = 0; i < sql.Length; i++)
                {
                    h.Editor.TextArea.PerformTextInput(sql[i].ToString());
                    if ((i + 1) % chunk == 0) DoEvents();
                }
                DoEvents();
                h.Key(Key.Escape);
                Assert.Equal(sql, h.Editor.Text);
            }

        });
    }

    [Fact]
    public void Auto_close_pairs()
    {
        RunSta(() =>
        {
            using var h = new Harness(Md());
            // Popups off so only the pair logic runs (auto-close works independently of autocomplete).
            h.Settings = new EditorSettings { Autocomplete = new AutocompleteSettings { Disabled = true } };
            h.Type("SELECT count(");
            Assert.Equal("SELECT count(|)", h.TextWithCaret);
            h.Type("*)");
            Assert.Equal("SELECT count(*)|", h.TextWithCaret);
            h.Type(", '");
            Assert.Equal("SELECT count(*), '|'", h.TextWithCaret);
            h.Key(Key.Back);
            Assert.Equal("SELECT count(*), |", h.TextWithCaret);
            h.Type("'it'");
            Assert.Equal("SELECT count(*), 'it'|", h.TextWithCaret);
            h.Type(" don't");
            Assert.Equal("SELECT count(*), 'it' don't|", h.TextWithCaret);
            h.Type(" f(a");
            Assert.Equal("SELECT count(*), 'it' don't f(a|)", h.TextWithCaret);
            h.SetText("x|y");
            h.Type("(");
            Assert.Equal("x(|y", h.TextWithCaret); // no pair before an identifier character
        });
    }

    [Fact]
    public void Auto_close_pairs_off()
    {
        RunSta(() =>
        {
            using var h = new Harness(Md());
            h.Settings = new EditorSettings { Autocomplete = new AutocompleteSettings { Disabled = true, AutoClosePairs = false } };
            h.Type("SELECT count('");
            Assert.Equal("SELECT count('|", h.TextWithCaret);
        });
    }

    // ───────── performance: < 16 ms per keystroke on a 5k-table database ─────────

    private static DbMetadata Big()
    {
        string[] words = ["user", "order", "invoice", "product", "account", "event", "log", "audit", "payment", "shipment"];
        var schemas = new List<SchemaInfo>();
        for (var s = 0; s < 10; s++)
        {
            var name = s == 0 ? "public" : "schema" + s;
            var tables = Enumerable.Range(0, 500).Select(t => new TableInfo
            {
                Schema = name,
                Name = $"{words[t % 10]}_{words[t / 10 % 10]}_{t}",
                Columns = Enumerable.Range(0, 12).Select(c => new ColumnInfo { Name = $"{words[c % 10]}_col{c}", DataType = "text" }).ToList(),
            }).ToList();
            var fns = Enumerable.Range(0, 300).Select(f => new FunctionInfo { Schema = name, Name = $"fn_{words[f % 10]}_{f}", Arguments = "x integer", ReturnType = "integer" }).ToList();
            schemas.Add(new SchemaInfo { Name = name, IsLoaded = true, Tables = tables, Functions = fns });
        }
        return new DbMetadata { SearchPath = ["public"], Schemas = schemas };
    }

    /// <summary>
    /// Per keystroke: text input + completion refresh (engine over 5k tables + popup list update) + layout + the queued
    /// work after it. The 16 ms budget is asserted on UI-thread CPU time (QueryThreadCycleTime), which other processes
    /// competing for the machine barely move; wall time is printed. Skipped as an assertion when a UI Automation client
    /// is listening (WPF then does cross-process automation work for every visual change).
    /// </summary>
    [Fact]
    public void Keystroke_with_five_thousand_tables_is_under_a_frame()
    {
        RunSta(() =>
        {
            using var h = new Harness(Big());
            var cpm = Perf.CompletionPerfTests.CyclesPerMs();
            var prefixSql = string.Concat(Enumerable.Repeat("SELECT a.user_col0 FROM user_user_0 a WHERE a.log_col6 = 'x';\n", 100));
            h.SetText(prefixSql + "SELECT * FROM |");
            h.Type("us"); // warm-up (JIT, first popup)
            h.SetText(prefixSql + "|");

            var wall = new List<double>();
            var cpu = new List<double>();
            var refresh = new List<double>();
            var trace = new System.Text.StringBuilder();
            foreach (var c in "SELECT a.user_col0, b.ord FROM user_user_0 a JOIN order_user_1 b ON b.pay")
            {
                var c0 = Perf.CompletionPerfTests.Cycles();
                var sw = Stopwatch.StartNew();
                h.Editor.TextArea.PerformTextInput(c.ToString());
                h.Completion.FlushPending();
                h.Editor.UpdateLayout();
                DoEvents();
                sw.Stop();
                cpu.Add((Perf.CompletionPerfTests.Cycles() - c0) / cpm);
                wall.Add(sw.Elapsed.TotalMilliseconds);
                refresh.Add(h.Completion.LastRefreshMs);
                trace.Append(c).Append(h.Completion.IsOpen ? "+" : "-");
            }
            output.WriteLine(trace.ToString());
            Assert.True(h.Completion.IsOpen);
            Assert.Equal("payment_col8", h.Selected);
            static double Median(List<double> xs) { xs.Sort(); return xs[xs.Count / 2]; }
            var automation = AutomationPeer.ListenerExists(AutomationEvents.StructureChanged) || AutomationPeer.ListenerExists(AutomationEvents.PropertyChanged);
            var cpuMedian = Median(cpu);
            output.WriteLine($"keystroke (input + completion + layout): UI-thread CPU median {cpuMedian:F2} ms, max {cpu[^1]:F2} ms; wall median {Median(wall):F2} ms; " +
                             $"refresh median {Median(refresh):F2} ms; UIA listeners: {automation}");
            if (automation)
                output.WriteLine("A UI Automation client is listening: timing not asserted.");
            else
                Assert.True(cpuMedian < 16, $"median keystroke UI-thread CPU {cpuMedian:F2} ms");
        });
    }
}
