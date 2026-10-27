using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Npgsql;
using TablePlsu.App.Features.Workspace;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.History;
using TablePlsu.Core.Metadata;
using TablePlsu.Core.Sidebar;

namespace TablePlsu.App.Features.Sidebar;

public enum ScriptAs { Create, Drop, Truncate, Select }

public enum DestructiveAction { Truncate, Drop }

/// <summary>What the Truncate… / Delete… confirmation returns.</summary>
public sealed record DestructiveOptions(bool Cascade, bool RestartIdentity);

/// <summary>
/// The sidebar's object actions, shared by clicks, keys, the context menu and the palette: open (preview / pinned / new
/// tab / structure), copy name, Copy Script As, Truncate… and Delete…. Truncate and Delete run immediately (after the
/// confirm dialog and the workspace Safe Mode gate) in one transaction on a pooled connection; they are NOT staged as
/// pending changes (spec §5 says staged; the shared pending-changes model is not available to the sidebar).
/// </summary>
public static class SidebarActions
{
    /// <summary><see cref="TabRequest.Parameter"/> for "Open structure".</summary>
    public const string StructureParameter = "structure";

    /// <summary>Clipboard writer (tests replace it).</summary>
    public static Action<string> SetClipboard { get; set; } = DefaultSetClipboard;

    /// <summary>Confirmation for Truncate… / Delete… (tests replace it). Null = cancelled.</summary>
    public static Func<IWorkspace, DestructiveAction, IReadOnlyList<ScriptObject>, DestructiveOptions?> ConfirmDestructive { get; set; } = ShowDestructiveDialog;

    /// <summary>Error reporter (tests replace it).</summary>
    public static Action<string, string> ShowError { get; set; } = (message, title) => Dialogs.Error(message, title);

    // ---- open -----------------------------------------------------------------------------------------------------

    public static TabRequest? RequestFor(SidebarItemRef item, bool pinned, bool newTab, DbMetadata? metadata = null)
    {
        switch (item.ObjectKind)
        {
            case ObjectKinds.Table or ObjectKinds.View or ObjectKinds.MaterializedView:
                return TabRequest.ForObject(item.Schema, item.Name, item.ObjectKind, pinned, newTab);
            case ObjectKinds.Function or ObjectKinds.Procedure:
                var fn = metadata is null ? null : SidebarTree.FindFunction(metadata, item);
                return TabRequest.ForObject(item.Schema, item.Name, item.ObjectKind, pinned: true, newTab) with
                {
                    Title = fn?.Signature ?? $"{item.Name}({item.Signature})",
                    Parameter = (object?)fn ?? item.Signature,
                };
            default:
                return null;
        }
    }

    /// <summary>Opens a table/view/function. Single click = preview, double click = pinned, "Open in new tab" = new tab.</summary>
    public static TabViewModelBase? Open(IWorkspace ws, SidebarItemRef item, bool pinned = false, bool newTab = false)
    {
        if (RequestFor(item, pinned, newTab, ws.Metadata.Current) is not { } request) return null;
        var tab = ws.OpenTab(request);
        if (tab is null) ws.Log($"Nothing can open a '{request.Kind}' tab yet.");
        return tab;
    }

    public static TabViewModelBase? OpenStructure(IWorkspace ws, SidebarItemRef item)
    {
        if (RequestFor(item, pinned: true, newTab: false, ws.Metadata.Current) is not { } request) return null;
        var tab = ws.OpenTab(request with { Parameter = StructureParameter });
        if (tab is null) ws.Log($"Nothing can open a '{request.Kind}' structure tab yet.");
        return tab;
    }

    /// <summary>"+" → New Table: requests a <see cref="TabKinds.CreateTable"/> tab for <paramref name="schema"/>.</summary>
    public static TabViewModelBase? NewTable(IWorkspace ws, string? schema)
    {
        schema ??= ws.Metadata.Current.EffectiveSearchPath.FirstOrDefault() ?? "public";
        var tab = ws.OpenTab(new TabRequest { Kind = TabKinds.CreateTable, Schema = schema, Pinned = true, InNewTab = true, Title = "New Table" });
        if (tab is null) ws.Log("Create Table is not available yet.");
        return tab;
    }

    // ---- copy -----------------------------------------------------------------------------------------------------

    public static void CopyNames(IReadOnlyList<SidebarItemRef> items) =>
        SetClipboard(string.Join(Environment.NewLine, items.Select(i => i.Name)));

