using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Commands;

namespace TablePlsu.App.Features.Sidebar;

/// <summary>
/// Left sidebar (spec §5): fills <see cref="WorkspacePanel.Sidebar"/> with <see cref="SidebarView"/> (one
/// <see cref="SidebarViewModel"/> per workspace, kept in <c>ws.Services</c>), registers the base context-menu
/// contributor and <c>sidebar.search</c> (Ctrl+Shift+F). Ctrl+0 (<c>sidebar.toggle</c>) is the workspace's own handler.
/// <c>sidebar.openItem</c> / <c>sidebar.dropItem</c> / <c>sidebar.truncateItem</c> are handled by the view-model as the
/// Sidebar scope's <see cref="ICommandTarget"/>.
/// </summary>
public sealed class SidebarFeature : IFeature
{
    public void Register(FeatureContext ctx)
    {
        ctx.Panels.Register(WorkspacePanel.Sidebar, ws => new SidebarView { DataContext = For(ws) });
        ctx.SidebarMenu.Register(new SidebarMenuContributor());
        ctx.Commands.Register(CommandIds.SidebarSearch, x =>
        {
            var ws = x.Workspace!;
            ws.IsSidebarVisible = true;
            For(ws).RequestFocusSearch();
        }, x => x.Workspace is not null);
    }

    /// <summary>The workspace's sidebar view-model (created on first use).</summary>
    public static SidebarViewModel For(IWorkspace ws) =>
        ws.Services.GetOrAdd(() => new SidebarViewModel(ws, AppServices.IsInitialized ? new SidebarSchemaStore(AppServices.Paths) : null));
}
