using TablePlsu.App.Infrastructure;

namespace TablePlsu.App.Features.TableData;

/// <summary>
/// Table / view / materialized view data tabs (spec §6, §8). Registers the tab factories for <see cref="TabKinds.Table"/>,
/// <see cref="TabKinds.View"/> and <see cref="TabKinds.MaterializedView"/>. The tab handles its grid, filter bar, paging,
/// undo/redo and Data/Structure/Indexes commands itself (it is the <see cref="ICommandTarget"/> of its Grid and FilterBar
/// scopes and receives workspace-scope commands while active); Commit / Preview / Discard come through the shell's
/// changes.* handlers. Structure / Indexes / DDL content comes from <see cref="TableSubViewRegistry"/>.
/// </summary>
public sealed class TableDataFeature : IFeature
{
    public void Register(FeatureContext ctx)
    {
        ctx.Tabs.Register(TabKinds.Table, r => new TableTabViewModel(r));
        ctx.Tabs.Register(TabKinds.View, r => new TableTabViewModel(r));
        ctx.Tabs.Register(TabKinds.MaterializedView, r => new TableTabViewModel(r));
    }
}
