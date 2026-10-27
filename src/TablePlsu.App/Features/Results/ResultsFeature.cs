using System.Windows;
using System.Windows.Media;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Commands;

namespace TablePlsu.App.Features.Results;

/// <summary>
/// The shared <see cref="ResultGrid"/> (used by the SQL editor results and the table data view). Registers the grid
/// copy commands: grid.copy (Ctrl+C, rows as TSV) and grid.copyCells (Ctrl+Shift+C, selected cells only). Host
/// view-models that implement ICommandTarget for these ids take precedence.
/// </summary>
public sealed class ResultsFeature : IFeature
{
    public void Register(FeatureContext ctx)
    {
        ctx.Commands.Register(CommandIds.GridCopy, x => FindGrid(x)!.CopyRows(), x => FindGrid(x) is { HasSelection: true });
        ctx.Commands.Register(CommandIds.GridCopyCells, x => FindGrid(x)!.CopySelectedCells(), x => FindGrid(x) is { HasSelection: true });
    }

    /// <summary>The ResultGrid a command applies to: the scope element, or the nearest ResultGrid around the focused element.</summary>
    public static ResultGrid? FindGrid(CommandContext ctx)
    {
        if (ctx.ScopeElement is ResultGrid g) return g;
        for (var d = ctx.FocusedElement as DependencyObject; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ResultGrid found) return found;
        return null;
    }
}
