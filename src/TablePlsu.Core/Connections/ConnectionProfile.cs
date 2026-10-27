using System.Text.Json.Serialization;
using TablePlsu.Core.Settings;

namespace TablePlsu.Core.Connections;

/// <summary>How a password (DB or SSH) is obtained on connect.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<PasswordMode>))]
public enum PasswordMode
{
    /// <summary>Stored with DPAPI in secrets.dat (default).</summary>
    Store,
    /// <summary>Prompted on every connect, never persisted.</summary>
    Ask,
    None,
    /// <summary>Output of <see cref="ConnectionProfile.PasswordCommand"/> (P1).</summary>
    Command,
}

/// <summary>Mirrors Npgsql's SslMode without referencing Npgsql (startup must not load Npgsql types).</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<SslModeOption>))]
public enum SslModeOption { Disable, Allow, Prefer, Require, VerifyCA, VerifyFull }

/// <summary>Safe Mode level per connection (spec §8).</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<SafeModeLevel>))]
public enum SafeModeLevel
{
    Silent,
    /// <summary>Confirm every query.</summary>
    Alert1,
    /// <summary>Confirm everything except reads.</summary>
    Alert2,
    /// <summary>DB password for every query.</summary>
    Safe1,
    /// <summary>DB password except reads.</summary>
    Safe2,
}

public enum SecretKind { DbPassword, SshPassword, SshPassphrase }

/// <summary>Resolved secrets handed to <c>DbSession</c> (from SecretStore, a prompt, or a command).</summary>
public sealed record ConnectionSecrets(string? Password, string? SshPassword, string? SshPassphrase)
{
    public static ConnectionSecrets None { get; } = new(null, null, null);
}

/// <summary>A saved connection. Holds no secrets; those live in <see cref="SecretStore"/> keyed by <see cref="Id"/>.</summary>
public sealed record ConnectionProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    /// <summary>Status colour "#RRGGBB" tinting the workspace toolbar, or null.</summary>
    public string? Color { get; init; }
    /// <summary>Tag name (keys of settings.tags), or null for NO TAG.</summary>
    public string? Tag { get; init; }
    public SafeModeLevel SafeMode { get; init; } = SafeModeLevel.Silent;

    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 5432;
    public string User { get; init; } = "postgres";
    public string Database { get; init; } = "postgres";
    public PasswordMode PasswordMode { get; init; } = PasswordMode.Store;
    public string? PasswordCommand { get; init; }

    public SslModeOption SslMode { get; init; } = SslModeOption.Prefer;
    public string? SslClientKeyFile { get; init; }
    public string? SslClientCertFile { get; init; }
    public string? SslRootCertFile { get; init; }

    public bool SshEnabled { get; init; }
    public string? SshHost { get; init; }
    public int SshPort { get; init; } = 22;
    public string? SshUser { get; init; }
    public PasswordMode SshPasswordMode { get; init; } = PasswordMode.Store;
    public bool SshUseKey { get; init; }
    /// <summary>Private key path; blank with <see cref="SshUseKey"/> falls back to ~/.ssh/config and default keys.</summary>
    public string? SshKeyFile { get; init; }

    /// <summary>SQL run on every physical (re)connect, e.g. "SET search_path …".</summary>
    public string? Bootstrap { get; init; }

    /// <summary>Remembered editor row limit (null = settings.editor.defaultRowLimit, 0 = no limit).</summary>
    public int? RowLimit { get; init; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"{User}@{Host}/{Database}" : Name;
}
