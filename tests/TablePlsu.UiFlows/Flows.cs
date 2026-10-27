using System.Diagnostics;
using System.Windows.Automation;
using Npgsql;
using static TablePlsu.UiFlows.Ui;

namespace TablePlsu.UiFlows;

public sealed record FlowResult(string Name, bool Ok, string Detail);

public sealed class FlowFailed(string message) : Exception(message);

/// <summary>Every P0 user flow from docs/spec.md, driven through the real UI of the published app.</summary>
public sealed partial class Flows
{
    readonly string _exe;
    readonly NpgsqlConnectionStringBuilder _pg;
    readonly string _root;
    readonly string _scratch = "uiflows_" + Guid.NewGuid().ToString("N")[..8];
    const string Db2 = "tableplsu_test_2";
    public const string ConnName = "uiflows-pg";
    public List<FlowResult> Results { get; } = [];
    Ui? _ui;
    AutomationElement? _main;

    public Flows(string exe, string pg)
    {
        _exe = exe;
        _pg = new NpgsqlConnectionStringBuilder(pg);
        _root = Path.Combine(Path.GetTempPath(), "tableplsu-uiflows-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "roaming"));
        File.WriteAllText(Path.Combine(_root, "roaming", "connections.json"), $$"""
            {
              "version": 1,
              "connections": [
                { "id": "uiflows1", "name": "{{ConnName}}", "color": "#34C759", "tag": "local", "host": "{{_pg.Host}}", "port": {{_pg.Port}},
                  "user": "{{_pg.Username}}", "database": "{{_pg.Database}}", "passwordMode": "none", "sslMode": "disable" }
              ]
            }
            """);
        // Light theme to start with, so the dark switch is measurable on screen.
        File.WriteAllText(SettingsPath, """
            {
              "theme": { "mode": "light" }
            }
            """);
    }

    string SettingsPath => Path.Combine(_root, "roaming", "settings.json");
    Ui U => _ui ?? throw new FlowFailed("app not running");
    AutomationElement Main => _main ?? throw new FlowFailed("no main window");

    // ---------- process ----------

    void Launch()
    {
        var psi = new ProcessStartInfo(_exe) { UseShellExecute = false };
        psi.Environment["TABLEPLSU_ROOT"] = _root;
        var proc = Process.Start(psi)!;
        _ui = new Ui(proc);
        _main = WaitFor(() => U.MainWindows().FirstOrDefault(), 15000)
                ?? throw new FlowFailed($"main window did not appear (exited: {proc.HasExited}; hwnd {Refreshed(proc).MainWindowHandle} '{proc.MainWindowTitle}' responding {proc.Responding}; windows: {string.Join(", ", U.Windows().Select(w => Safe(() => w.Current.AutomationId) + "/" + Name(w)))})");
        Activate(Main);
    }

    static Process Refreshed(Process p) { p.Refresh(); return p; }

    void Kill()
    {
        if (_ui != null && !_ui.Proc.HasExited) { _ui.Proc.Kill(); _ui.Proc.WaitForExit(5000); }
        _ui = null; _main = null;
    }

    public void Cleanup()
    {
        Kill();
        try
        {
            using var c = Open();
            Exec(c, $"drop table if exists public.{_scratch}");
        }
        catch { }
        try
        {
            using var c = Open();
            Exec(c, $"drop database if exists {Db2} with (force)");
        }
        catch { }
        try { Directory.Delete(_root, true); } catch { }
    }

    NpgsqlConnection Open(string? db = null)
    {
        var b = new NpgsqlConnectionStringBuilder(_pg.ConnectionString) { Pooling = false };
        if (db != null) b.Database = db;
        var c = new NpgsqlConnection(b.ConnectionString);
        c.Open();
        return c;
    }

    static void Exec(NpgsqlConnection c, string sql)
    {
        using var cmd = new NpgsqlCommand(sql, c);
        cmd.ExecuteNonQuery();
    }

    static object? Scalar(NpgsqlConnection c, string sql)
    {
        using var cmd = new NpgsqlCommand(sql, c);
        return cmd.ExecuteScalar();
    }

    // ---------- runner ----------

    void Step(string name, Action body)
    {
        if (_only.Count > 0 && !_only.Contains(name)) return;
        var sw = Stopwatch.StartNew();
        try
        {
            body();
            Results.Add(new FlowResult(name, true, $"{sw.ElapsedMilliseconds} ms"));
            Console.WriteLine($"PASS {name} ({sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            var msg = ex is FlowFailed ? ex.Message : ex.GetType().Name + ": " + ex.Message;
            Results.Add(new FlowResult(name, false, msg));
            Console.WriteLine($"FAIL {name}: {msg}");
            try
            {
                var file = Path.Combine(Path.GetTempPath(), "uiflows-fail-" + new string(name.Where(char.IsLetterOrDigit).ToArray()) + ".txt");
                File.WriteAllText(file, string.Join("\n\n", U.Windows().Select(w => Dump(w))));
                Console.WriteLine("  tree: " + file);
            }
            catch { }
            Recover();
        }
    }

    /// <summary>After a failure: close stray dialogs/popups so the next flow starts from the workspace.</summary>
    void Recover()
    {
        if (_ui == null || _ui.Proc.HasExited) return;
        foreach (var w in U.Windows())
        {
            var dialogs = Safe(() => w.Current.AutomationId) == "MainWindow"
                ? Safe(() => w.FindAll(TreeScope.Children, Type(ControlType.Window)).Cast<AutomationElement>().ToList()) ?? []
                : [w];
            foreach (var d in dialogs)
                if (d.TryGetCurrentPattern(WindowPattern.Pattern, out var p)) Safe(() => { ((WindowPattern)p).Close(); return true; });
        }
        Thread.Sleep(200);
        if (_main != null) Activate(Main);
    }

    static void Check(bool cond, string message) { if (!cond) throw new FlowFailed(message); }

    HashSet<string> _only = new(StringComparer.OrdinalIgnoreCase);

    public void ProbeMode(string what)
    {
        Launch();
        ConnectFromList();
        Activate(Main);
        Thread.Sleep(int.Parse(Environment.GetEnvironmentVariable("PROBE_WAIT") ?? "0"));
        U.Keys(what == "db" ? "Ctrl+K" : "Ctrl+P");
        var pal = WaitFor(() => OtherWindow(w => First(w, Id("Input")) != null), 3000)!;
        foreach (var q in new[] { "", "orders", "customers", "sales" })
        {
            U.Keys("Ctrl+A Delete");
            U.Type(q, 20);
            Thread.Sleep(1500);
            Console.WriteLine($"'{q}': status '{Name(First(pal, Id("Status")))}', items: {string.Join(" | ", All(pal, Type(ControlType.ListItem)).Take(5).Select(i => string.Join(" ", All(i, Type(ControlType.Text)).Select(Name))))}");
        }
        U.Keys("Esc Esc");
        Thread.Sleep(300);
        U.Keys("Ctrl+Alt+C");
        Thread.Sleep(800);
        var list = FirstVisible(Main, Id("ConsoleList"));
        if (list != null)
            foreach (var i in All(list, Type(ControlType.ListItem)))
                Console.WriteLine("console: " + string.Join(" ", All(i, Type(ControlType.Text)).Select(Name)).Replace("\n", " ")[..Math.Min(200, string.Join(" ", All(i, Type(ControlType.Text)).Select(Name)).Length)]);
    }

    public void DumpMode()
    {
        Launch();
        ConnectFromList();
        Activate(Main);
        U.Keys("Ctrl+T");
        Thread.Sleep(1500);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "uiflows-dump.txt"), Dump(Main));
        foreach (var w in U.Windows()) Console.WriteLine("window: " + Name(w) + " id=" + Safe(() => w.Current.AutomationId));
        Console.WriteLine("dump: " + Path.Combine(Path.GetTempPath(), "uiflows-dump.txt"));
    }

    // ---------- shared helpers ----------

    AutomationElement ConnectionStatus() => First(Main, Id("ConnectionStatus")) ?? throw new FlowFailed("no ConnectionStatus");

    string Title() { U.Proc.Refresh(); return Name(Main); }

    void WaitConnected(string db, int ms = 15000)
    {
        var ok = WaitTrue(() => Title().Contains("/ " + db) && Name(First(Main, Id("ConnectionStatus"))) == "connected", ms);
        Check(ok, $"workspace did not reach connected on {db} (title '{Title()}', status '{Name(First(Main, Id("ConnectionStatus")))}')");
    }

    void ConnectFromList()
    {
        var item = WaitFor(() => First(Main, And(Type(ControlType.ListItem), Named(ConnName))), 8000)
                   ?? throw new FlowFailed("saved connection not in the welcome list");
        Click(item);
        U.Keys("Enter");
        // Another process on a shared desktop can take the foreground between the click and Enter; retry once.
        if (!WaitTrue(() => Title().Contains("/ " + _pg.Database), 4000))
        {
            Activate(Main);
            Click(item);
            U.Keys("Enter");
        }
        WaitConnected(_pg.Database!);
    }
}
