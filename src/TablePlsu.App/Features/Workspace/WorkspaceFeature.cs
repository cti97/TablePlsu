using System.Windows;
using System.Windows.Controls;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Commands;

namespace TablePlsu.App.Features.Workspace;

/// <summary>
/// The workspace shell's own registrations: tab/workspace/layout command handlers, the empty tab kind and the built-in
/// toolbar items (templates "tb.&lt;id&gt;" in WorkspaceView.xaml). Other features can override any handler by registering
/// the same command id later.
/// </summary>
public sealed class WorkspaceFeature : IFeature
{
    public void Register(FeatureContext ctx)
    {
        var c = ctx.Commands;
        c.Register(CommandIds.TabNew, x => Ws(x)!.NewTab(), Has);
        c.Register(CommandIds.EditorNew, x => Ws(x)!.NewSqlEditor(), Has);
        c.Register(CommandIds.TabClose, x => Ws(x)!.CloseActiveTab(), x => Ws(x)?.ActiveTab is not null);
        c.Register(CommandIds.TabNext, x => Ws(x)!.NextTab(1), Has);
        c.Register(CommandIds.TabPrev, x => Ws(x)!.NextTab(-1), Has);
        for (var i = 1; i <= 9; i++)
        {
            var n = i;
            c.Register(CommandIds.TabGotoPrefix + n, x => Ws(x)!.GotoTab(n), Has);
        }
        c.Register(CommandIds.TabReload, x => Ws(x)!.ActiveTab!.Reload(), x => Ws(x)?.ActiveTab is not null);
        c.Register(CommandIds.WorkspaceReload, x => Ws(x)!.RequestReload(), Has);
        c.Register(CommandIds.WorkspaceReconnect, x => Ws(x)!.ReconnectAsync().Forget(), Has);
        c.Register(CommandIds.WorkspaceDisconnect, x => Ws(x)!.DisconnectAsync().Forget(), Has);
        c.Register(CommandIds.SidebarToggle, x => Ws(x)!.IsSidebarVisible = !Ws(x)!.IsSidebarVisible, Has);
        c.Register(CommandIds.ConsoleToggle, x => Ws(x)!.IsConsoleVisible = !Ws(x)!.IsConsoleVisible, Has);

        // Ctrl+S commits staged changes; with nothing staged it saves the active tab (autosave flush / linked file).
        c.Register(CommandIds.ChangesCommit, x =>
        {
            var tab = Ws(x)!.ActiveTab!;
            if (tab.HasPendingChanges) tab.CommitAsync().Forget(ex => Dialogs.Error(ex.Message, "Commit failed"));
            else tab.Save();
        }, x => Ws(x)?.ActiveTab is not null);
        c.Register(CommandIds.ChangesPreview, x => Ws(x)!.ActiveTab!.PreviewChanges(), x => Ws(x)?.ActiveTab?.HasPendingChanges == true);
        c.Register(CommandIds.ChangesDiscard, x => Ws(x)!.ActiveTab!.DiscardChanges(), x => Ws(x)?.ActiveTab?.HasPendingChanges == true);

        ctx.Tabs.Register(TabKinds.Empty, _ => new EmptyTabViewModel());

        foreach (var id in new[]
                 {
                     ToolbarItemIds.ToggleSidebar, ToolbarItemIds.Connection, ToolbarItemIds.Changes, ToolbarItemIds.SafeMode,
                     ToolbarItemIds.Database, ToolbarItemIds.NewSql, ToolbarItemIds.Reload, ToolbarItemIds.Command, ToolbarItemIds.Panels,
                 })
        {
            ctx.Toolbar.Register(new ToolbarItemDefinition(id, ws => Templated(ws, id)));
        }
        ctx.Toolbar.Register(new ToolbarItemDefinition(ToolbarItemIds.Status, ws => Templated(ws, ToolbarItemIds.Status)) { Stretch = true });
    }

    private static WorkspaceViewModel? Ws(CommandContext ctx) => ctx.Workspace as WorkspaceViewModel;

    private static bool Has(CommandContext ctx) => ctx.Workspace is WorkspaceViewModel;

    /// <summary>A ContentControl showing the "tb.&lt;id&gt;" template from WorkspaceView's resources with the workspace as content.</summary>
    private static FrameworkElement Templated(IWorkspace ws, string id)
    {
        var cc = new ContentControl { Content = ws, Focusable = false, IsTabStop = false };
        cc.SetResourceReference(ContentControl.ContentTemplateProperty, "tb." + id);
        return cc;
    }
}