    /// <summary>Builds the script text (CREATE may query the server for view/routine definitions).</summary>
    public static async Task<string> BuildScriptAsync(IWorkspace ws, IReadOnlyList<SidebarItemRef> items, ScriptAs kind, CancellationToken ct = default)
    {
        var md = ws.Metadata.Current;
        var objects = items.Select(i => SidebarTree.ToScriptObject(md, i)).OfType<ScriptObject>().ToList();
        switch (kind)
        {
            case ScriptAs.Drop:
                return string.Join("\n", ScriptGenerator.Drop(objects));
            case ScriptAs.Truncate:
                return ScriptGenerator.Truncate(objects);
            case ScriptAs.Select:
                return string.Join("\n", objects.Where(o => o.Kind is ScriptObjectKind.Table or ScriptObjectKind.View or ScriptObjectKind.MaterializedView or ScriptObjectKind.ForeignTable)
                    .Select(o => ScriptGenerator.Select(o.Schema, o.Name)));
        }

        var parts = new List<string>();
        foreach (var (obj, item) in objects.Zip(items))
        {
            if (ScriptGenerator.CanCreateFromMetadata(obj.Kind))
            {
                if (ScriptGenerator.CreateFromMetadata(md, obj) is { } sql) parts.Add(sql);
                else if (obj.Kind is ScriptObjectKind.Table or ScriptObjectKind.ForeignTable
                         && await ws.Metadata.EnsureSchemaLoadedAsync(obj.Schema, ct: ct).ConfigureAwait(false) is { } loaded
                         && loaded.FindTable(obj.Name) is { } t)
                    parts.Add(ScriptGenerator.CreateTable(t));
                else parts.Add($"-- {obj.Target}: not found in the schema cache");
                continue;
            }
            var oid = SidebarTree.FindFunction(md, item)?.Oid ?? 0;
            if (ScriptGenerator.DefinitionQuery(obj, oid) is not { } query)
            {
                parts.Add($"-- CREATE for {obj.Target} is not supported");
                continue;
            }
            var def = await ws.Session.WithMetadataConnectionAsync(async (conn, c) =>
            {
                await using var cmd = new NpgsqlCommand(query, conn);
                return await cmd.ExecuteScalarAsync(c).ConfigureAwait(false) as string;
            }, ct).ConfigureAwait(false);
            parts.Add(def is null ? $"-- {obj.Target}: no definition" : ScriptGenerator.FinishDefinition(def));
        }
        return string.Join("\n\n", parts);
    }

    public static async Task CopyScriptAsync(IWorkspace ws, IReadOnlyList<SidebarItemRef> items, ScriptAs kind)
    {
        try
        {
            var text = await BuildScriptAsync(ws, items, kind).ConfigureAwait(true);
            UiDispatch.Run(() => SetClipboard(text));
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            UiDispatch.Run(() => ShowError(ex.Message, "Copy Script failed"));
        }
    }

    // ---- truncate / drop ------------------------------------------------------------------------------------------

    /// <summary>The statements Truncate… / Delete… would send for <paramref name="objects"/> with <paramref name="options"/>.</summary>
    public static IReadOnlyList<string> Statements(DestructiveAction action, IReadOnlyList<ScriptObject> objects, DestructiveOptions options) =>
        action == DestructiveAction.Truncate
            ? ScriptGenerator.Truncate(objects, options.Cascade, options.RestartIdentity) is { Length: > 0 } t ? [t] : []
            : ScriptGenerator.Drop(objects, options.Cascade);

