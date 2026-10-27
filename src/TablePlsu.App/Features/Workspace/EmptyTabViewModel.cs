using System.Windows;
using System.Windows.Controls;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Commands;

namespace TablePlsu.App.Features.Workspace;

/// <summary>The "empty" tab (<c>general.newTabOpens = "empty"</c>, or when no SQL editor feature is registered).</summary>
public sealed class EmptyTabViewModel : TabViewModelBase
{
    public EmptyTabViewModel() : base(TabKinds.Empty) => Title = "New Tab";

    public override FrameworkElement CreateView()
    {
        var hint = new TextBlock
        {
            Text = $"{ShortcutText.Tooltip(CommandIds.PaletteOpenAnything)}\n{ShortcutText.Tooltip(CommandIds.EditorNew)}",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            LineHeight = 22,
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "c.app.mutedForeground");
        var root = new Grid { Children = { hint } };
        root.SetResourceReference(Panel.BackgroundProperty, "c.app.window");
        return root;
    }
}
