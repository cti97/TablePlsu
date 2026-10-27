using Renci.SshNet;
using TablePlsu.Core.Connections;

namespace TablePlsu.Core.Db;

/// <summary>
/// SSH local port forward (SSH.NET <see cref="ForwardedPortLocal"/>) from 127.0.0.1:&lt;ephemeral&gt; to the DB host:port as
/// seen from the SSH server. Auth: password and/or private key (with passphrase). With "use key" and no key file, the
/// IdentityFile entries for the host in ~/.ssh/config, then the default ~/.ssh/id_* keys, are tried.
/// SSH agents (Pageant / OpenSSH agent) are not supported by SSH.NET.
/// </summary>
public sealed class SshTunnel : IDisposable
{
    private readonly SshClient _client;
    private readonly ForwardedPortLocal _port;

    private SshTunnel(SshClient client, ForwardedPortLocal port)
    {
        _client = client;
        _port = port;
    }

    public string LocalHost => "127.0.0.1";
    public int LocalPort => (int)_port.BoundPort;
    public bool IsConnected => _client.IsConnected && _port.IsStarted;

    public static async Task<SshTunnel> OpenAsync(ConnectionProfile profile, ConnectionSecrets secrets, TimeSpan keepAlive, CancellationToken ct = default)
    {
        if (!profile.SshEnabled || string.IsNullOrWhiteSpace(profile.SshHost))
            throw new InvalidOperationException("SSH is not enabled for this connection.");

        var host = profile.SshHost!;
        var port = profile.SshPort;
        var user = profile.SshUser;
        var configEntry = SshConfig.Find(host);
        if (configEntry is not null)
        {
            host = configEntry.HostName ?? host;
            if (configEntry.Port is { } p && profile.SshPort == 22) port = p;
            if (string.IsNullOrEmpty(user)) user = configEntry.User;
        }
        if (string.IsNullOrEmpty(user)) user = Environment.UserName;

        var methods = new List<AuthenticationMethod>();
        if (profile.SshUseKey)
        {
            var keyFiles = new List<IPrivateKeySource>();
            var paths = !string.IsNullOrWhiteSpace(profile.SshKeyFile)
                ? [profile.SshKeyFile!]
                : (configEntry?.IdentityFiles ?? []).Concat(SshConfig.DefaultKeyFiles()).Where(File.Exists).Distinct().ToList();
            foreach (var path in paths)
            {
                try
                {
                    keyFiles.Add(string.IsNullOrEmpty(secrets.SshPassphrase) ? new PrivateKeyFile(path) : new PrivateKeyFile(path, secrets.SshPassphrase));
                }
                catch (Exception ex) when (!string.IsNullOrWhiteSpace(profile.SshKeyFile))
                {
                    throw new InvalidOperationException($"Could not load SSH key '{path}': {ex.Message}", ex);
                }
                catch
                {
                    // Default keys that fail to load (e.g. need another passphrase) are skipped.
                }
            }
            if (keyFiles.Count > 0) methods.Add(new PrivateKeyAuthenticationMethod(user, [.. keyFiles]));
        }
        if (!string.IsNullOrEmpty(secrets.SshPassword)) methods.Add(new PasswordAuthenticationMethod(user, secrets.SshPassword));
        if (methods.Count == 0) methods.Add(new NoneAuthenticationMethod(user));

        var info = new ConnectionInfo(host, port, user, [.. methods]) { Timeout = TimeSpan.FromSeconds(15) };
        var client = new SshClient(info) { KeepAliveInterval = keepAlive };
        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);
            var forward = new ForwardedPortLocal("127.0.0.1", 0, profile.Host, (uint)profile.Port);
            client.AddForwardedPort(forward);
            forward.Start();
            return new SshTunnel(client, forward);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        try { if (_port.IsStarted) _port.Stop(); } catch { /* ignore */ }
        try { _client.Disconnect(); } catch { /* ignore */ }
        _port.Dispose();
        _client.Dispose();
    }
}

/// <summary>Minimal ~/.ssh/config reader: Host patterns with HostName, User, Port and IdentityFile.</summary>
public sealed record SshConfigEntry(string? HostName, string? User, int? Port, IReadOnlyList<string> IdentityFiles);

public static class SshConfig
{
    public static string SshDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

    public static IEnumerable<string> DefaultKeyFiles() =>
        new[] { "id_ed25519", "id_ecdsa", "id_rsa" }.Select(n => Path.Combine(SshDir, n));

    public static SshConfigEntry? Find(string alias, string? configText = null)
    {
        if (configText is null)
        {
            var file = Path.Combine(SshDir, "config");
            if (!File.Exists(file)) return null;
            try { configText = File.ReadAllText(file); }
            catch (IOException) { return null; }
        }

        string? hostName = null, user = null;
        int? port = null;
        var identities = new List<string>();
        var matched = false;
        var inMatch = false;
        foreach (var raw in configText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var sep = line.IndexOfAny([' ', '\t', '=']);
            if (sep < 0) continue;
            var key = line[..sep].Trim();
            var value = line[(sep + 1)..].Trim().TrimStart('=').Trim().Trim('"');
            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                inMatch = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(p => Glob(p, alias));
                matched |= inMatch;
                continue;
            }
            if (key.Equals("Match", StringComparison.OrdinalIgnoreCase)) { inMatch = false; continue; }
            if (!inMatch) continue;
            // First value wins, as in OpenSSH.
            if (key.Equals("HostName", StringComparison.OrdinalIgnoreCase)) hostName ??= value;
            else if (key.Equals("User", StringComparison.OrdinalIgnoreCase)) user ??= value;
            else if (key.Equals("Port", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var p)) port ??= p;
            else if (key.Equals("IdentityFile", StringComparison.OrdinalIgnoreCase)) identities.Add(ExpandHome(value));
        }
        return matched ? new SshConfigEntry(hostName, user, port, identities) : null;
    }

    private static string ExpandHome(string path) =>
        path.StartsWith('~') ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[1..].TrimStart('/', '\\')) : path;

    private static bool Glob(string pattern, string text)
    {
        if (pattern.StartsWith('!')) return false;
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(text, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