    /// <summary>
    /// Truncate… / Delete…: confirm dialog (options), then the Safe Mode gate, then executes in one transaction, logs to
    /// the console and history, refreshes the affected schemas and reloads open tabs of truncated tables.
    /// Returns true when the statements ran.
    /// </summary>
    public static async Task<bool> RunDestructiveAsync(IWorkspace ws, DestructiveAction action, IReadOnlyList<SidebarItemRef> items)
    {
        var md = ws.Metadata.Current;
        var objects = items.Where(i => i.ObjectKind != ObjectKinds.Schema)
            .Select(i => SidebarTree.ToScriptObject(md, i)).OfType<ScriptObject>().ToList();
        if (action == DestructiveAction.Truncate)
            objects = [.. objects.Where(o => o.Kind is ScriptObjectKind.Table or ScriptObjectKind.ForeignTable)];
        if (objects.Count == 0) return false;

        var options = ConfirmDestructive(ws, action, objects);
        if (options is null) return false;
        var statements = Statements(action, objects, options);
        if (statements.Count == 0) return false;
        if (!await ws.SafeMode.ConfirmAsync(statements).ConfigureAwait(true)) return false;

        var error = await ExecuteAsync(ws, statements).ConfigureAwait(true);
        if (error is not null)
        {
            ShowError(error, action == DestructiveAction.Truncate ? "Truncate failed" : "Delete failed");
            return false;
        }

        foreach (var schema in objects.Select(o => o.Schema).Distinct())
            ws.Metadata.EnsureSchemaLoadedAsync(schema, force: true).Forget(ex => ws.Log("Schema refresh failed: " + ex.Message));
        if (action == DestructiveAction.Truncate)
        {
            foreach (var tab in ws.Tabs.Where(t => objects.Any(o => o.Schema == t.Schema && o.Name == t.Name)).ToList())
                tab.Reload();
        }
        return true;
    }

    /// <summary>Runs <paramref name="statements"/> in one transaction off the UI thread. Returns the error message, or null.</summary>
    public static Task<string?> ExecuteAsync(IWorkspace ws, IReadOnlyList<string> statements) => Task.Run(async () =>
    {
        var sw = Stopwatch.StartNew();
        var sql = string.Join("\n", statements);
        try
        {
            await using var conn = await ws.Session.OpenConnectionAsync().ConfigureAwait(false);
            await using var tx = await conn.BeginTransactionAsync().ConfigureAwait(false);
            foreach (var s in statements)
            {
                ws.Log(s);
                await using var cmd = new NpgsqlCommand(s, conn, tx);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            await tx.CommitAsync().ConfigureAwait(false);
            ws.History.Append(ws.Profile.Id, new HistoryEntry { Database = ws.Database, Sql = sql, DurationMs = sw.Elapsed.TotalMilliseconds, Source = "sidebar" });
            return (string?)null;
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            ws.Log("Error: " + ex.Message);
            ws.History.Append(ws.Profile.Id, new HistoryEntry { Database = ws.Database, Sql = sql, DurationMs = sw.Elapsed.TotalMilliseconds, Error = true, Source = "sidebar" });
            return ex.Message;
        }
    });

    // ---- UI helpers -----------------------------------------------------------------------------------------------

    private static void DefaultSetClipboard(string text)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(30);
            }
        }
    }

    private static DestructiveOptions? ShowDestructiveDialog(IWorkspace ws, DestructiveAction action, IReadOnlyList<ScriptObject> objects)
    {
        var truncate = action == DestructiveAction.Truncate;
        var verb = truncate ? "Truncate" : "Delete";
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = objects.Count == 1 ? $"{verb} {objects[0].Target}?" : $"{verb} {objects.Count} items?",
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        if (objects.Count > 1)
        {
            var list = new TextBlock { Text = string.Join("\n", objects.Take(12).Select(o => o.Target)) + (objects.Count > 12 ? "\n…" : ""), Margin = new Thickness(0, 0, 0, 8) };
            list.SetResourceReference(TextBlock.ForegroundProperty, "c.app.mutedForeground");
            panel.Children.Add(list);
        }
        var note = new TextBlock
        {
            Text = truncate ? "All rows will be removed. This runs immediately (it is not staged)." : "The objects will be dropped. This runs immediately (it is not staged).",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "c.app.warning");
        panel.Children.Add(note);
        var cascade = new CheckBox { Content = "Cascade", Margin = new Thickness(0, 0, 0, 6) };
        panel.Children.Add(cascade);
        var restart = new CheckBox { Content = "Restart identity", Margin = new Thickness(0, 0, 0, 6), Visibility = truncate ? Visibility.Visible : Visibility.Collapsed };
        panel.Children.Add(restart);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = verb, IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        ok.SetResourceReference(FrameworkElement.StyleProperty, "AccentButton");
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        var host = Dialogs.CreateHost(panel, verb, 440, double.NaN, (ws as WorkspaceViewModel)?.Window);
        host.SizeToContent = SizeToContent.Height;
        host.ResizeMode = ResizeMode.NoResize;
        ok.Click += (_, _) => host.DialogResult = true;
        host.Loaded += (_, _) => cancel.Focus();
        return host.ShowDialog() == true ? new DestructiveOptions(cascade.IsChecked == true, restart.IsChecked == true) : null;
    }
}
