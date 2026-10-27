using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Commands;

namespace TablePlsu.App.Features.Palette;

/// <summary>
/// Open Anything (Ctrl+P, <c>palette.openAnything</c>), Open Database (Ctrl+K, <c>palette.openDatabase</c>) and
/// Open Connection (Ctrl+Shift+K, <c>palette.openConnection</c>, also on the welcome screen). Items come from
/// <see cref="FeatureContext.Palette"/>; this feature registers <see cref="BuiltInPaletteProvider"/> (metadata objects,
/// schemas, databases, open SQL tabs, app commands, saved connections).
/// </summary>
public sealed class PaletteFeature : IFeature
{
    public void Register(FeatureContext ctx)
    {
        ctx.Palette.Register(new BuiltInPaletteProvider());
        ctx.Commands.Register(CommandIds.PaletteOpenAnything, x => PaletteWindow.Open(PaletteMode.Anything, x.Workspace, x.Window), x => x.Workspace is not null);
        ctx.Commands.Register(CommandIds.PaletteOpenDatabase, x => PaletteWindow.Open(PaletteMode.Databases, x.Workspace, x.Window), x => x.Workspace is not null);
        ctx.Commands.Register(CommandIds.PaletteOpenConnection, x => PaletteWindow.Open(PaletteMode.Connections, x.Workspace, x.Window));
    }
}
