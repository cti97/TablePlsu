using System.Windows;

namespace TablePlsu.App.Infrastructure;

/// <summary>
/// A feature plugs into the shell once, at startup, through <see cref="FeatureContext"/>. Every feature is listed
/// explicitly in <see cref="Features.All"/> (no reflection). <c>Register</c> must be cheap: no I/O, no DB, no Npgsql types.
/// </summary>
public interface IFeature
{
    void Register(FeatureContext ctx);
}

/// <summary>The extension points features register into (architecture §Extension points).</summary>
public sealed class FeatureContext
{
    private readonly List<Action<IWorkspace>> _workspaceOpened = [];
    private readonly List<Action<Window>> _windowCreated = [];

    public FeatureContext(CommandRegistry commands) => Commands = commands;

    /// <summary>Command handlers by command id (bindings come from the keymap).</summary>
    public CommandRegistry Commands { get; }

    /// <summary>Tab kind → factory (<c>ctx.Tabs.Register(kind, request =&gt; new MyTabViewModel(…))</c>).</summary>
    public TabFactoryRegistry Tabs { get; } = new();

    /// <summary>Sidebar item context menu contributors.</summary>
    public SidebarMenuRegistry SidebarMenu { get; } = new();

    /// <summary>Open Anything / Open Database / Open Connection item providers.</summary>
    public PaletteRegistry Palette { get; } = new();

    /// <summary>Workspace toolbar items by id (ordered by <c>settings.toolbar.items</c>).</summary>
    public ToolbarRegistry Toolbar { get; } = new();

    /// <summary>Workspace layout slots: left sidebar, bottom console, right sidebar.</summary>
    public WorkspacePanelRegistry Panels { get; } = new();

    /// <summary>Creates the Safe Mode gate for each workspace. Replace with <see cref="UseSafeMode"/>.</summary>
    public Func<IWorkspace, ISafeModeGate> SafeModeFactory { get; private set; } = ws => new DefaultSafeModeGate(ws);

    public void UseSafeMode(Func<IWorkspace, ISafeModeGate> factory) => SafeModeFactory = factory;

    /// <summary>
    /// Runs when a workspace is created, before its connection finishes (restore editor tabs from disk here so text shows
    /// immediately). Runs on the UI thread.
    /// </summary>
    public void OnWorkspaceOpened(Action<IWorkspace> handler) => _workspaceOpened.Add(handler);

    /// <summary>Runs for every new main window after it is created (before it is shown).</summary>
    public void OnWindowCreated(Action<Window> handler) => _windowCreated.Add(handler);

    internal void RaiseWorkspaceOpened(IWorkspace workspace)
    {
        foreach (var h in _workspaceOpened) h(workspace);
    }

    internal void RaiseWindowCreated(Window window)
    {
        foreach (var h in _windowCreated) h(window);
    }
}
