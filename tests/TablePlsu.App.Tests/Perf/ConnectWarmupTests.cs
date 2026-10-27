using TablePlsu.App.Features.Workspace;
using TablePlsu.App.Infrastructure;
using TablePlsu.App.Shell;
using TablePlsu.Core.Connections;
using TablePlsu.Core.Db;

namespace TablePlsu.App.Tests.Perf;

[Collection(AppServicesCollection.Name)]
public class ConnectWarmupTests
{
    private sealed class OpenedProbe(Action<IWorkspace> onOpened) : IFeature
    {
        public void Register(FeatureContext ctx) => ctx.OnWorkspaceOpened(onOpened);
    }

    [Fact]
    public void Opening_a_workspace_starts_connecting_before_workspace_features_restore_their_state()
    {
        var states = new List<DbSessionState>();
        TestApp.Init(new WorkspaceFeature(), new FakeTabsFeature(true), new OpenedProbe(ws => states.Add(ws.State)));
        var main = new MainViewModel();
        main.OpenWorkspace(TestApp.Profile(), ConnectionSecrets.None, null, newWindow: false);
        var ws = Assert.IsType<WorkspaceViewModel>(main.Content);
        try
        {
            // The connection (TCP + TLS + auth + type loading) must already be under way while editor tabs are
            // restored and the workspace view is built, not queued behind them.
            Assert.Equal([DbSessionState.Connecting], states);
        }
        finally
        {
            ws.CloseAsync().GetAwaiter().GetResult();
        }
    }
}
