namespace TablePlsu.Core.Startup;

/// <summary>
/// Command-line flags of <c>TablePlsu.App.exe</c>. Parsed before WPF starts (cheap: no regex, no reflection).
/// <list type="bullet">
/// <item><c>--background</c>: start hidden in the tray, pre-warmed (Windows sign-in, <c>general.startWithWindows</c>).</item>
/// <item><c>--measure-startup</c>: log ms to first render and exit (never forwarded to a running instance).</item>
/// <item><c>--measure-activation</c>: forwarded to the running instance, which logs launch → window visible and hides again.</item>
/// <item><c>--quit</c>: ask the running instance to quit (flushing autosave).</item>
/// <item>a <c>postgresql://</c>, <c>postgres://</c> or <c>postgresql+ssh://</c> URL: connect to it.</item>
/// </list>
/// </summary>
public sealed record LaunchArgs(bool Background, bool MeasureStartup, bool MeasureActivation, bool Quit, string? Url)
{
    private static readonly string[] UrlSchemes = ["postgresql://", "postgres://", "postgresql+ssh://"];

    /// <summary>No flags and no URL: just "show me a window".</summary>
    public bool IsPlainLaunch => !Background && !MeasureStartup && !MeasureActivation && !Quit && Url is null;

    public static LaunchArgs Parse(IReadOnlyList<string> args)
    {
        bool background = false, measure = false, activation = false, quit = false;
        string? url = null;
        foreach (var raw in args)
        {
            var a = raw.Trim();
            if (Is(a, "--background")) background = true;
            else if (Is(a, "--measure-startup")) measure = true;
            else if (Is(a, "--measure-activation")) activation = true;
            else if (Is(a, "--quit")) quit = true;
            else if (url is null && IsConnectionUrl(a)) url = a;
        }
        return new LaunchArgs(background, measure, activation, quit, url);
    }

    public static bool IsConnectionUrl(string text)
    {
        foreach (var scheme in UrlSchemes)
            if (text.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool Is(string arg, string flag) => string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase);
}
