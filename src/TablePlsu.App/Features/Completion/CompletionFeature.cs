using System.Windows;
using System.Windows.Media;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Commands;

namespace TablePlsu.App.Features.Completion;

/// <summary>
/// SQL autocomplete (spec §9). The popup itself is attached per editor with <see cref="SqlCompletion.Attach(ICSharpCode.AvalonEdit.TextEditor, IWorkspace)"/>
/// (the SQL editor hooks it in). This feature only lends the popup Ctrl+N / Ctrl+P while it is open: those chords are
/// bound to window.new and palette.openAnything, which the window-level key router resolves before the editor sees them,
/// so it registers higher-priority handlers that move the popup selection instead.
/// </summary>
public sealed class CompletionFeature : IFeature
{
    public void Register(FeatureContext ctx)
    {
        ctx.Commands.Register(CommandIds.WindowNew, _ => SqlCompletion.Active!.MoveSelection(1), PopupOwnsKeys);
        ctx.Commands.Register(CommandIds.PaletteOpenAnything, _ => SqlCompletion.Active!.MoveSelection(-1), PopupOwnsKeys);
    }

    /// <summary>True when a completion popup is open and keyboard focus is in its editor.</summary>
    internal static bool PopupOwnsKeys(CommandContext ctx)
    {
        if (SqlCompletion.Active is not { IsOpen: true } active) return false;
        for (var d = ctx.FocusedElement as DependencyObject; d is not null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (ReferenceEquals(d, active.Editor)) return true;
        return false;
    }
}
