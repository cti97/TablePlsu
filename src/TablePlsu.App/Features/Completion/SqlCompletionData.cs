using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using TablePlsu.Core.Completion;

namespace TablePlsu.App.Features.Completion;

/// <summary>One popup row: kind glyph, name, detail (all theme brushes). Content is built lazily (only for realized rows).</summary>
public sealed class SqlCompletionData(CompletionItem item, SqlCompletion owner) : ICompletionData
{
    private object? _content;

    public CompletionItem Item { get; } = item;

    public ImageSource? Image => null;

    public string Text => Item.Display;

    public object Content => _content ??= BuildContent();

    public object Description => Item.Detail is null ? Item.Display : Item.Display + "\n" + Item.Detail;

    public double Priority => 0;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
        owner.Insert(textArea, completionSegment, Item);

    /// <summary>The glyph letter for a kind.</summary>
    public static string Glyph(CompletionKind kind) => kind switch
    {
        CompletionKind.Keyword => "K",
        CompletionKind.Schema => "S",
        CompletionKind.Table => "T",
        CompletionKind.View => "V",
        CompletionKind.Column => "C",
        CompletionKind.Function => "ƒ",
        CompletionKind.Alias => "A",
        CompletionKind.Cte => "W",
        _ => "·",
    };

    /// <summary>The theme brush key a kind's glyph uses.</summary>
    public static string GlyphBrushKey(CompletionKind kind) => kind switch
    {
        CompletionKind.Keyword => "c.editor.keyword",
        CompletionKind.Function => "c.editor.function",
        CompletionKind.Column => "c.editor.parameter",
        CompletionKind.Table or CompletionKind.View => "c.editor.type",
        CompletionKind.Schema => "c.editor.number",
        _ => "c.editor.string",
    };

    private FrameworkElement BuildContent()
    {
        var glyph = new TextBlock
        {
            Text = Glyph(Item.Kind),
            Width = 18,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, GlyphBrushKey(Item.Kind));

        var name = new TextBlock { Text = Item.Display, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

        var panel = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(glyph, Dock.Left);
        panel.Children.Add(glyph);
        if (!string.IsNullOrEmpty(Item.Detail))
        {
            var detail = new TextBlock
            {
                Text = Item.Detail,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 200,
            };
            detail.SetResourceReference(TextBlock.ForegroundProperty, "c.app.mutedForeground");
            DockPanel.SetDock(detail, Dock.Right);
            panel.Children.Add(detail);
        }
        panel.Children.Add(name);
        return panel;
    }
}
