using System.Windows;

namespace TablePlsu.App.Infrastructure;

/// <summary>What the palette is searching (Ctrl+P = Anything, Ctrl+K = Databases, Ctrl+Shift+K = Connections).</summary>
public enum PaletteMode { Anything, Databases, Connections }

/// <summary>Context handed to providers. <see cref="Workspace"/> is null on the welcome screen.</summary>
public sealed record PaletteQuery(PaletteMode Mode, IWorkspace? Workspace, Window? Window);

/// <summary>How a palette item was invoked: Enter, Ctrl+Enter (new tab) or Shift+Enter (new window).</summary>
public sealed record PaletteInvocation(PaletteQuery Query, bool NewTab, bool NewWindow);

/// <summary>One palette row. <see cref="SearchText"/> is what the fuzzy matcher scores (defaults to <see cref="Title"/>).</summary>
public sealed record PaletteItem(string Title, string Kind, Action<PaletteInvocation> Invoke)
{
    public string? Detail { get; init; }
    public string? SearchText { get; init; }
    /// <summary>Shortcut text shown on the right (for app commands).</summary>
    public string? Shortcut { get; init; }
    /// <summary>Text inserted by Tab (complete to the highlighted name).</summary>
    public string? CompletionText { get; init; }
    /// <summary>Tie-breaker: higher sorts first among equal scores.</summary>
    public int Priority { get; init; }
}

/// <summary>Supplies palette items. Called when the palette opens; must be in-memory only (no I/O).</summary>
public interface IPaletteItemProvider
{
    IEnumerable<PaletteItem> GetItems(PaletteQuery query);
}

public sealed class PaletteRegistry
{
    private readonly List<IPaletteItemProvider> _providers = [];

    public void Register(IPaletteItemProvider provider) => _providers.Add(provider);

    public IReadOnlyList<IPaletteItemProvider> Providers => _providers;

    public IEnumerable<PaletteItem> GetItems(PaletteQuery query) => _providers.SelectMany(p => p.GetItems(query));
}
