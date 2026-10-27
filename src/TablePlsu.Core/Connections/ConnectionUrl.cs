using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text;

namespace TablePlsu.Core.Connections;

/// <summary>A parsed connection URL: a profile ready for review plus any secrets and unconsumed query options
/// (schema, table, column, operation, value, condition, lazyload… for URL launch).</summary>
public sealed record ParsedConnectionUrl(
    ConnectionProfile Profile,
    string? Password,
    string? SshPassword,
    IReadOnlyDictionary<string, string> Options);

/// <summary>
/// Parses and formats <c>postgresql://user:pass@host:port/db?options</c> (also <c>postgres://</c>) and the SSH variant
/// <c>postgresql+ssh://ssh_user:ssh_pass@ssh_host:ssh_port/user:pass@host:port/db?options</c>.
/// Recognised options: <c>sslmode</c>, <c>name</c>, <c>statusColor</c> (hex, '#' optional), <c>env</c>/<c>tag</c>,
/// <c>safeModeLevel</c> (0–4 or silent/alert1/alert2/safe1/safe2).
/// </summary>
public static class ConnectionUrl
{
    private static readonly string[] Consumed = ["sslmode", "name", "statusColor", "env", "tag", "safeModeLevel"];

    public static ParsedConnectionUrl Parse(string url) =>
        TryParse(url, out var result, out var error) ? result : throw new FormatException(error);

    public static bool TryParse(string? url, [NotNullWhen(true)] out ParsedConnectionUrl? result, [NotNullWhen(false)] out string? error)
    {
        result = null;
        error = null;
        if (string.IsNullOrWhiteSpace(url)) { error = "The URL is empty."; return false; }
        var s = url.Trim();

        bool ssh;
        string rest;
        if (TryStrip(s, "postgresql+ssh://", out rest) || TryStrip(s, "postgres+ssh://", out rest)) ssh = true;
        else if (TryStrip(s, "postgresql://", out rest) || TryStrip(s, "postgres://", out rest)) ssh = false;
        else { error = "The URL must start with postgresql://, postgres:// or postgresql+ssh://."; return false; }

        var query = "";
        var q = rest.IndexOf('?');
        if (q >= 0)
        {
            query = rest[(q + 1)..];
            rest = rest[..q];
        }

        var profile = new ConnectionProfile();
        string? sshPassword = null;
        if (ssh)
        {
            var slash = rest.IndexOf('/');
            if (slash < 0) { error = "The SSH URL needs '/user@host/db' after the SSH host."; return false; }
            if (!TryParseAuthority(rest[..slash], 22, out var sshUser, out sshPassword, out var sshHost, out var sshPort, out error)) return false;
            profile = profile with
            {
                SshEnabled = true,
                SshHost = sshHost,
                SshPort = sshPort,
                SshUser = sshUser,
                SshPasswordMode = sshPassword is null ? PasswordMode.None : PasswordMode.Store,
                SshUseKey = sshPassword is null,
            };
            rest = rest[(slash + 1)..];
        }

        var dbSlash = rest.IndexOf('/');
        var authority = dbSlash < 0 ? rest : rest[..dbSlash];
        var database = dbSlash < 0 ? "" : Uri.UnescapeDataString(rest[(dbSlash + 1)..]);
        if (!TryParseAuthority(authority, 5432, out var user, out var password, out var host, out var port, out error)) return false;

        profile = profile with
        {
            Host = host,
            Port = port,
            User = string.IsNullOrEmpty(user) ? "postgres" : user,
            Database = string.IsNullOrEmpty(database) ? "postgres" : database,
            PasswordMode = password is null ? PasswordMode.None : PasswordMode.Store,
        };

        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = WebUtility.UrlDecode(eq < 0 ? pair : pair[..eq]);
            var value = eq < 0 ? "" : WebUtility.UrlDecode(pair[(eq + 1)..]);
            options[key] = value;
        }

        if (options.TryGetValue("sslmode", out var ssl))
        {
            if (!TryParseSslMode(ssl, out var mode)) { error = $"Unknown sslmode '{ssl}'."; return false; }
            profile = profile with { SslMode = mode };
        }
        if (options.TryGetValue("name", out var name)) profile = profile with { Name = name };
        if (options.TryGetValue("statusColor", out var color) && NormalizeColor(color) is { } c) profile = profile with { Color = c };
        if (options.TryGetValue("env", out var env) && env.Length > 0) profile = profile with { Tag = env };
        else if (options.TryGetValue("tag", out var tag) && tag.Length > 0) profile = profile with { Tag = tag };
        if (options.TryGetValue("safeModeLevel", out var safe) && TryParseSafeMode(safe, out var level)) profile = profile with { SafeMode = level };

