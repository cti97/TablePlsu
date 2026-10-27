using System.IO;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Commands;

namespace TablePlsu.App.Features.SqlEditor;

/// <summary>
/// SQL editor tabs (spec §9/§10/§11): the <see cref="TabKinds.SqlEditor"/> factory, autosave restore when a workspace
/// opens, and the editor commands — run current/all, cancel, find/replace, comments, font zoom, toggle results,
/// open/save-as .sql, reopen closed tab.
/// </summary>
public sealed class SqlEditorFeature : IFeature
{
    public const string SqlFileFilter = "SQL files (*.sql)|*.sql|All files (*.*)|*.*";

    public void Register(FeatureContext ctx)
    {
        ctx.Tabs.Register(TabKinds.SqlEditor, (request, ws) => EditorSessionManager.For(ws).CreateNew(request.Sql, request.Title));
        ctx.OnWorkspaceOpened(ws => EditorSessionManager.For(ws).Restore());

        var c = ctx.Commands;
        // Editor scope (Target is the tab view-model when the chord came from inside the editor view).
        c.Register(CommandIds.QueryRunCurrent, x => Editor(x)!.RunCurrentAsync().Forget(ex => Report(x, ex)), HasEditor);
        c.Register(CommandIds.QueryRunAll, x => Editor(x)!.RunAllAsync().Forget(ex => Report(x, ex)), HasEditor);
        c.Register(CommandIds.EditorToggleLineComment, x => Editor(x)!.View!.ToggleLineComment(), HasView);
        c.Register(CommandIds.EditorToggleBlockComment, x => Editor(x)!.View!.ToggleBlockComment(), HasView);
        c.Register(CommandIds.EditorFontBigger, x => Editor(x)!.View!.ZoomFont(1), HasView);
        c.Register(CommandIds.EditorFontSmaller, x => Editor(x)!.View!.ZoomFont(-1), HasView);
        c.Register(CommandIds.EditorToggleResults, x => Editor(x)!.IsResultsVisible = !Editor(x)!.IsResultsVisible, HasEditor);
        c.Register(CommandIds.EditorFind, x => Editor(x)!.View!.OpenFind(replace: false), HasView);
        c.Register(CommandIds.EditorReplace, x => Editor(x)!.View!.OpenFind(replace: true), HasView);
        c.Register(CommandIds.EditorFindNext, x => Editor(x)!.View!.FindNext(backwards: false), HasView);
        c.Register(CommandIds.EditorFindPrev, x => Editor(x)!.View!.FindNext(backwards: true), HasView);
        c.Register(CommandIds.FileSaveAs, x => SaveAs(Editor(x)!), HasEditor);

        // Workspace scope.
        c.Register(CommandIds.QueryCancel, x => Editor(x)!.Cancel(), x => Editor(x)?.IsRunning == true);
        c.Register(CommandIds.TabReopenClosed, x => EditorSessionManager.For(x.Workspace!).ReopenLast(), x => x.Workspace is not null);
        c.Register(CommandIds.FileOpen, x => OpenFile(x.Workspace!), x => x.Workspace is not null);
    }

    /// <summary>The editor a command applies to: the scope target, else the workspace's active tab.</summary>
    private static SqlEditorTabViewModel? Editor(CommandContext ctx) =>
        ctx.Target as SqlEditorTabViewModel ?? ctx.Workspace?.ActiveTab as SqlEditorTabViewModel;

    private static bool HasEditor(CommandContext ctx) => Editor(ctx) is not null;

    private static bool HasView(CommandContext ctx) => Editor(ctx)?.View is not null;

    private static void Report(CommandContext ctx, Exception ex) =>
        ctx.Workspace?.ConsoleLog.Add("Run failed: " + ex.Message, Workspace.ConsoleEntryKind.Error);

    /// <summary>Ctrl+O: pick a .sql file and open it in a linked editor tab.</summary>
    public static void OpenFile(IWorkspace ws)
    {
        var path = Dialogs.OpenFile(SqlFileFilter, "Open SQL File");
        if (path is null) return;
        try
        {
            EditorSessionManager.For(ws).OpenFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Error(ex.Message, "Could not open the file");
        }
    }

    /// <summary>Ctrl+Shift+S: save the editor text to a .sql file and link the tab to it.</summary>
    public static void SaveAs(SqlEditorTabViewModel tab)
    {
        var name = tab.LinkedFilePath is { } p ? Path.GetFileName(p) : tab.DisplayTitle + ".sql";
        var path = Dialogs.SaveFile(SqlFileFilter, name, "Save SQL As");
        if (path is null) return;
        try
        {
            tab.SaveAs(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Error(ex.Message, "Could not save the file");
        }
    }
}
