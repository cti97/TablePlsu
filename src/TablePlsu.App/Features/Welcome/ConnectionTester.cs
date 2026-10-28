using Npgsql;
using TablePlsu.App.Features.Palette;
using TablePlsu.Core.Connections;
using TablePlsu.Core.Db;

namespace TablePlsu.App.Features.Welcome;

/// <summary>What the Test button learned: the server version and (when the query worked) the server's databases.</summary>
public sealed record ConnectionProbe(string Version, IReadOnlyList<string>? Databases);

/// <summary>
/// The connection form's Test button and database dropdown. Kept in its own class so Npgsql-touching code is only
/// JIT-compiled when used. Everything goes through <see cref="DbSession"/>, so SSH tunnels, SSL and bootstrap SQL
/// behave exactly as they do for a real connection.
/// </summary>
public static class ConnectionTester
{
    /// <summary>Opens a throwaway session and returns the server version. Throws with the server's message on failure.</summary>
    public static async Task<string> TestAsync(ConnectionProfile profile, ConnectionSecrets secrets, CancellationToken ct = default) =>
        (await ProbeAsync(profile, secrets, ct).ConfigureAwait(false)).Version;

    /// <summary>
    /// Like <see cref="TestAsync"/>, and also lists the server's databases on the same session. A failure of the list
    /// query alone does not fail the test (<see cref="ConnectionProbe.Databases"/> is then null).
    /// </summary>
    public static async Task<ConnectionProbe> ProbeAsync(ConnectionProfile profile, ConnectionSecrets secrets, CancellationToken ct = default)
    {
        await using var session = Open(profile, secrets, null);
        await session.ConnectAsync(ct).ConfigureAwait(false);
        var version = session.ServerVersion ?? "(unknown version)";
        IReadOnlyList<string>? databases = null;
        try
        {
            databases = await DatabaseList.QueryNamesAsync(session, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The connection works; only the list is missing.
        }
        return new ConnectionProbe(version, databases);
    }

    /// <summary>
    /// The server's connectable, non-template databases. Connects to the profile's database; when that one does not
    /// exist (the user is still typing it), falls back to <c>postgres</c> and then <c>template1</c>.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ListDatabasesAsync(ConnectionProfile profile, ConnectionSecrets secrets, CancellationToken ct = default)
    {
        var candidates = new List<string> { profile.Database };
        foreach (var fallback in new[] { DatabaseSuggestion.MaintenanceDatabase, "template1" })
            if (!candidates.Contains(fallback)) candidates.Add(fallback);
        for (var i = 0; ; i++)
        {
            try
            {
                await using var session = Open(profile, secrets, candidates[i]);
                await session.ConnectAsync(ct).ConfigureAwait(false);
                return await DatabaseList.QueryNamesAsync(session, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (i < candidates.Count - 1 && IsMissingDatabase(ex))
            {
                // Try the next candidate.
            }
        }
    }

    /// <summary>True when the error says the database does not exist (SQLSTATE 3D000).</summary>
    public static bool IsMissingDatabase(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex is PostgresException { SqlState: PostgresErrorCodes.InvalidCatalogName }) return true;
        return false;
    }

    private static DbSession Open(ConnectionProfile profile, ConnectionSecrets secrets, string? database) =>
        new(profile, secrets, database, new DbSessionOptions { KeepAlive = false });
}
