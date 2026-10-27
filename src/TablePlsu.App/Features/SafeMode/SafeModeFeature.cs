using TablePlsu.App.Infrastructure;

namespace TablePlsu.App.Features.SafeMode;

/// <summary>
/// Safe Mode (spec §8): replaces the shell's DefaultSafeModeGate with <see cref="SafeModeGate"/> (confirm dialog listing
/// statements for Alert levels, DB password prompt for Safe levels, password required to lower from Safe). The toolbar's
/// Safe Mode button (WorkspaceView "tb.safeMode") shows the level and calls ChangeLevelAsync.
/// </summary>
public sealed class SafeModeFeature : IFeature
{
    public void Register(FeatureContext ctx) => ctx.UseSafeMode(ws => new SafeModeGate(ws));
}
