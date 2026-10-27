using System.Diagnostics;
using System.Windows;
using TablePlsu.Core.Connections;

namespace TablePlsu.App.Infrastructure;

/// <summary>
/// Turns a profile's password modes into <see cref="ConnectionSecrets"/>: Store reads DPAPI, Ask prompts (never
/// persisted), None gives null, Command runs the profile's command and uses trimmed stdout. Returns null when the user
/// cancels a prompt. Prompts run on the UI thread; commands run on the thread pool.
/// </summary>
public static class SecretResolver
{
    public static async Task<ConnectionSecrets?> ResolveAsync(ConnectionProfile profile, Window? owner = null, CancellationToken ct = default)
    {
        string? password;
        switch (profile.PasswordMode)
        {
            case PasswordMode.Store:
                password = AppServices.Secrets.Get(profile.Id, SecretKind.DbPassword);
                break;
            case PasswordMode.Ask:
                password = Dialogs.PromptPassword("Password", $"Password for {profile.User}@{profile.Host} ({profile.DisplayName}):", owner);
                if (password is null) return null;
                break;
            case PasswordMode.Command:
                password = await RunPasswordCommandAsync(profile.PasswordCommand, ct).ConfigureAwait(true);
                break;
            default:
                password = null;
                break;
        }

        string? sshPassword = null;
        string? passphrase = null;
        if (profile.SshEnabled)
        {
            if (profile.SshUseKey)
            {
                passphrase = AppServices.Secrets.Get(profile.Id, SecretKind.SshPassphrase);
            }
            else
            {
                switch (profile.SshPasswordMode)
                {
                    case PasswordMode.Store:
                        sshPassword = AppServices.Secrets.Get(profile.Id, SecretKind.SshPassword);
                        break;
                    case PasswordMode.Ask:
                        sshPassword = Dialogs.PromptPassword("SSH password", $"SSH password for {profile.SshUser}@{profile.SshHost}:", owner);
                        if (sshPassword is null) return null;
                        break;
                }
            }
        }
        return new ConnectionSecrets(password, sshPassword, passphrase);
    }

    /// <summary>Runs <paramref name="command"/> through cmd.exe and returns trimmed stdout (P1 "Command line" mode).</summary>
    public static async Task<string?> RunPasswordCommandAsync(string? command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var psi = new ProcessStartInfo("cmd.exe", "/d /c " + command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the password command.");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"Password command failed ({p.ExitCode}): {(await stderr.ConfigureAwait(false)).Trim()}");
        return (await stdout.ConfigureAwait(false)).Trim();
    }
}
