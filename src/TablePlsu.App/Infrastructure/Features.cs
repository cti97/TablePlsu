using TablePlsu.App.Features.Completion;
using TablePlsu.App.Features.Console;
using TablePlsu.App.Features.Palette;
using TablePlsu.App.Features.Results;
using TablePlsu.App.Features.SafeMode;
using TablePlsu.App.Features.Settings;
using TablePlsu.App.Features.Sidebar;
using TablePlsu.App.Features.SqlEditor;
using TablePlsu.App.Features.Structure;
using TablePlsu.App.Features.TableData;
using TablePlsu.App.Features.Welcome;
using TablePlsu.App.Features.Workspace;

namespace TablePlsu.App.Infrastructure;

/// <summary>
/// The explicit feature list (one line per feature, no reflection). Order matters only for command handler overrides:
/// for the same command id, a later feature's handler is tried first.
/// <para>Note: inside <c>namespace TablePlsu.App.*</c> the bare name <c>Features</c> binds to the
/// <c>TablePlsu.App.Features</c> namespace, so outside this namespace write <c>Infrastructure.Features</c>.</para>
/// </summary>
public static class Features
{
    public static IReadOnlyList<IFeature> CreateAll() =>
    [
        new WelcomeFeature(),
        new WorkspaceFeature(),
        new SqlEditorFeature(),
        new ResultsFeature(),
        new SidebarFeature(),
        new PaletteFeature(),
        new TableDataFeature(),
        new StructureFeature(),
        new SafeModeFeature(),
        new SettingsFeature(),
        new ConsoleFeature(),
        new CompletionFeature(),
    ];
}
