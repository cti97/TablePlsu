using System.Windows;
using TablePlsu.App.Infrastructure;

namespace TablePlsu.App.Features.TableData;

/// <summary>
/// Content for the table tab's bottom tabs other than Data (spec §6/§7: Structure, Indexes, and DDL for views).
/// Other features (Structure) register a factory once at startup:
/// <c>TableSubViewRegistry.Register(TableSubViewRegistry.Structure, (ws, schema, table) =&gt; new MyStructureView(…))</c>.
/// The table tab calls the factory lazily the first time the bottom tab is shown and keeps the element while the tab
/// lives; it shows a placeholder when nothing is registered for a kind.
/// </summary>
public static class TableSubViewRegistry
{
    public const string Structure = "structure";
    public const string Indexes = "indexes";
    public const string Ddl = "ddl";

    private static readonly Dictionary<string, Func<IWorkspace, string, string, FrameworkElement>> Factories = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    public static void Register(string kind, Func<IWorkspace, string, string, FrameworkElement> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (Gate) Factories[kind] = factory;
    }

    public static bool Unregister(string kind)
    {
        lock (Gate) return Factories.Remove(kind);
    }

    public static bool IsRegistered(string kind)
    {
        lock (Gate) return Factories.ContainsKey(kind);
    }

    /// <summary>The registered view for <paramref name="kind"/>, or null when none is registered.</summary>
    public static FrameworkElement? Create(string kind, IWorkspace workspace, string schema, string table)
    {
        Func<IWorkspace, string, string, FrameworkElement>? f;
        lock (Gate) Factories.TryGetValue(kind, out f);
        return f?.Invoke(workspace, schema, table);
    }
}
