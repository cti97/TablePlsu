using TablePlsu.Core.Connections;

namespace TablePlsu.App.Infrastructure;

/// <summary>
/// Minimal Safe Mode gate used until the SafeMode feature replaces it: Silent sends; every other level asks for a plain
/// confirmation listing the statements (the password levels and the read-only exemptions are the SafeMode feature's job).
/// The level starts from the profile and changes persist to the profile.
/// </summary>
public sealed class DefaultSafeModeGate(IWorkspace workspace) : ISafeModeGate
{
    private SafeModeLevel _level = workspace.Profile.SafeMode;

    public SafeModeLevel Level => _level;

    public event EventHandler? LevelChanged;

    public Task<bool> ConfirmAsync(IReadOnlyList<string> statements, CancellationToken ct = default)
    {
        if (_level == SafeModeLevel.Silent || statements.Count == 0) return Task.FromResult(true);
        var detail = string.Join(";\n\n", statements.Take(50));
        return Task.FromResult(Dialogs.Confirm($"Safe Mode ({_level}): send {statements.Count} statement(s)?", "Safe Mode", "Send", detail: detail));
    }

    public Task<bool> ChangeLevelAsync(SafeModeLevel level)
    {
        if (level == _level) return Task.FromResult(false);
        _level = level;
        var stored = AppServices.Connections.Get(workspace.Profile.Id);
        if (stored is not null) AppServices.Connections.Save(stored with { SafeMode = level });
        LevelChanged?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(true);
    }
}
