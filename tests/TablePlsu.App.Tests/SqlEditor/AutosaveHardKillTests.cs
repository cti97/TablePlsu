using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit.Abstractions;

namespace TablePlsu.App.Tests.SqlEditor;

/// <summary>
/// End-to-end "never lose a query": the real app (TablePlsu.App.exe next to the tests) on a throwaway data root with an
/// unreachable connection. TablePlsu.UiDriver.exe types into the restored SQL editor through UI Automation and kills the
/// app with <see cref="Process.Kill()"/> after the autosave debounce; the text must be on disk and come back on relaunch.
/// UI Automation runs in the driver process, never in this test host. Needs an interactive desktop; skipped otherwise.
/// </summary>
public class AutosaveHardKillTests(ITestOutputHelper output)
{
    private const string ConnId = "kill1";
    private const string TabA = "hardkilltaba";
    private const string TabB = "hardkilltabb";

    [Fact]
    public void Text_typed_while_disconnected_survives_a_hard_kill_and_restores_on_relaunch()
    {
        if (!Environment.UserInteractive)
        {
            output.WriteLine("SKIPPED: no interactive desktop.");
            return;
        }
        var exe = Path.Combine(AppContext.BaseDirectory, "TablePlsu.App.exe");
        Assert.True(File.Exists(exe), exe);

        var root = Path.Combine(Path.GetTempPath(), "tableplsu-hardkill", Guid.NewGuid().ToString("N"));
        var editors = Path.Combine(root, "local", "editors", ConnId);
        Directory.CreateDirectory(Path.Combine(root, "roaming"));
        Directory.CreateDirectory(editors);
        // Port 1 on localhost: the connection fails; the editor must still restore and autosave.
        File.WriteAllText(Path.Combine(root, "roaming", "connections.json"), $$"""
            { "version": 1, "connections": [
              { "id": "{{ConnId}}", "name": "unreachable", "host": "localhost", "port": 1, "user": "postgres",
                "database": "db", "passwordMode": "none", "sslMode": "disable" } ] }
            """);
        File.WriteAllText(Path.Combine(root, "local", "open-workspaces.txt"), $"{ConnId}\tdb");
        File.WriteAllText(Path.Combine(editors, TabA + ".sql"), "select 'seed a';");
        File.WriteAllText(Path.Combine(editors, TabB + ".sql"), "select 'seed b';");
        File.WriteAllText(Path.Combine(editors, "session.json"), $$"""
            { "tabs": [ { "id": "{{TabA}}", "title": "Query 1" }, { "id": "{{TabB}}", "title": "Query 2", "caretOffset": 3 } ],
              "activeTabId": "{{TabB}}" }
            """);

        const string typed = "select 'typed while the database is unreachable, then killed';";
        try
        {
            // > 500 ms debounce + write; spec: a crash loses at most the last 500 ms of typing.
            var first = Drive("type", exe, root, Convert.ToBase64String(Encoding.UTF8.GetBytes(typed)), "900");
            Assert.Equal("select 'seed b';", first); // the saved active tab (B) is the one shown

            Assert.Equal(typed, File.ReadAllText(Path.Combine(editors, TabB + ".sql")));
            Assert.Equal("select 'seed a';", File.ReadAllText(Path.Combine(editors, TabA + ".sql")));
            Assert.DoesNotContain(Directory.GetFiles(editors), f => new FileInfo(f).Length == 0);

            Assert.Equal(typed, Drive("read", exe, root));
            var session = File.ReadAllText(Path.Combine(editors, "session.json"));
            Assert.Contains(TabA, session);
            Assert.Contains(TabB, session);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (Exception) { /* best effort */ }
        }
    }

    /// <summary>Runs TablePlsu.UiDriver and returns the editor text it saw.</summary>
    private string Drive(params string[] args)
    {
        var driver = Path.Combine(AppContext.BaseDirectory, "TablePlsu.UiDriver.exe");
        var psi = new ProcessStartInfo(driver) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        var stdout = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(180_000))
        {
            p.Kill(entireProcessTree: true);
            throw new TimeoutException("UI driver timed out. " + stdout);
        }
        output.WriteLine(stdout + stderr.Result);
        Assert.True(p.ExitCode == 0, stdout + stderr.Result);
        var value = stdout.Split('\n').Select(l => l.Trim()).First(l => l.StartsWith("VALUE ", StringComparison.Ordinal))[6..];
        return Encoding.UTF8.GetString(Convert.FromBase64String(value));
    }
}
