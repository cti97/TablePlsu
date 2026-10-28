using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
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

public enum DatabaseItemKind { Database, Loading, Message }

/// <summary>A row of the Database dropdown: a database name, or a disabled "Loading…" / message row.</summary>
public sealed record DatabaseItem(string Text, DatabaseItemKind Kind)
{
    public bool IsDatabase => Kind == DatabaseItemKind.Database;

    public override string ToString() => Text;
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
        if (TryBuildProfile(out var profile, out var error)) return profile;
        StatusOk = false;
        StatusMessage = error;
        return null;
    }

    /// <summary>Validates the fields and builds the profile without touching the status line.</summary>
    private bool TryBuildProfile([NotNullWhen(true)] out ConnectionProfile? profile, out string? error)
    {
        profile = null;
        error = null;
        if (string.IsNullOrWhiteSpace(Host)) error = "Host is required.";
        else if (!TryPort(Port, out var port)) error = "Port must be a number between 1 and 65535.";
        else if (!string.IsNullOrWhiteSpace(Color) && !ThemeColor.TryParse(Color.Trim(), out _)) error = "Status colour must look like #RRGGBB.";
        else if (SshEnabled && string.IsNullOrWhiteSpace(SshHost)) error = "SSH host is required.";
        else if (SshEnabled && !TryPort(SshPort, out _)) error = "SSH port must be a number between 1 and 65535.";
        else if (!string.IsNullOrWhiteSpace(SshKeyFile) && SshKeyFile.EndsWith(".pub", StringComparison.OrdinalIgnoreCase))
            error = "Pick the private key, not the .pub file.";
        if (error is not null) return false;
        TryPort(Port, out var dbPort);
        TryPort(SshPort, out var sshPort);
        profile = new ConnectionProfile
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
        return true;
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
        var token = _testCts.Token;
        var generation = _serverGeneration;
        IsTesting = true;
        StatusOk = null;
        StatusMessage = "Testing…";
        try
        {
            var probe = await Task.Run(() => ConnectionTester.ProbeAsync(profile, secrets, token));
            StatusOk = true;
            StatusMessage = $"Connected. PostgreSQL {probe.Version}";
            // The same session listed the databases: fill the dropdown and maybe hint (unless the server fields changed meanwhile).
            if (generation == _serverGeneration && probe.Databases is { } databases)
            {
                _dbListCts?.Cancel();
                _dbListCts = null;
                ShowDatabases(databases);
                _testedDatabases = databases;
                UpdateSuggestion();
            }
        }
        catch (OperationCanceledException)
        {
            StatusOk = false;
            StatusMessage = "Timed out.";
        }
        catch (Exception ex)
        {
            StatusOk = false;
            StatusMessage = ErrorText(ex);
            // Wrong database name: the server is fine, so list what is there to pick from.
            if (generation == _serverGeneration && ConnectionTester.IsMissingDatabase(ex))
                DatabaseListTask = FetchDatabasesAsync(profile, secrets);
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

    // ---- database dropdown and hint ------------------------------------------------------------------------------

    /// <summary>Fields that identify the server and login: editing one makes the database list stale.</summary>
    private static readonly HashSet<string> ServerFields =
    [
        nameof(Host), nameof(Port), nameof(User), nameof(Password), nameof(PasswordMode), nameof(PasswordCommand),
        nameof(SslMode), nameof(SslClientKeyFile), nameof(SslClientCertFile), nameof(SslRootCertFile),
        nameof(SshEnabled), nameof(SshHost), nameof(SshPort), nameof(SshUser), nameof(SshPasswordMode), nameof(SshPassword),
        nameof(SshUseKey), nameof(SshKeyFile), nameof(SshPassphrase), nameof(Bootstrap),
    ];

    private CancellationTokenSource? _dbListCts;
    private bool _dbListLoaded;
    private int _serverGeneration;
    private IReadOnlyList<string>? _testedDatabases;

    /// <summary>
    /// The Database dropdown: database names from the server, or one disabled row ("Loading…", an error, a hint).
    /// Empty until a Test succeeds or the dropdown is opened.
    /// </summary>
    public ObservableCollection<DatabaseItem> DatabaseItems { get; } = [];

    /// <summary>The latest database list fetch (completed when none ran). Tests await it.</summary>
    public Task DatabaseListTask { get; private set; } = Task.CompletedTask;

    /// <summary>Lists the server's databases for the dropdown. Replaceable for tests.</summary>
    internal Func<ConnectionProfile, ConnectionSecrets, CancellationToken, Task<IReadOnlyList<string>>> DatabaseLister { get; set; } =
        ConnectionTester.ListDatabasesAsync;

    /// <summary>How long the dropdown waits for the list.</summary>
    internal TimeSpan DatabaseListTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The database the hint under the field suggests (see <see cref="DatabaseSuggestion"/>), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DatabaseHint))]
    public partial string? SuggestedDatabase { get; private set; }

    public string? DatabaseHint => SuggestedDatabase is null ? null : $"Your data may be in “{SuggestedDatabase}” — use it";

    [RelayCommand]
    private void UseSuggestedDatabase()
    {
        if (SuggestedDatabase is { } name) Database = name;
    }

    /// <summary>
    /// The dropdown is opening: fetch the list once per server (host/port/user/password/SSH/SSL), if the form has what a
    /// connection needs without prompting. Never shows a dialog; problems become a disabled row.
    /// </summary>
    public Task OpenDatabaseList()
    {
        if (_dbListLoaded || _dbListCts is not null) return DatabaseListTask;
        if (!TryBuildForListing(out var profile, out var secrets))
        {
            SetDatabaseItems([new DatabaseItem("Fill in host, user and password (or Test) to list databases", DatabaseItemKind.Message)]);
            return Task.CompletedTask;
        }
        return DatabaseListTask = FetchDatabasesAsync(profile, secrets);
    }

    private async Task FetchDatabasesAsync(ConnectionProfile profile, ConnectionSecrets secrets)
    {
        _dbListCts?.Cancel();
        var cts = new CancellationTokenSource();
        _dbListCts = cts;
        SetDatabaseItems([new DatabaseItem("Loading…", DatabaseItemKind.Loading)]);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        timeout.CancelAfter(DatabaseListTimeout);
        var lister = DatabaseLister;
        try
        {
            var names = await Task.Run(() => lister(profile, secrets, timeout.Token), timeout.Token).WaitAsync(timeout.Token);
            if (cts.IsCancellationRequested) return;
            ShowDatabases(names);
        }
        catch (Exception ex)
        {
            // Cancelled because the server fields changed: the list was already cleared.
            if (cts.IsCancellationRequested) return;
            SetDatabaseItems([new DatabaseItem(
                ex is OperationCanceledException ? "Timed out listing databases." : "Could not list databases: " + ErrorText(ex),
                DatabaseItemKind.Message)]);
        }
        finally
        {
            if (_dbListCts == cts) _dbListCts = null;
        }
    }

    private void ShowDatabases(IReadOnlyList<string> names)
    {
        _dbListLoaded = true;
        SetDatabaseItems(names.Count == 0
            ? [new DatabaseItem("No databases found", DatabaseItemKind.Message)]
            : names.Select(n => new DatabaseItem(n, DatabaseItemKind.Database)).ToList());
    }

    /// <summary>
    /// Raised just before <see cref="DatabaseItems"/> changes. The editable ComboBox clears its text when its selected
    /// item leaves the list, so the view drops the selection (keeping the text) first.
    /// </summary>
    public event Action? DatabaseItemsChanging;

    private void SetDatabaseItems(IReadOnlyList<DatabaseItem> items)
    {
        if (DatabaseItems.SequenceEqual(items)) return;
        DatabaseItemsChanging?.Invoke();
        DatabaseItems.Clear();
        foreach (var item in items) DatabaseItems.Add(item);
    }

    /// <summary>A server field changed: cancel any fetch and forget the list and the hint.</summary>
    private void InvalidateDatabaseList()
    {
        _serverGeneration++;
        _dbListCts?.Cancel();
        _dbListCts = null;
        _dbListLoaded = false;
        _testedDatabases = null;
        SetDatabaseItems([]);
        UpdateSuggestion();
    }

    private void UpdateSuggestion() => SuggestedDatabase = DatabaseSuggestion.Suggest(Database, _testedDatabases);

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is null) return;
        if (e.PropertyName == nameof(Database)) UpdateSuggestion();
        else if (ServerFields.Contains(e.PropertyName)) InvalidateDatabaseList();
    }

    /// <summary>Profile and secrets for a background list fetch: valid fields and no password prompt or command needed.</summary>
    private bool TryBuildForListing([NotNullWhen(true)] out ConnectionProfile? profile, [NotNullWhen(true)] out ConnectionSecrets? secrets)
    {
        secrets = null;
        if (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(User) || !TryBuildProfile(out profile, out _))
        {
            profile = null;
            return false;
        }
        string? password = null;
        switch (profile.PasswordMode)
        {
            case PasswordMode.Store when !string.IsNullOrEmpty(Password):
                password = Password;
                break;
            case PasswordMode.None:
                break;
            default:
                return false;
        }
        string? sshPassword = null;
        if (profile.SshEnabled && !profile.SshUseKey)
        {
            switch (profile.SshPasswordMode)
            {
                case PasswordMode.Store when !string.IsNullOrEmpty(SshPassword):
                    sshPassword = SshPassword;
                    break;
                case PasswordMode.None:
                    break;
                default:
                    return false;
            }
        }
        secrets = new ConnectionSecrets(password, sshPassword, profile.SshEnabled && profile.SshUseKey ? NullIfEmpty(SshPassphrase) : null);
        return true;
    }

    private static string ErrorText(Exception ex) =>
        ex.InnerException is { } inner && ex.Message.Length < 20 ? inner.Message : ex.Message;

    private static bool TryPort(string text, out int port) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
