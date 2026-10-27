using TablePlsu.App.Features.Workspace;
using TablePlsu.App.Infrastructure;

namespace TablePlsu.App.Features.Console;

/// <summary>
/// Console log panel (spec §11 "Console log"): fills <see cref="WorkspacePanel.Console"/> with a view over
/// <see cref="IWorkspace.ConsoleLog"/> (every statement the session sends, background metadata loads, connection events,
/// editor run summaries; timestamps; last 1,000 entries; Clear). Toggled with console.toggle (Ctrl+Alt+C, handled by the
/// workspace shell).
/// </summary>
public sealed class ConsoleFeature : IFeature
{
    public void Register(FeatureContext ctx)
    {
        ctx.Panels.Register(WorkspacePanel.Console, ws => new ConsoleView { DataContext = new ConsoleViewModel(ws) });
        ctx.OnWorkspaceOpened(ws =>
        {
            // Background metadata loads run on their own connection and are not echoed by DbSession.Log.
            ws.Metadata.RefreshingChanged += (_, refreshing) =>
            {
                if (refreshing) ws.ConsoleLog.Add("Loading schema metadata (background)…", ConsoleEntryKind.Query);
            };
        });
    }
}
