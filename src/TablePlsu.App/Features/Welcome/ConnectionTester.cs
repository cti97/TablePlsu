using TablePlsu.Core.Connections;
using TablePlsu.Core.Db;

namespace TablePlsu.App.Features.Welcome;

/// <summary>
/// The connection form's Test button. Kept in its own class so Npgsql-touching code is only JIT-compiled when used.
/// </summary>
public static class ConnectionTester
{
    /// <summary>Opens a throwaway session and returns the server version. Throws with the server's message on failure.</summary>
    public static async Task<string> TestAsync(ConnectionProfile profile, ConnectionSecrets secrets, CancellationToken ct = default)
    {
        await using var session = new DbSession(profile, secrets, null, new DbSessionOptions { KeepAlive = false });
        await session.ConnectAsync(ct).ConfigureAwait(false);
        return session.ServerVersion ?? "(unknown version)";
    }
}
