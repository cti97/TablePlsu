using Microsoft.Win32;

namespace TablePlsu.App.Infrastructure;

/// <summary>
/// <c>general.startWithWindows</c>: the <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> value "TablePlsu" =
/// <c>"&lt;exe&gt;" --background</c>. <see cref="Sync"/> writes it only when it differs and removes it only when it is
/// ours, so toggling the setting is idempotent. Tests point <see cref="KeyPath"/> at a scratch key.
/// </summary>
public static class RunAtLogin
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "TablePlsu";

    internal static string KeyPath { get; set; } = RunKeyPath;

    public static string CommandLineFor(string exePath) => $"\"{exePath}\" --background";

    /// <summary>The registered command line, or null.</summary>
    public static string? Current()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        return key?.GetValue(ValueName) as string;
    }

    /// <summary>Registers (enabled) or unregisters (disabled) <paramref name="exePath"/>. Returns true when it changed anything.</summary>
    public static bool Sync(bool enabled, string exePath)
    {
        var wanted = CommandLineFor(exePath);
        var current = Current();
        if (enabled)
        {
            if (string.Equals(current, wanted, StringComparison.OrdinalIgnoreCase)) return false;
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            key.SetValue(ValueName, wanted, RegistryValueKind.String);
            return true;
        }
        // Only remove an entry that points at this exe (another install of TablePlsu may own it).
        if (current is null || !current.Contains(exePath, StringComparison.OrdinalIgnoreCase)) return false;
        using (var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true))
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        return true;
    }
}
