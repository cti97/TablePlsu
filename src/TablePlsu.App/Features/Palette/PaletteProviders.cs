using System.Windows;
using Npgsql;
using TablePlsu.App.Features.Sidebar;
using TablePlsu.App.Features.Workspace;
using TablePlsu.App.Infrastructure;
using TablePlsu.App.Shell;
using TablePlsu.Core.Commands;
using TablePlsu.Core.Connections;
using TablePlsu.Core.Db;
using TablePlsu.Core.Metadata;

namespace TablePlsu.App.Features.Palette;

/// <summary>
/// The server's database list for a workspace (kept in <c>ws.Services</c>). <see cref="Names"/> is the last loaded
/// list (empty until loaded); <see cref="RefreshAsync"/> queries <c>pg_database</c> on the metadata connection.
/// </summary>
public sealed class DatabaseList
{
    public const string Query = "SELECT datname FROM pg_database WHERE datallowconn AND NOT datistemplate ORDER BY datname";

    private Task<IReadOnlyList<string>>? _running;

    public IReadOnlyList<string> Names { get; private set; } = [];

    public bool IsLoaded { get; private set; }

    public static DatabaseList For(IWorkspace ws) => ws.Services.GetOrAdd(() => new DatabaseList());

    public Task<IReadOnlyList<string>> RefreshAsync(DbSession session, CancellationToken ct = default)
    {
        if (_running is { IsCompleted: false }) return _running;
        return _running = Task.Run(async () =>
        {
            var names = await QueryNamesAsync(session, ct).ConfigureAwait(false);
            Names = names;
            IsLoaded = true;
            return names;
        }, ct);
    }

    /// <summary>Runs <see cref="Query"/> on the session's metadata connection (also used by the connection form).</summary>
    public static async Task<IReadOnlyList<string>> QueryNamesAsync(DbSession session, CancellationToken ct = default) =>
        await session.WithMetadataConnectionAsync(async (conn, c) =>
        {
            var list = new List<string>();
            await using var cmd = new NpgsqlCommand(Query, conn);
            await using var reader = await cmd.ExecuteReaderAsync(c).ConfigureAwait(false);
            while (await reader.ReadAsync(c).ConfigureAwait(false)) list.Add(reader.GetString(0));
            return list;
        }, ct).ConfigureAwait(false);
}

/// <summary>Builds the palette's own items. All in-memory (metadata snapshot, cached database list, open tabs, commands).</summary>
public static class PaletteItems
{
    public static IEnumerable<PaletteItem> Databases(IWorkspace ws, IReadOnlyList<string> names) =>
        names.Select(name => new PaletteItem(name, PaletteKinds.Database, inv =>
        {
            if (!inv.NewWindow && name == ws.Database) return;
            ws.OpenDatabase(name, inv.NewWindow);
        })
        {
            Detail = name == ws.Database ? "Database · current" : "Database",
            Priority = 1,
        });

    public static IEnumerable<PaletteItem> Objects(IWorkspace ws, DbMetadata md)
    {
        foreach (var schema in md.Schemas)
        {
            var schemaName = schema.Name;
            yield return new PaletteItem(schemaName, PaletteKinds.Schema, _ =>
            {
                ws.IsSidebarVisible = true;
                SidebarFeature.For(ws).RevealSchema(schemaName);
            })
            { Detail = "Schema", Priority = 2 };

            foreach (var t in schema.Tables)
            {
                var kind = SidebarTree.ObjectKindOf(t.Kind);
                var r = new SidebarItemRef(t.Schema, t.Name, kind);
                yield return new PaletteItem(t.Name, kind, inv => SidebarActions.Open(ws, r, pinned: true, newTab: inv.NewTab))
                {
                    Detail = $"{SidebarTree.KindText(t.Kind)} · {t.Schema}",
                    SearchText = t.Schema + "." + t.Name,
                    Priority = 3,
                };
            }
            foreach (var f in schema.Functions)
            {
                var kind = f.Kind == RoutineKind.Procedure ? ObjectKinds.Procedure : ObjectKinds.Function;
                var r = new SidebarItemRef(f.Schema, f.Name, kind) { Signature = f.Arguments };
                yield return new PaletteItem(f.Name, kind, inv => SidebarActions.Open(ws, r, pinned: true, newTab: inv.NewTab))
                {
                    Detail = $"{(kind == ObjectKinds.Procedure ? "Procedure" : "Function")} · {f.Schema} · ({f.Arguments})",
                    SearchText = f.Schema + "." + f.Name,
                    Priority = 2,
                };
            }
        }
    }

