using System.Diagnostics;
using System.Text;
using System.Windows.Automation;

// Drives the real TablePlsu.App.exe through UI Automation for end-to-end tests (kept out of the test host on purpose).
//   type <appExe> <dataRoot> <text> <killAfterMs>  launch, wait for the SQL editor, print its text, set <text>,
//                                                 wait <killAfterMs>, Process.Kill
//   read <appExe> <dataRoot>                      launch, wait for the SQL editor, print its text, Process.Kill
// Output: "VALUE <base64 utf-8>" lines, then "OK"; failures print "FAIL <message>" and exit 1.
try
{
    if (args.Length < 3) throw new ArgumentException("usage: type|read <appExe> <dataRoot> [text killAfterMs]");
    using var app = Launch(args[1], args[2]);
    try
    {
        var editor = WaitForEditor(app);
        Print(Value(editor));
        if (args[0] == "type")
        {
            ((ValuePattern)editor.GetCurrentPattern(ValuePattern.Pattern)).SetValue(Encoding.UTF8.GetString(Convert.FromBase64String(args[3])));
            Thread.Sleep(int.Parse(args[4]));
        }
    }
    finally
    {
        if (!app.HasExited)
        {
            app.Kill();
            app.WaitForExit(10_000);
        }
    }
    Console.WriteLine("OK");
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine("FAIL " + ex.Message.ReplaceLineEndings(" "));
    return 1;
}

static void Print(string value) => Console.WriteLine("VALUE " + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)));

static Process Launch(string exe, string root)
{
    var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    psi.Environment["TABLEPLSU_ROOT"] = root;
    return Process.Start(psi) ?? throw new InvalidOperationException("Could not start " + exe);
}

static string Value(AutomationElement editor) => ((ValuePattern)editor.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;

static AutomationElement WaitForEditor(Process app)
{
    var cond = new AndCondition(
        new PropertyCondition(AutomationElement.AutomationIdProperty, "Editor"), // SqlEditorView's AvalonEdit TextEditor
        new PropertyCondition(AutomationElement.IsValuePatternAvailableProperty, true));
    var deadline = DateTime.UtcNow.AddSeconds(90);
    AutomationElement? window = null;
    while (DateTime.UtcNow < deadline)
    {
        if (app.HasExited) throw new InvalidOperationException($"The app exited with code {app.ExitCode}.");
        window = AutomationElement.RootElement.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, app.Id));
        var editor = window?.FindFirst(TreeScope.Descendants, cond);
        if (editor is not null) return editor;
        Thread.Sleep(200);
    }
    var dump = window?.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
        .Select(e => $"{e.Current.ClassName}/{e.Current.AutomationId}")
        .Where(s => s != "/");
    throw new TimeoutException("No SQL editor appeared in the app window. Elements: " + string.Join("; ", dump ?? ["(no window)"]));
}
