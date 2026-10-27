using TablePlsu.App.Infrastructure;
using TablePlsu.App.Shell;
using TablePlsu.Core.Commands;

namespace TablePlsu.App.Features.Welcome;

/// <summary>Welcome screen commands: Ctrl+F focuses the connection search, Ctrl+N opens a new window.</summary>
public sealed class WelcomeFeature : IFeature
{
    public void Register(FeatureContext ctx)
    {
        ctx.Commands.Register(CommandIds.WelcomeSearch,
            c => ((WelcomeViewModel)c.Target!).RequestSearchFocus(),
            c => c.Target is WelcomeViewModel { IsFormOpen: false });

        ctx.Commands.Register(CommandIds.WindowNew, _ => ShellWindows.OpenNew());
    }
}
