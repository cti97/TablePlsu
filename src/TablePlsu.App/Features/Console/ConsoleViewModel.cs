using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using TablePlsu.App.Features.Workspace;
using TablePlsu.App.Infrastructure;

namespace TablePlsu.App.Features.Console;

/// <summary>The console panel's view-model: the workspace log entries plus Clear / copy helpers.</summary>
public sealed class ConsoleViewModel(IWorkspace workspace)
{
    public IWorkspace Workspace { get; } = workspace;

    public ObservableCollection<ConsoleEntry> Entries => Workspace.ConsoleLog.Entries;

    public void Clear() => Workspace.ConsoleLog.Clear();

    /// <summary>"HH:mm:ss.fff  message" lines for the clipboard.</summary>
    public static string Format(IEnumerable<ConsoleEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var e in entries)
        {
            if (sb.Length > 0) sb.Append("\r\n");
            sb.Append(e.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append("  ").Append(e.Message);
        }
        return sb.ToString();
    }
}