    public static IEnumerable<PaletteItem> SqlTabs(IWorkspace ws) =>
        ws.Tabs.Where(t => t.Kind == TabKinds.SqlEditor).Select(t => new PaletteItem(t.DisplayTitle, PaletteKinds.SqlTab, _ => ws.ActivateTab(t))
        {
            Detail = "Open SQL tab",
            Priority = 2,
        });

    /// <summary>Commands that make sense from the palette (Global, plus Workspace when a workspace is open) and can run now.</summary>
    public static IEnumerable<PaletteItem> Commands(IWorkspace? ws, Window? window)
    {
        if (!AppServices.IsInitialized) yield break;
        foreach (var def in CommandIds.All)
        {
            if (def.Scope is not (CommandScope.Global or CommandScope.Workspace)) continue;
            if (def.Scope == CommandScope.Workspace && ws is null) continue;
            if (def.Id == CommandIds.PaletteOpenAnything || def.Id.StartsWith(CommandIds.TabGotoPrefix, StringComparison.Ordinal)) continue;
            if (!AppServices.Commands.IsRegistered(def.Id)) continue;
            var id = def.Id;
            var ctx = Context(id, ws, window);
            bool can;
            try { can = AppServices.Commands.CanExecute(id, ctx); }
            catch (Exception) { can = false; }
            if (!can) continue;
            yield return new PaletteItem(def.Title, PaletteKinds.Command, _ => Execute(id, ws, window))
            {
                Shortcut = ShortcutText.Gesture(id),
                Detail = "Command",
                Priority = 1,
            };
        }
    }

    public static CommandContext Context(string id, IWorkspace? ws, Window? window) => new()
    {
        CommandId = id,
        Scope = CommandIds.Get(id)?.Scope ?? CommandScope.Global,
        Window = window,
        Workspace = ws,
        Target = ws,
    };

    public static void Execute(string id, IWorkspace? ws, Window? window)
    {
        if (ws is WorkspaceViewModel wvm && wvm.ExecuteCommand(id)) return;
        AppServices.Commands.TryExecute(id, Context(id, ws, window));
    }

    public static IEnumerable<PaletteItem> Connections(Window? window)
    {
        if (!AppServices.IsInitialized) yield break;
        foreach (var p in AppServices.Connections.Profiles)
        {
            var profile = p;
            yield return new PaletteItem(p.DisplayName, PaletteKinds.Connection, _ => OpenConnection(profile, window))
            {
                Detail = $"{p.User}@{p.Host}:{p.Port}/{p.Database}" + (string.IsNullOrWhiteSpace(p.Tag) ? "" : " · " + p.Tag),
                SearchText = p.DisplayName + " " + p.Host + " " + p.Database,
            };
        }
    }

    /// <summary>Ctrl+Shift+K → Enter: opens the connection in a new window (in this window when it shows the welcome screen).</summary>
    public static void OpenConnection(ConnectionProfile profile, Window? window)
    {
        if (window?.DataContext is MainViewModel main)
            main.ConnectAsync(profile, newWindow: main.Workspace is not null).Forget(ex => Dialogs.Error(ex.Message, "Connect failed"));
        else
            ShellWindows.OpenNew(vm => vm.ConnectAsync(profile).Forget(ex => Dialogs.Error(ex.Message, "Connect failed")));
    }
}

/// <summary>Registers the built-in items for every palette mode.</summary>
public sealed class BuiltInPaletteProvider : IPaletteItemProvider
{
    public IEnumerable<PaletteItem> GetItems(PaletteQuery query)
    {
        var ws = query.Workspace;
        switch (query.Mode)
        {
            case PaletteMode.Databases:
                return ws is null ? [] : PaletteItems.Databases(ws, DatabaseList.For(ws).Names);
            case PaletteMode.Connections:
                return PaletteItems.Connections(query.Window);
            default:
                var items = PaletteItems.Commands(ws, query.Window);
                if (ws is null) return items;
                return items
                    .Concat(PaletteItems.Objects(ws, ws.Metadata.Current))
                    .Concat(PaletteItems.Databases(ws, DatabaseList.For(ws).Names))
                    .Concat(PaletteItems.SqlTabs(ws));
        }
    }
}
