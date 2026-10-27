using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Connections;
using TablePlsu.Core.Themes;

namespace TablePlsu.App.Features.Welcome;

/// <summary>A choice in a form dropdown.</summary>
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Create/edit connection form (spec §1): general (name, status colour, tag, Safe Mode), server (host, port, user,
/// password + storage mode, database), SSL (mode + key/cert/CA files), SSH tunnel, bootstrap SQL.
/// Test shows the server version or the error; Save stores the profile and DPAPI secrets; Connect saves then opens.
/// </summary>
public sealed partial class ConnectionFormViewModel : ObservableObject
{
    private readonly string _id;
    private readonly int? _rowLimit;
    private CancellationTokenSource? _testCts;

    public ConnectionFormViewModel(ConnectionProfile? profile, bool allowConnect)
    {
        AllowConnect = allowConnect;
        IsNew = profile is null || (AppServices.IsInitialized && AppServices.Connections.Get(profile.Id) is null);
        var defaultSafeMode = AppServices.IsInitialized ? AppServices.Current.Security.DefaultSafeMode : SafeModeLevel.Silent;
        var p = profile ?? new ConnectionProfile { SafeMode = defaultSafeMode };
        _id = p.Id;
        _rowLimit = p.RowLimit;
        Load(p);
        if (!IsNew && AppServices.IsInitialized)
        {
            Password = (p.PasswordMode == PasswordMode.Store ? AppServices.Secrets.Get(p.Id, SecretKind.DbPassword) : null) ?? "";
            SshPassword = (p.SshPasswordMode == PasswordMode.Store ? AppServices.Secrets.Get(p.Id, SecretKind.SshPassword) : null) ?? "";
            SshPassphrase = AppServices.Secrets.Get(p.Id, SecretKind.SshPassphrase) ?? "";
        }

        Tags = AppServices.IsInitialized ? ["", .. AppServices.Current.Tags.Keys] : [""];
    }

    public bool IsNew { get; }

    public bool AllowConnect { get; }

    public string Heading => IsNew ? "New Connection" : "Edit Connection";

    public event Action<ConnectionProfile>? Saved;

    public event Action<ConnectionProfile, ConnectionSecrets>? ConnectRequested;

    public event Action? Cancelled;

    // ---- choices ------------------------------------------------------------------------------------------------

    public static IReadOnlyList<Choice<PasswordMode>> PasswordModes { get; } =
    [
        new(PasswordMode.Store, "Store in keychain (DPAPI)"),
        new(PasswordMode.Ask, "Ask every time"),
        new(PasswordMode.None, "No password"),
        new(PasswordMode.Command, "Command line"),
    ];

    public static IReadOnlyList<Choice<PasswordMode>> SshPasswordModes { get; } =
    [
        new(PasswordMode.Store, "Store in keychain (DPAPI)"),
        new(PasswordMode.Ask, "Ask every time"),
        new(PasswordMode.None, "No password"),
    ];

    public static IReadOnlyList<Choice<SslModeOption>> SslModes { get; } =
    [
        new(SslModeOption.Disable, "Disable"),
        new(SslModeOption.Allow, "Allow"),
        new(SslModeOption.Prefer, "Prefer"),
        new(SslModeOption.Require, "Require"),
        new(SslModeOption.VerifyCA, "Verify-CA"),
        new(SslModeOption.VerifyFull, "Verify-Full"),
    ];

    public static IReadOnlyList<Choice<SafeModeLevel>> SafeModeLevels { get; } =
    [
        new(SafeModeLevel.Silent, "Silent"),
        new(SafeModeLevel.Alert1, "Alert 1 — confirm every query"),
        new(SafeModeLevel.Alert2, "Alert 2 — confirm except reads"),
        new(SafeModeLevel.Safe1, "Safe 1 — password for every query"),
        new(SafeModeLevel.Safe2, "Safe 2 — password except reads"),
    ];

    /// <summary>Status colour presets (the hex box accepts anything).</summary>
    public static IReadOnlyList<string> ColorPresets { get; } =
        ["", "#8E8E93", "#007AFF", "#34C759", "#FFCC00", "#FF9500", "#FF3B30", "#AF52DE", "#5AC8FA"];

    public IReadOnlyList<string> Tags { get; }

    // ---- fields -------------------------------------------------------------------------------------------------

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Color { get; set; } = "";
    [ObservableProperty] public partial string Tag { get; set; } = "";
    [ObservableProperty] public partial SafeModeLevel SafeMode { get; set; }

    [ObservableProperty] public partial string Host { get; set; } = "";
    [ObservableProperty] public partial string Port { get; set; } = "";
    [ObservableProperty] public partial string User { get; set; } = "";
    [ObservableProperty] public partial string Password { get; set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPasswordEnabled), nameof(IsPasswordCommand))]
    public partial PasswordMode PasswordMode { get; set; }
    [ObservableProperty] public partial string PasswordCommand { get; set; } = "";
    [ObservableProperty] public partial string Database { get; set; } = "";

    [ObservableProperty] public partial SslModeOption SslMode { get; set; }
    [ObservableProperty] public partial string SslClientKeyFile { get; set; } = "";
    [ObservableProperty] public partial string SslClientCertFile { get; set; } = "";
    [ObservableProperty] public partial string SslRootCertFile { get; set; } = "";

    [ObservableProperty] public partial bool SshEnabled { get; set; }
    [ObservableProperty] public partial string SshHost { get; set; } = "";
    [ObservableProperty] public partial string SshPort { get; set; } = "22";
    [ObservableProperty] public partial string SshUser { get; set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSshPasswordEnabled))]
    public partial PasswordMode SshPasswordMode { get; set; }
    [ObservableProperty] public partial string SshPassword { get; set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSshPasswordEnabled))]
    public partial bool SshUseKey { get; set; }
    [ObservableProperty] public partial string SshKeyFile { get; set; } = "";
    [ObservableProperty] public partial string SshPassphrase { get; set; } = "";

    [ObservableProperty] public partial string Bootstrap { get; set; } = "";

    public bool IsPasswordEnabled => PasswordMode == PasswordMode.Store;

    public bool IsPasswordCommand => PasswordMode == PasswordMode.Command;

    public bool IsSshPasswordEnabled => !SshUseKey && SshPasswordMode == PasswordMode.Store;

    // ---- status -------------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand), nameof(ConnectCommand))]
    public partial bool IsTesting { get; private set; }

    [ObservableProperty] public partial string? StatusMessage { get; private set; }

    /// <summary>True = last test succeeded, false = failed, null = no result.</summary>
    [ObservableProperty] public partial bool? StatusOk { get; private set; }

    // ---- load / build -------------------------------------------------------------------------------------------

    private void Load(ConnectionProfile p)
    {
        Name = p.Name;
        Color = p.Color ?? "";
        Tag = p.Tag ?? "";
        SafeMode = p.SafeMode;
        Host = p.Host;
        Port = p.Port.ToString(CultureInfo.InvariantCulture);
        User = p.User;
        PasswordMode = p.PasswordMode;
        PasswordCommand = p.PasswordCommand ?? "";
        Database = p.Database;
        SslMode = p.SslMode;
        SslClientKeyFile = p.SslClientKeyFile ?? "";
        SslClientCertFile = p.SslClientCertFile ?? "";
        SslRootCertFile = p.SslRootCertFile ?? "";
        SshEnabled = p.SshEnabled;
        SshHost = p.SshHost ?? "";
        SshPort = p.SshPort.ToString(CultureInfo.InvariantCulture);
        SshUser = p.SshUser ?? "";
        SshPasswordMode = p.SshPasswordMode;
        SshUseKey = p.SshUseKey;
        SshKeyFile = p.SshKeyFile ?? "";
        Bootstrap = p.Bootstrap ?? "";
    }

    /// <summary>Pre-fills the form from an imported URL (kept for review; nothing is saved yet).</summary>
    public void ApplyParsedUrl(ParsedConnectionUrl parsed)
    {
        Load(parsed.Profile with { Id = _id });
        Password = parsed.Password ?? "";
        SshPassword = parsed.SshPassword ?? "";
        StatusOk = null;
        StatusMessage = "Imported from URL. Review and save.";
    }

    /// <summary>Validates the fields and builds the profile. Returns null and sets <see cref="StatusMessage"/> when invalid.</summary>
    public ConnectionProfile? BuildProfile()
    {
        string? error = null;
        if (string.IsNullOrWhiteSpace(Host)) error = "Host is required.";
        else if (!TryPort(Port, out var port)) error = "Port must be a number between 1 and 65535.";
        else if (!string.IsNullOrWhiteSpace(Color) && !ThemeColor.TryParse(Color.Trim(), out _)) error = "Status colour must look like #RRGGBB.";
        else if (SshEnabled && string.IsNullOrWhiteSpace(SshHost)) error = "SSH host is required.";
        else if (SshEnabled && !TryPort(SshPort, out _)) error = "SSH port must be a number between 1 and 65535.";
        else if (!string.IsNullOrWhiteSpace(SshKeyFile) && SshKeyFile.EndsWith(".pub", StringComparison.OrdinalIgnoreCase))
            error = "Pick the private key, not the .pub file.";
        if (error is not null)
        {
            StatusOk = false;
            StatusMessage = error;
            return null;
        }
        TryPort(Port, out var dbPort);
        TryPort(SshPort, out var sshPort);
        return new ConnectionProfile
        {
            Id = _id,
            Name = Name.Trim(),
            Color = NullIfEmpty(Color),
            Tag = NullIfEmpty(Tag),
            SafeMode = SafeMode,
            Host = Host.Trim(),
            Port = dbPort,
            User = User.Trim(),
            Database = string.IsNullOrWhiteSpace(Database) ? "postgres" : Database.Trim(),
            PasswordMode = PasswordMode,
            PasswordCommand = NullIfEmpty(PasswordCommand),
            SslMode = SslMode,
            SslClientKeyFile = NullIfEmpty(SslClientKeyFile),
            SslClientCertFile = NullIfEmpty(SslClientCertFile),
            SslRootCertFile = NullIfEmpty(SslRootCertFile),
            SshEnabled = SshEnabled,
            SshHost = NullIfEmpty(SshHost),
            SshPort = sshPort == 0 ? 22 : sshPort,
            SshUser = NullIfEmpty(SshUser),
            SshPasswordMode = SshPasswordMode,
            SshUseKey = SshUseKey,
            SshKeyFile = NullIfEmpty(SshKeyFile),
            Bootstrap = string.IsNullOrWhiteSpace(Bootstrap) ? null : Bootstrap,
            RowLimit = _rowLimit,
        };
    }

    /// <summary>Secrets from the form fields; "Ask" modes prompt now (null when cancelled).</summary>
    public ConnectionSecrets? BuildSecrets(ConnectionProfile p)
    {
        var password = p.PasswordMode switch
        {
            PasswordMode.Store => NullIfEmpty(Password),
            PasswordMode.Ask => Dialogs.PromptPassword("Password", $"Password for {p.User}@{p.Host}:"),
            _ => null,
        };
        if (p.PasswordMode == PasswordMode.Ask && password is null) return null;
        string? sshPassword = null;
        if (p.SshEnabled && !p.SshUseKey)
        {
            sshPassword = p.SshPasswordMode switch
            {
                PasswordMode.Store => NullIfEmpty(SshPassword),
                PasswordMode.Ask => Dialogs.PromptPassword("SSH password", $"SSH password for {p.SshUser}@{p.SshHost}:"),
                _ => null,
            };
            if (p.SshPasswordMode == PasswordMode.Ask && sshPassword is null) return null;
        }
        return new ConnectionSecrets(password, sshPassword, p.SshEnabled && p.SshUseKey ? NullIfEmpty(SshPassphrase) : null);
    }

    // ---- commands -----------------------------------------------------------------------------------------------

    private bool CanTest() => !IsTesting;

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestAsync()
    {
        if (BuildProfile() is not { } profile) return;
        if (profile.PasswordMode == PasswordMode.Command)
        {
            try
            {
                var pw = await SecretResolver.RunPasswordCommandAsync(profile.PasswordCommand);
                Password = pw ?? "";
            }
            catch (Exception ex)
            {
                StatusOk = false;
                StatusMessage = ex.Message;
                return;
            }
        }
        // Command mode: the command's output is now in Password, so build secrets as if stored.
        var secrets = BuildSecrets(profile.PasswordMode == PasswordMode.Command ? profile with { PasswordMode = PasswordMode.Store } : profile);
        if (secrets is null) return;

        _testCts?.Cancel();
        _testCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        IsTesting = true;
        StatusOk = null;
        StatusMessage = "Testing…";
        try
        {
            var version = await Task.Run(() => ConnectionTester.TestAsync(profile, secrets, _testCts.Token));
            StatusOk = true;
            StatusMessage = $"Connected. PostgreSQL {version}";
        }
        catch (OperationCanceledException)
        {
            StatusOk = false;
            StatusMessage = "Timed out.";
        }
        catch (Exception ex)
        {
            StatusOk = false;
            StatusMessage = ex.InnerException is { } inner && ex.Message.Length < 20 ? inner.Message : ex.Message;
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private void Save()
    {
        if (SaveCore() is { } profile) Saved?.Invoke(profile);
    }

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task ConnectAsync()
    {
        if (SaveCore() is not { } profile) return;
        ConnectionSecrets? secrets;
        try
        {
            secrets = profile.PasswordMode == PasswordMode.Store || profile.PasswordMode == PasswordMode.None
                ? BuildSecrets(profile)
                : await SecretResolver.ResolveAsync(profile);
        }
        catch (Exception ex)
        {
            StatusOk = false;
            StatusMessage = ex.Message;
            return;
        }
        if (secrets is null) return;
        ConnectRequested?.Invoke(profile, secrets);
    }

    [RelayCommand]
    private void Cancel()
    {
        _testCts?.Cancel();
        Cancelled?.Invoke();
    }

    [RelayCommand]
    private void ImportUrl()
    {
        var url = Dialogs.PromptText("Import from URL", "Paste a postgresql:// or postgresql+ssh:// URL:");
        if (string.IsNullOrWhiteSpace(url)) return;
        if (ConnectionUrl.TryParse(url.Trim(), out var parsed, out var error)) ApplyParsedUrl(parsed);
        else
        {
            StatusOk = false;
            StatusMessage = error;
        }
    }

    [RelayCommand]
    private void PickColor(string? hex) => Color = hex ?? "";

    [RelayCommand]
    private void Browse(string? field)
    {
        const string keyFilter = "Key files|*.key;*.pem;*.pk8|All files|*.*";
        const string certFilter = "Certificates|*.crt;*.pem;*.cer|All files|*.*";
        switch (field)
        {
            case "sslKey":
                SslClientKeyFile = Dialogs.OpenFile(keyFilter, "Client Key", initialPath: SslClientKeyFile) ?? SslClientKeyFile;
                break;
            case "sslCert":
                SslClientCertFile = Dialogs.OpenFile(certFilter, "Client Certificate", initialPath: SslClientCertFile) ?? SslClientCertFile;
                break;
            case "sslRoot":
                SslRootCertFile = Dialogs.OpenFile(certFilter, "CA Certificate", initialPath: SslRootCertFile) ?? SslRootCertFile;
                break;
            case "sshKey":
                var initial = string.IsNullOrEmpty(SshKeyFile)
                    ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "id_ed25519")
                    : SshKeyFile;
                var picked = Dialogs.OpenFile("Private keys|*.*", "Import private key", initialPath: initial);
                if (picked is not null && picked.EndsWith(".pub", StringComparison.OrdinalIgnoreCase))
                {
                    StatusOk = false;
                    StatusMessage = "That is a public key (.pub). Pick the private key.";
                    return;
                }
                SshKeyFile = picked ?? SshKeyFile;
                break;
        }
    }

    /// <summary>Validates, saves the profile and its DPAPI secrets. Null when invalid.</summary>
    private ConnectionProfile? SaveCore()
    {
        if (BuildProfile() is not { } profile) return null;
        var store = AppServices.Connections;
        var secrets = AppServices.Secrets;
        store.Save(profile);
        secrets.Set(profile.Id, SecretKind.DbPassword, profile.PasswordMode == PasswordMode.Store ? NullIfEmpty(Password) : null);
        secrets.Set(profile.Id, SecretKind.SshPassword,
            profile.SshEnabled && !profile.SshUseKey && profile.SshPasswordMode == PasswordMode.Store ? NullIfEmpty(SshPassword) : null);
        secrets.Set(profile.Id, SecretKind.SshPassphrase, profile.SshEnabled && profile.SshUseKey ? NullIfEmpty(SshPassphrase) : null);
        var err = store.LastError ?? secrets.LastError;
        if (err is not null)
        {
            StatusOk = false;
            StatusMessage = "Saved with a problem: " + err;
        }
        return profile;
    }

    private static bool TryPort(string text, out int port) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
