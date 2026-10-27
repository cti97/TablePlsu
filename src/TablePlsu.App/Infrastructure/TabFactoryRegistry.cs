using TablePlsu.App.Features.Workspace;

namespace TablePlsu.App.Infrastructure;

/// <summary>Creates a tab view-model for a request. The workspace attaches it (sets <see cref="TabViewModelBase.Workspace"/>) right after.</summary>
public delegate TabViewModelBase TabFactory(TabRequest request, IWorkspace workspace);

/// <summary>Tab kind → factory. Registered once at startup by features (<c>ctx.Tabs.Register(kind, …)</c>).</summary>
public sealed class TabFactoryRegistry
{
    private readonly Dictionary<string, TabFactory> _factories = new(StringComparer.Ordinal);

    public void Register(string kind, TabFactory factory) => _factories[kind] = factory;

    /// <summary>The architecture-doc signature, for factories that do not need the workspace at construction.</summary>
    public void Register(string kind, Func<TabRequest, TabViewModelBase> factory) => _factories[kind] = (r, _) => factory(r);

    public bool IsRegistered(string kind) => _factories.ContainsKey(kind);

    public IReadOnlyCollection<string> Kinds => _factories.Keys;

    public TabViewModelBase? Create(TabRequest request, IWorkspace workspace) =>
        _factories.TryGetValue(request.Kind, out var f) ? f(request, workspace) : null;
}
