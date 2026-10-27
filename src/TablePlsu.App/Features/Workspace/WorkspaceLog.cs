using System.Collections.ObjectModel;
using TablePlsu.App.Infrastructure;

namespace TablePlsu.App.Features.Workspace;

public enum ConsoleEntryKind { Info, Query, Error }

/// <summary>One console line. <see cref="Message"/> is truncated to <see cref="WorkspaceLog.MaxMessageLength"/>.</summary>
public sealed record ConsoleEntry(DateTimeOffset Timestamp, string Message, ConsoleEntryKind Kind);

/// <summary>
/// The workspace console log (spec §11 "Console log"): the last <see cref="Capacity"/> entries. <see cref="Add"/> is
/// thread-safe; <see cref="Entries"/> is updated on the UI thread (at Background priority) for binding.
/// </summary>
public sealed class WorkspaceLog
{
    public const int Capacity = 1000;
    public const int MaxMessageLength = 10_000;

    /// <summary>Bindable entries, oldest first. UI thread only.</summary>
    public ObservableCollection<ConsoleEntry> Entries { get; } = [];

    /// <summary>Raised on the UI thread after an entry was appended.</summary>
    public event EventHandler<ConsoleEntry>? EntryAdded;

    public void Add(string message, ConsoleEntryKind kind = ConsoleEntryKind.Info)
    {
        if (message.Length > MaxMessageLength) message = message[..MaxMessageLength] + $"… ({message.Length:N0} chars)";
        var entry = new ConsoleEntry(DateTimeOffset.Now, message, kind);
        UiDispatch.Run(() => Append(entry), System.Windows.Threading.DispatcherPriority.Background);
    }

    public void Clear() => UiDispatch.Run(Entries.Clear);

    private void Append(ConsoleEntry entry)
    {
        Entries.Add(entry);
        while (Entries.Count > Capacity) Entries.RemoveAt(0);
        EntryAdded?.Invoke(this, entry);
    }
}
