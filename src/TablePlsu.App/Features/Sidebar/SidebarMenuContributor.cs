using System.Windows.Controls;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Commands;

namespace TablePlsu.App.Features.Sidebar;

/// <summary>
/// The sidebar's own context-menu entries (Order 0): Open in new tab, Open structure, Copy name, Copy Script As ▸,
/// Truncate…, Delete…; for a schema: New Table…, Copy name, Reload schema.
/// </summary>
public sealed class SidebarMenuContributor : ISidebarMenuContributor
{
    public int Order => 0;

    public void Contribute(SidebarMenuContext context, IList<object> items)
    {
        var ws = context.Workspace;
        var refs = context.Items;
        if (refs.Count == 0) return;

        if (refs.All(r => r.ObjectKind == ObjectKinds.Schema))
        {
            var schema = refs[0].Schema;
            items.Add(Item("New Table…", () => SidebarActions.NewTable(ws, schema)));
            items.Add(Item("Copy Name", () => SidebarActions.CopyNames(refs)));
            items.Add(Item("Reload Schema", () => ws.Metadata.EnsureSchemaLoadedAsync(schema, force: true).Forget(ex => ws.Log("Reload failed: " + ex.Message))));
            return;
        }

        var objects = refs.Where(r => r.ObjectKind != ObjectKinds.Schema).ToList();
        var first = objects[0];
        var openable = first.ObjectKind is ObjectKinds.Table or ObjectKinds.View or ObjectKinds.MaterializedView or ObjectKinds.Function or ObjectKinds.Procedure;
        var relation = first.ObjectKind is ObjectKinds.Table or ObjectKinds.View or ObjectKinds.MaterializedView;
        if (openable)
        {
            items.Add(Item("Open in New Tab", () =>
            {
                foreach (var r in objects) SidebarActions.Open(ws, r, pinned: true, newTab: true);
            }));
        }
        if (relation) items.Add(Item("Open Structure", () => SidebarActions.OpenStructure(ws, first)));
        items.Add(Item("Copy Name", () => SidebarActions.CopyNames(objects)));

        var script = new MenuItem { Header = "Copy Script As" };
        script.Items.Add(Item("CREATE", () => SidebarActions.CopyScriptAsync(ws, objects, ScriptAs.Create).Forget()));
        script.Items.Add(Item("DROP", () => SidebarActions.CopyScriptAsync(ws, objects, ScriptAs.Drop).Forget()));
        var tables = objects.Any(r => r.ObjectKind == ObjectKinds.Table);
        if (tables) script.Items.Add(Item("TRUNCATE", () => SidebarActions.CopyScriptAsync(ws, objects, ScriptAs.Truncate).Forget()));
        if (objects.Any(r => r.ObjectKind is ObjectKinds.Table or ObjectKinds.View or ObjectKinds.MaterializedView))
            script.Items.Add(Item("SELECT … LIMIT 100", () => SidebarActions.CopyScriptAsync(ws, objects, ScriptAs.Select).Forget()));
        items.Add(script);

        items.Add(new Separator());
        if (tables)
            items.Add(Item("Truncate…", () => SidebarActions.RunDestructiveAsync(ws, DestructiveAction.Truncate, objects).Forget(), CommandIds.SidebarTruncateItem));
        items.Add(Item("Delete…", () => SidebarActions.RunDestructiveAsync(ws, DestructiveAction.Drop, objects).Forget(), CommandIds.SidebarDropItem));
    }

    private static MenuItem Item(string header, Action click, string? commandId = null)
    {
        var item = new MenuItem { Header = header };
        if (commandId is not null) item.InputGestureText = ShortcutText.Gesture(commandId);
        item.Click += (_, _) => click();
        return item;
    }
}
