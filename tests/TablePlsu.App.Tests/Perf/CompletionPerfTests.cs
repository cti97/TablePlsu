using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using TablePlsu.App.Features.Completion;
using TablePlsu.Core.Completion;
using TablePlsu.Core.Metadata;
using TablePlsu.Core.Settings;
using Xunit.Abstractions;

namespace TablePlsu.App.Tests.Perf;

/// <summary>
/// Autocomplete hot path on a 5000-table database, measured with load-proof proxies next to wall time: UI-thread CPU
/// (QueryThreadCycleTime), bytes allocated on the UI thread, dispatcher operations posted and layout passes per keystroke,
/// plus the first popup open on a fresh editor. Prints <c>PERF name=value</c> lines (scripts/perf-completion.ps1 parses
/// them). Uses only SqlCompletion's long-standing public API, so the same file measures older builds too.
/// Bounded: the UI thread has a hard timeout and nothing depends on window activation or focus.
/// </summary>
[Trait("Category", "Perf")]
public class CompletionPerfTests(ITestOutputHelper output)
{
    [DllImport("kernel32.dll")] private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();

    internal static ulong Cycles() { QueryThreadCycleTime(GetCurrentThread(), out var c); return c; }

    /// <summary>Thread cycles per millisecond of running time (best of a few short spins: preemption only lowers it).</summary>
    internal static double CyclesPerMs()
    {
        var best = 0.0;
        for (var i = 0; i < 5; i++)
        {
            var c0 = Cycles();
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalMilliseconds < 20) { }
            best = Math.Max(best, (Cycles() - c0) / sw.Elapsed.TotalMilliseconds);
        }
        return best;
    }

    private static void RunSta(Action body, int timeoutSeconds = 180)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ex; }
            finally { try { Dispatcher.CurrentDispatcher.InvokeShutdown(); } catch (NullReferenceException) { } }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        if (!t.Join(TimeSpan.FromSeconds(timeoutSeconds))) throw new TimeoutException($"completion perf body did not finish in {timeoutSeconds} s");
        if (error is not null) throw new Xunit.Sdk.XunitException("STA body failed: " + error);
    }

    /// <summary>Runs queued dispatcher work down to Background priority (layout, render, the completion refresh), bounded.</summary>
    private static void DoEvents()
    {
        var frame = new DispatcherFrame();
        var op = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        var timer = new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Send, (s, _) => { ((DispatcherTimer)s!).Stop(); frame.Continue = false; }, Dispatcher.CurrentDispatcher);
        Dispatcher.PushFrame(frame);
        timer.Stop();
        op.Abort();
    }

    /// <summary>Lets queued idle-time work run (ApplicationIdle), bounded.</summary>
    private static void Idle()
    {
        var frame = new DispatcherFrame();
        var op = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        var timer = new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Send, (s, _) => { ((DispatcherTimer)s!).Stop(); frame.Continue = false; }, Dispatcher.CurrentDispatcher);
        Dispatcher.PushFrame(frame);
        timer.Stop();
        op.Abort();
    }

    internal static DbMetadata Big()
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

    private sealed class Probe : IDisposable
    {
        private readonly Dispatcher _d = Dispatcher.CurrentDispatcher;
        private readonly UIElement _anchor;
        public int Posted, Layouts;

        public Probe(UIElement anchor)
        {
            _anchor = anchor;
            _d.Hooks.OperationPosted += OnPosted;
            anchor.LayoutUpdated += OnLayout;
        }

        private void OnPosted(object? s, DispatcherHookEventArgs e) => Posted++;
        private void OnLayout(object? s, EventArgs e) => Layouts++;

        public void Dispose()
        {
            _d.Hooks.OperationPosted -= OnPosted;
            _anchor.LayoutUpdated -= OnLayout;
        }
    }

    private readonly record struct Sample(double WallMs, double CpuMs, long Bytes, int Posted, int Layouts);

    private static double Median(IEnumerable<double> xs) { var l = xs.OrderBy(x => x).ToList(); return l[l.Count / 2]; }

    private sealed class Host : IDisposable
    {
        public readonly Window Window;
        public readonly TextEditor Editor;
        public readonly SqlCompletion Completion;

        public Host(DbMetadata md)
        {
            Editor = new TextEditor { FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 13 };
            Window = new Window
            {
                Width = 800, Height = 500, Content = Editor, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
            };
            InputMethod.SetIsInputMethodEnabled(Editor.TextArea, false);
            Window.Show();
            Completion = SqlCompletion.Attach(Editor, () => md, () => new EditorSettings());
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

        public void Dispose()
        {
            Completion.Dispose();
            Window.Close();
        }
    }

    /// <summary>One keystroke, end to end on the UI thread: input, the completion refresh, layout and the queued work after it.</summary>
    private static Sample Keystroke(Host h, char c, double cyclesPerMs)
    {
        using var probe = new Probe(h.Editor);
        var b0 = GC.GetAllocatedBytesForCurrentThread();
        var c0 = Cycles();
        var sw = Stopwatch.StartNew();
        h.Editor.TextArea.PerformTextInput(c.ToString());
        h.Completion.FlushPending();
        h.Editor.UpdateLayout();
        DoEvents();
        sw.Stop();
        var cpu = (Cycles() - c0) / cyclesPerMs;
        return new Sample(sw.Elapsed.TotalMilliseconds, cpu, GC.GetAllocatedBytesForCurrentThread() - b0, probe.Posted, probe.Layouts);
    }

    private void Report(string name, IReadOnlyList<Sample> s)
    {
        output.WriteLine($"PERF {name}.wallMs={Median(s.Select(x => x.WallMs)):F2} {name}.cpuMs={Median(s.Select(x => x.CpuMs)):F2} " +
                         $"{name}.allocKB={Median(s.Select(x => x.Bytes / 1024.0)):F1} {name}.posted={Median(s.Select(x => (double)x.Posted)):F0} " +
                         $"{name}.layouts={Median(s.Select(x => (double)x.Layouts)):F0} {name}.cpuMaxMs={s.Max(x => x.CpuMs):F2}");
    }

    [Fact]
    public void Autocomplete_five_thousand_tables()
    {
        RunSta(() =>
        {
            var md = Big();
            var cpm = CyclesPerMs();
            var prefixSql = string.Concat(Enumerable.Repeat("SELECT a.user_col0 FROM user_user_0 a WHERE a.log_col6 = 'x';\n", 100));
            // In the app the catalog is loaded (and its lookups built) long before the first keystroke: do that here
            // too, and settle the heap after building 60k metadata objects, so the first-open numbers are about the popup.
            CompletionEngine.Complete(prefixSql + "SELECT * FROM x", prefixSql.Length + 15, md, CompletionOptions.From(new AutocompleteSettings(), 100));
            GC.Collect();
            GC.WaitForPendingFinalizers();

            // Popup open: the very first in this process (JIT, popup creation), the first on each new editor, and a
            // reopen on an editor that already showed it.
            var firstOnEditor = new List<Sample>();
            var opens = new List<Sample>();
            for (var round = 0; round < 6; round++)
            {
                using var fresh = new Host(md);
                // Builds with an idle-time engine warm-up: let it finish, as it would before anyone types (bounded).
                for (var i = 0; i < 4; i++) Idle();
                (typeof(SqlCompletion).GetProperty("EngineWarmUp")?.GetValue(null) as Task)?.Wait(TimeSpan.FromSeconds(10));
                if (round == 0)
                {
                    // The process's first keystroke pays AvalonEdit's own cold start; keep that out of the popup number.
                    fresh.SetText(prefixSql + "SELECT 1|");
                    Report("firstKeyCold", [Keystroke(fresh, '2', cpm)]);
                }
                fresh.SetText(prefixSql + "SELECT * FROM |");
                var first = Keystroke(fresh, 'u', cpm);
                Assert.True(fresh.Completion.IsOpen, "popup did not open");
                if (round == 0)
                {
                    Report("firstOpenCold", [first]);
                    output.WriteLine($"PERF firstOpenCold.refreshMs={fresh.Completion.LastRefreshMs:F2}");   // engine + list update, no layout/render
                }
                else firstOnEditor.Add(first);
                Keystroke(fresh, 's', cpm);
                fresh.Completion.Close();
                fresh.SetText(prefixSql + "SELECT * FROM u|");
                opens.Add(Keystroke(fresh, 's', cpm));   // popup closed → opens on this keystroke
                Assert.True(fresh.Completion.IsOpen, "popup did not reopen");
            }
            Report("firstOpenEditor", firstOnEditor);

            // Reference: the first keystroke on a new editor that opens no popup (editor-only cost of that keystroke).
            var noPopup = new List<Sample>();
            for (var round = 0; round < 5; round++)
            {
                using var fresh = new Host(md);
                fresh.SetText(prefixSql + "SELECT 1|");
                noPopup.Add(Keystroke(fresh, '2', cpm));
                Assert.False(fresh.Completion.IsOpen);
            }
            Report("firstKeyNoPopup", noPopup);
            Report("reopen", opens);

            using var h = new Host(md);
            h.SetText(prefixSql + "SELECT * FROM |");
            foreach (var c in "us") Keystroke(h, c, cpm);   // warm-up
            var keys = new List<Sample>();
            for (var rep = 0; rep < 3; rep++)
            {
                h.SetText(prefixSql + "|");
                foreach (var c in "SELECT a.user_col0, b.ord FROM user_user_0 a JOIN order_user_1 b ON b.pay")
                {
                    var wasOpen = h.Completion.IsOpen;
                    var s = Keystroke(h, c, cpm);
                    if (wasOpen && h.Completion.IsOpen) keys.Add(s);   // popup open before and after: an update
                }
                Assert.True(h.Completion.IsOpen);
                Assert.Equal("payment_col8", h.Completion.LastResult!.Items[0].Display);
            }
            Report("keystroke", keys);
            output.WriteLine($"PERF refresh.ms={h.Completion.LastRefreshMs:F2} cyclesPerMs={cpm:F0}");

            // The engine alone (what would move off the UI thread): the slowest context, columns + 3000 functions.
            var text = prefixSql + "SELECT a.user_col0, b.ord FROM user_user_0 a JOIN order_user_1 b ON b.pay = p";
            var options = CompletionOptions.From(new AutocompleteSettings(), 100);
            var engine = new List<Sample>();
            for (var i = 0; i < 25; i++)
            {
                var b0 = GC.GetAllocatedBytesForCurrentThread();
                var c0 = Cycles();
                var sw = Stopwatch.StartNew();
                CompletionEngine.Complete(text, text.Length, md, options);
                engine.Add(new Sample(sw.Elapsed.TotalMilliseconds, (Cycles() - c0) / cpm, GC.GetAllocatedBytesForCurrentThread() - b0, 0, 0));
            }
            Report("engine", engine);
        });
    }
}
