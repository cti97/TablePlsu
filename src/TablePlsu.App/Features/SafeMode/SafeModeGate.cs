using System.Security.Cryptography;
using System.Text;
using Npgsql;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Connections;
using TablePlsu.Core.Data;

namespace TablePlsu.App.Features.SafeMode;

/// <summary>The UI the gate needs (replaced in tests).</summary>
public interface ISafeModePrompts
{
    /// <summary>Lists the statements; true = Send.</summary>
    bool Confirm(SafeModeLevel level, IReadOnlyList<string> statements);

    /// <summary>Asks for the database password (optionally listing statements). Null = cancelled.</summary>
    string? AskPassword(string message, IReadOnlyList<string>? statements, string? error);
}

/// <summary>How the gate checks a typed password against the connection's DB password.</summary>
public interface ISafeModePasswordVerifier
{
    /// <summary>True when the connection has a password the typed one can be checked against.</summary>
    bool CanVerify(ConnectionProfile profile);

    Task<bool> VerifyAsync(ConnectionProfile profile, string database, string password, CancellationToken ct = default);
}

/// <summary>
/// The real Safe Mode gate (spec §8). Alert levels show a confirm dialog listing the statements; Safe levels ask for the
/// connection's DB password (compared against the DPAPI-stored secret, or checked by a test login for "ask" mode); the
/// "except reads" levels let SELECT / EXPLAIN / SHOW / WITH-select / VALUES / TABLE through (<see cref="SafeModePolicy"/>).
/// Lowering the level from Safe 1/2 requires the password. Level changes persist to the connection profile.
/// When a Safe level is set on a connection without any password, a plain confirmation is used instead.
/// </summary>
public sealed class SafeModeGate : ISafeModeGate
{
    private const int MaxAttempts = 3;
    private readonly IWorkspace _workspace;
    private readonly ISafeModePrompts _prompts;
    private readonly ISafeModePasswordVerifier _verifier;
    private readonly Action<ConnectionProfile>? _persist;
    private SafeModeLevel _level;

    public SafeModeGate(IWorkspace workspace, ISafeModePrompts? prompts = null, ISafeModePasswordVerifier? verifier = null, Action<ConnectionProfile>? persist = null)
    {
        _workspace = workspace;
        _prompts = prompts ?? new SafeModeDialogPrompts();
        _verifier = verifier ?? new SecretStorePasswordVerifier();
        _persist = persist ?? PersistToStore;
        _level = workspace.Profile.SafeMode;
    }

    public SafeModeLevel Level => _level;

    public event EventHandler? LevelChanged;

    public async Task<bool> ConfirmAsync(IReadOnlyList<string> statements, CancellationToken ct = default)
    {
        var requirement = SafeModePolicy.Evaluate(_level, statements);
        switch (requirement)
        {
            case SafeModeRequirement.None:
                return true;
            case SafeModeRequirement.Confirm:
                return await UiDispatch.InvokeAsync(() => _prompts.Confirm(_level, statements)).ConfigureAwait(true);
            default:
                if (!_verifier.CanVerify(_workspace.Profile))
                    return await UiDispatch.InvokeAsync(() => _prompts.Confirm(_level, statements)).ConfigureAwait(true);
                return await AskAndVerifyAsync($"Safe Mode ({LevelText(_level)}): enter the database password to send {statements.Count} statement(s).", statements, ct).ConfigureAwait(true);
        }
    }

    public async Task<bool> ChangeLevelAsync(SafeModeLevel level)
    {
        if (level == _level) return false;
        if (SafeModePolicy.ChangeRequiresPassword(_level, level) && _verifier.CanVerify(_workspace.Profile))
        {
            var ok = await AskAndVerifyAsync($"Enter the database password to lower Safe Mode from {LevelText(_level)} to {LevelText(level)}.", null, default).ConfigureAwait(true);
            if (!ok) return false;
        }
        _level = level;
        _persist?.Invoke(_workspace.Profile with { SafeMode = level });
        LevelChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private async Task<bool> AskAndVerifyAsync(string message, IReadOnlyList<string>? statements, CancellationToken ct)
    {
        string? error = null;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var typed = await UiDispatch.InvokeAsync(() => _prompts.AskPassword(message, statements, error)).ConfigureAwait(true);
            if (typed is null) return false;
            if (await _verifier.VerifyAsync(_workspace.Profile, _workspace.Database, typed, ct).ConfigureAwait(true)) return true;
            error = "Wrong password.";
        }
        return false;
    }

    public static string LevelText(SafeModeLevel level) => level switch
    {
        SafeModeLevel.Alert1 => "Alert 1",
        SafeModeLevel.Alert2 => "Alert 2",
        SafeModeLevel.Safe1 => "Safe 1",
        SafeModeLevel.Safe2 => "Safe 2",
        _ => "Silent",
    };

    private static void PersistToStore(ConnectionProfile profile)
    {
        if (!AppServices.IsInitialized) return;
        var stored = AppServices.Connections.Get(profile.Id);
        if (stored is not null) AppServices.Connections.Save(stored with { SafeMode = profile.SafeMode });
    }
}

/// <summary>
/// Store mode: constant-time compare with the DPAPI secret. Ask / Command mode (no SSH): a test login with the typed
/// password. No password, or SSH without a stored password: cannot verify (the gate falls back to a confirmation).
/// </summary>
public sealed class SecretStorePasswordVerifier : ISafeModePasswordVerifier
{
    public bool CanVerify(ConnectionProfile profile) => profile.PasswordMode switch
    {
        PasswordMode.Store => !string.IsNullOrEmpty(StoredPassword(profile)),
        PasswordMode.Ask or PasswordMode.Command => !profile.SshEnabled,
        _ => false,
    };

    public async Task<bool> VerifyAsync(ConnectionProfile profile, string database, string password, CancellationToken ct = default)
    {
        if (profile.PasswordMode == PasswordMode.Store)
        {
            var stored = StoredPassword(profile) ?? "";
            return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(stored), Encoding.UTF8.GetBytes(password));
        }
        try
        {
            var cs = Core.Db.DbSession.BuildConnectionString(profile, new ConnectionSecrets(password, null, null), database, applicationName: "TablePlsu (safe mode check)");
            var b = new NpgsqlConnectionStringBuilder(cs) { Pooling = false, Timeout = 10 };
            await using var conn = new NpgsqlConnection(b.ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.InvalidPassword or PostgresErrorCodes.InvalidAuthorizationSpecification)
        {
            return false;
        }
        catch (NpgsqlException)
        {
            return false;
        }
    }

    private static string? StoredPassword(ConnectionProfile profile) =>
        AppServices.IsInitialized ? AppServices.Secrets.Get(profile.Id, SecretKind.DbPassword) : null;
}

/// <summary>The WPF prompts (<see cref="SafeModeDialog"/>).</summary>
public sealed class SafeModeDialogPrompts : ISafeModePrompts
{
    public bool Confirm(SafeModeLevel level, IReadOnlyList<string> statements) =>
        SafeModeDialog.Show($"Safe Mode ({SafeModeGate.LevelText(level)}): send {statements.Count} statement(s)?", statements, askPassword: false, error: null, out _);

    public string? AskPassword(string message, IReadOnlyList<string>? statements, string? error) =>
        SafeModeDialog.Show(message, statements ?? [], askPassword: true, error, out var password) ? password : null;
}
