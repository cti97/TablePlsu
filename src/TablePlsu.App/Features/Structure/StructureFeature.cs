using TablePlsu.App.Infrastructure;

namespace TablePlsu.App.Features.Structure;

/// <summary>
/// Structure, index and DDL views, the create-table editor and the function editor (spec §7).
/// Tab kinds: <c>structure</c> (Structure + Indexes of a table; Parameter "indexes" opens the Indexes sub-tab),
/// <c>viewDdl</c> (view / materialized view DDL), <c>function</c> (pg_get_functiondef editor), <c>createTable</c>.
/// Hostable sub-views for the table tab: <see cref="StructureViews.CreateStructure"/>, <see cref="StructureViews.CreateIndexes"/>,
/// <see cref="StructureViews.CreateViewDdl"/>.
/// </summary>
public sealed class StructureFeature : IFeature
{
    public void Register(FeatureContext ctx)
    {
        ctx.Tabs.Register(TabKinds.Structure, r => new StructureTabViewModel(r));
        ctx.Tabs.Register(StructureTabKinds.ViewDdl, r => new ViewDdlTabViewModel(r));
        ctx.Tabs.Register(TabKinds.Function, r => new FunctionTabViewModel(r));
        ctx.Tabs.Register(TabKinds.CreateTable, r => new CreateTableTabViewModel(r));

        // Bottom tabs of the TableData table tab (Ctrl+Alt+2 / Ctrl+Alt+3).
        TableData.TableSubViewRegistry.Register(TableData.TableSubViewRegistry.Structure, StructureViews.CreateStructure);
        TableData.TableSubViewRegistry.Register(TableData.TableSubViewRegistry.Indexes, StructureViews.CreateIndexes);
        TableData.TableSubViewRegistry.Register(TableData.TableSubViewRegistry.Ddl, StructureViews.CreateViewDdl);
    }
}