        foreach (var k in Consumed) options.Remove(k);
        result = new ParsedConnectionUrl(profile, password, sshPassword, options);
        return true;
    }

    /// <summary>Formats a profile as a URL. Passwords are included only when passed (callers confirm first).</summary>
    public static string Format(ConnectionProfile profile, string? password = null, string? sshPassword = null)
    {
        var sb = new StringBuilder();
        if (profile.SshEnabled)
        {
            sb.Append("postgresql+ssh://");
            AppendAuthority(sb, profile.SshUser, sshPassword, profile.SshHost ?? "", profile.SshPort);
            sb.Append('/');
        }
        else
        {
            sb.Append("postgresql://");
        }
        AppendAuthority(sb, profile.User, password, profile.Host, profile.Port);
        sb.Append('/').Append(Uri.EscapeDataString(profile.Database));

        var query = new List<string>();
        if (profile.SslMode != SslModeOption.Prefer) query.Add("sslmode=" + SslModeText(profile.SslMode));
        if (!string.IsNullOrEmpty(profile.Name)) query.Add("name=" + Uri.EscapeDataString(profile.Name));
        if (NormalizeColor(profile.Color) is { } color) query.Add("statusColor=" + color[1..]);
        if (!string.IsNullOrEmpty(profile.Tag)) query.Add("env=" + Uri.EscapeDataString(profile.Tag));
        if (profile.SafeMode != SafeModeLevel.Silent) query.Add("safeModeLevel=" + (int)profile.SafeMode);
        if (query.Count > 0) sb.Append('?').AppendJoin('&', query);
        return sb.ToString();
    }

    public static string SslModeText(SslModeOption mode) => mode switch
    {
        SslModeOption.Disable => "disable",
        SslModeOption.Allow => "allow",
        SslModeOption.Prefer => "prefer",
        SslModeOption.Require => "require",
        SslModeOption.VerifyCA => "verify-ca",
        SslModeOption.VerifyFull => "verify-full",
        _ => "prefer",
    };

    public static bool TryParseSslMode(string text, out SslModeOption mode)
    {
        foreach (var m in Enum.GetValues<SslModeOption>())
            if (string.Equals(SslModeText(m), text, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m.ToString(), text, StringComparison.OrdinalIgnoreCase))
            {
                mode = m;
                return true;
            }
        mode = SslModeOption.Prefer;
        return false;
    }

    private static bool TryParseSafeMode(string text, out SafeModeLevel level)
    {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n is >= 0 and <= 4)
        {
            level = (SafeModeLevel)n;
            return true;
        }
        return Enum.TryParse(text, ignoreCase: true, out level) && !int.TryParse(text, out _);
    }

    private static string? NormalizeColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color)) return null;
        var hex = color.Trim().TrimStart('#');
        if (hex.Length != 6 || !hex.All(char.IsAsciiHexDigit)) return null;
        return "#" + hex.ToUpperInvariant();
    }

    private static void AppendAuthority(StringBuilder sb, string? user, string? password, string host, int port)
    {
        if (!string.IsNullOrEmpty(user))
        {
            sb.Append(Uri.EscapeDataString(user));
            if (!string.IsNullOrEmpty(password)) sb.Append(':').Append(Uri.EscapeDataString(password));
            sb.Append('@');
        }
        sb.Append(host.Contains(':') ? "[" + host + "]" : host).Append(':').Append(port.ToString(CultureInfo.InvariantCulture));
    }

    private static bool TryStrip(string s, string prefix, out string rest)
    {
        if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            rest = s[prefix.Length..];
            return true;
        }
        rest = "";
        return false;
    }

    private static bool TryParseAuthority(string authority, int defaultPort, out string? user, out string? password,
        out string host, out int port, [NotNullWhen(false)] out string? error)
    {
        user = password = null;
        host = "";
        port = defaultPort;
        error = null;

        var at = authority.LastIndexOf('@');
        var hostPort = authority;
        if (at >= 0)
        {
            var userInfo = authority[..at];
            hostPort = authority[(at + 1)..];
            var colon = userInfo.IndexOf(':');
            user = Uri.UnescapeDataString(colon < 0 ? userInfo : userInfo[..colon]);
            if (colon >= 0) password = Uri.UnescapeDataString(userInfo[(colon + 1)..]);
        }

        string? portText = null;
        if (hostPort.StartsWith('['))
        {
            var close = hostPort.IndexOf(']');
            if (close < 0) { error = "Unterminated IPv6 host."; return false; }
            host = hostPort[1..close];
            var after = hostPort[(close + 1)..];
            if (after.StartsWith(':')) portText = after[1..];
            else if (after.Length > 0) { error = $"Unexpected '{after}' after host."; return false; }
        }
        else
        {
            var colon = hostPort.LastIndexOf(':');
            host = colon < 0 ? hostPort : hostPort[..colon];
            if (colon >= 0) portText = hostPort[(colon + 1)..];
        }

        host = Uri.UnescapeDataString(host);
        if (string.IsNullOrWhiteSpace(host) || host.Contains(' ')) { error = "The URL has no valid host."; return false; }
        if (portText is { Length: > 0 })
        {
            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)
            {
                error = $"'{portText}' is not a valid port.";
                return false;
            }
        }
        return true;
    }
}
