using System.Diagnostics;
using TablePlsu.UiFlows;

// Usage: TablePlsu.UiFlows [--exe path\TablePlsu.App.exe] [--pg "Host=..;Port=..;Username=..;Database=.."] [--only name,name] [--dump]
var argv = args.ToList();
string Arg(string name, string def)
{
    var i = argv.IndexOf(name);
    return i >= 0 && i + 1 < argv.Count ? argv[i + 1] : def;
}

var exe = Path.GetFullPath(Arg("--exe", "artifacts/publish/TablePlsu.App.exe"));
var pg = Arg("--pg", Environment.GetEnvironmentVariable("TABLEPLSU_TEST_PG") ?? "Host=localhost;Port=54329;Username=postgres;Database=tableplsu_test");
var only = Arg("--only", "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

if (!File.Exists(exe)) { Console.Error.WriteLine("App not found: " + exe + " (run scripts/publish.ps1 -NoMeasure)"); return 2; }

var flows = new Flows(exe, pg);
try
{
    if (argv.Contains("--dump")) { flows.DumpMode(); return 0; }
    if (argv.Contains("--probe")) { flows.ProbeMode(Arg("--probe", "")); return 0; }
    flows.RunAll(only);
}
finally
{
    flows.Cleanup();
}

Console.WriteLine();
Console.WriteLine("| Flow | Result | Detail |");
Console.WriteLine("|---|---|---|");
foreach (var r in flows.Results)
    Console.WriteLine($"| {r.Name} | {(r.Ok ? "PASS" : "FAIL")} | {r.Detail.Replace("|", "/").Replace("\n", " ")} |");
var failed = flows.Results.Count(r => !r.Ok);
Console.WriteLine();
Console.WriteLine(failed == 0 ? $"UI FLOWS OK ({flows.Results.Count})" : $"UI FLOWS FAILED: {failed} of {flows.Results.Count}");
return failed == 0 ? 0 : 1;
