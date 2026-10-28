using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Data;
using TablePlsu.Core.History;

namespace TablePlsu.App.Features.Results;

/// <summary>
/// A tab whose staged grid changes can be reviewed and committed (table tabs, editor result tabs): what the Preview
/// window (<see cref="TableData.ChangesPreviewWindow"/>) shows and drives.
/// </summary>
public interface IStagedChangesSource
{
    /// <summary>"schema.table", for the Preview window title.</summary>
    string ChangesTitle { get; }

    /// <summary>The statements Commit would send.</summary>
    IReadOnlyList<SqlCommandSpec> GenerateStatements();

    string? LastCommitError { get; }

    int LastFailedStatement { get; }

    /// <summary>Sends every staged statement in one transaction (through Safe Mode). True when committed.</summary>
    Task<bool> CommitCoreAsync(bool showErrors = true);

    void DiscardChanges();
}

/// <summary>
/// The commit path shared by every editable grid (spec §8 Commit): Safe Mode confirmation of the exact statements, then
/// all of them in ONE transaction on a pooled connection (UPDATE/DELETE must affect exactly one row), console log and
/// history entries. The caller locks staging for the duration and applies the outcome.
/// </summary>
public static class ChangeCommitter
{
    /// <summary>
    /// Confirms and sends <paramref name="statements"/>. Returns null when Safe Mode cancelled (nothing sent).
    /// <paramref name="sending"/> runs after confirmation, right before the statements are sent.
    /// </summary>
    public static async Task<CommitResult?> CommitAsync(IWorkspace ws, IReadOnlyList<SqlCommandSpec> statements, Action? sending = null)
    {
        if (!await ws.SafeMode.ConfirmAsync([.. statements.Select(s => s.LiteralSql)]).ConfigureAwait(true)) return null;
        sending?.Invoke();
        var session = ws.Session;
        var timeout = AppServices.IsInitialized ? AppServices.Current.Connections.CommandTimeoutSeconds : 0;
        foreach (var s in statements) ws.Log(s.LiteralSql);
        CommitResult result;
        try
        {
            result = await Task.Run(async () =>
            {
                await using var conn = await session.OpenConnectionAsync().ConfigureAwait(false);
                return await TableDataAccess.CommitAsync(conn, statements, timeout).ConfigureAwait(false);
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new CommitResult { Success = false, FailedIndex = 0, Error = ex.Message };
        }
        AppendHistory(ws, statements, result);
        if (result.Success) ws.Log($"committed {statements.Count} statement(s) in {result.Elapsed.TotalMilliseconds:0} ms");
        else ws.Log($"commit failed (rolled back) at statement {result.FailedIndex + 1}: {result.Error}");
        return result;
    }

    /// <summary>The "Commit failed" dialog: nothing was committed and the changes are still staged.</summary>
    public static void ShowFailure(IReadOnlyList<SqlCommandSpec> statements, CommitResult result)
    {
        var failing = result.FailedIndex >= 0 && result.FailedIndex < statements.Count ? statements[result.FailedIndex].LiteralSql : null;
        Dialogs.Error($"Commit failed at statement {result.FailedIndex + 1} of {statements.Count}. Nothing was committed; your changes are still staged.\n\n{result.Error}",
            "Commit failed", detail: failing);
    }

    private static void AppendHistory(IWorkspace ws, IReadOnlyList<SqlCommandSpec> statements, CommitResult result)
    {
        if (!AppServices.IsInitialized) return;
        for (var i = 0; i < statements.Count; i++)
        {
            if (!result.Success && i > result.FailedIndex) break;
            ws.History.Append(ws.Profile.Id, new HistoryEntry
            {
                Database = ws.Database,
                Sql = statements[i].LiteralSql,
                DurationMs = i == 0 ? result.Elapsed.TotalMilliseconds : 0,
                Rows = i < result.Affected.Count ? result.Affected[i] : null,
                Error = !result.Success && i == result.FailedIndex,
                Source = "commit",
            });
        }
    }
}
