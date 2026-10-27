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
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException("STA body failed: " + error);
    }

    /// <summary>Runs queued dispatcher work down to Background priority (layout, render, the completion refresh).</summary>
    private static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
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

        public List<string> Shown => Completion.Window?.CompletionList.CompletionData.Select(d => ((SqlCompletionData)d).Item.Display).ToList() ?? [];

        public string? Selected => (Completion.Window?.CompletionList.SelectedItem as SqlCompletionData)?.Item.Display;

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
    /// Per keystroke: text input + completion refresh (engine over 5k tables + popup list update) + layout. The 16 ms
    /// budget is asserted only when no UI Automation client is listening: with one (screen reader, Power Automate…),
    /// WPF raises cross-process automation events for every visual change and timings are not representative.
    /// </summary>
    [Fact]
    public void Keystroke_with_five_thousand_tables_is_under_a_frame()
    {
        RunSta(() =>
        {
            using var h = new Harness(Big());
            var prefixSql = string.Concat(Enumerable.Repeat("SELECT a.user_col0 FROM user_user_0 a WHERE a.log_col6 = 'x';\n", 100));
            h.SetText(prefixSql + "SELECT * FROM |");
            h.Type("us"); // warm-up (JIT, first popup)
            h.SetText(prefixSql + "|");

            var times = new List<double>();
            var refresh = new List<double>();
            var trace = new System.Text.StringBuilder();
            foreach (var c in "SELECT a.user_col0, b.ord FROM user_user_0 a JOIN order_user_1 b ON b.pay")
            {
                var sw = Stopwatch.StartNew();
                h.Editor.TextArea.PerformTextInput(c.ToString());
                h.Completion.FlushPending();
                h.Editor.UpdateLayout();
                h.Completion.Window?.UpdateLayout();
                sw.Stop();
                times.Add(sw.Elapsed.TotalMilliseconds);
                refresh.Add(h.Completion.LastRefreshMs);
                DoEvents();
                trace.Append(c).Append(h.Completion.IsOpen ? "+" : "-");
            }
            output.WriteLine(trace.ToString());
            Assert.True(h.Completion.IsOpen);
            Assert.Equal("payment_col8", h.Selected);
            times.Sort();
            refresh.Sort();
            var median = times[times.Count / 2];
            var automation = AutomationPeer.ListenerExists(AutomationEvents.StructureChanged) || AutomationPeer.ListenerExists(AutomationEvents.PropertyChanged);
            output.WriteLine($"keystroke (input + completion + layout): median {median:F2} ms, p90 {times[(int)(times.Count * 0.9)]:F2} ms, max {times[^1]:F2} ms; refresh median {refresh[refresh.Count / 2]:F2} ms; UIA listeners: {automation}");
            if (automation)
                output.WriteLine("A UI Automation client is listening: timing not asserted.");
            else
                Assert.True(median < 16, $"median keystroke {median:F2} ms");
        });
    }
}
