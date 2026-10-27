using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using ICSharpCode.AvalonEdit.Rendering;

namespace TablePlsu.App.Features.SqlEditor;

/// <summary>
/// <c>editor.lineHeight</c> for AvalonEdit, which has no line-spacing option: a zero-width element at the end of each
/// document line that is <see cref="Factor"/> × font size tall and centred on the text, so the line grows to that height
/// (never below the font's own line spacing). Factor 0 (or a height the font already reaches) adds nothing.
/// </summary>
public sealed class LineSpacingGenerator : VisualLineElementGenerator
{
    /// <summary>Line height as a multiple of the font size (CSS-style), e.g. 1.5.</summary>
    public double Factor { get; set; }

    private (double Height, double Baseline)? Metrics()
    {
        var view = CurrentContext.TextView;
        var natural = view.DefaultLineHeight;
        var target = CurrentContext.GlobalTextRunProperties.FontRenderingEmSize * Factor;
        if (Factor <= 0 || target <= natural + 0.5) return null;
        var extra = target - natural;
        return (target, view.DefaultBaseline + extra / 2);
    }

    public override int GetFirstInterestedOffset(int startOffset)
    {
        if (Metrics() is null) return -1;
        var end = CurrentContext.VisualLine.LastDocumentLine.EndOffset;
        return end >= startOffset ? end : -1;
    }

    public override VisualLineElement? ConstructElement(int offset) =>
        Metrics() is { } m ? new SpacerElement(m.Height, m.Baseline) : null;

    private sealed class SpacerElement(double height, double baseline) : VisualLineElement(1, 0)
    {
        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) =>
            new SpacerRun(height, baseline, TextRunProperties);

        public override int GetNextCaretPosition(int visualColumn, LogicalDirection direction, ICSharpCode.AvalonEdit.Document.CaretPositioningMode mode) => -1;

        public override bool IsWhitespace(int visualColumn) => true;
    }

    private sealed class SpacerRun(double height, double baseline, TextRunProperties properties) : TextEmbeddedObject
    {
        public override LineBreakCondition BreakBefore => LineBreakCondition.BreakRestrained;
        public override LineBreakCondition BreakAfter => LineBreakCondition.BreakPossible;
        public override bool HasFixedSize => true;
        public override CharacterBufferReference CharacterBufferReference => default;
        public override int Length => 1;
        public override TextRunProperties Properties => properties;
        public override TextEmbeddedObjectMetrics Format(double remainingParagraphWidth) => new(0, height, baseline);
        public override Rect ComputeBoundingBox(bool rightToLeft, bool sideways) => new(0, 0, 0, height);
        public override void Draw(DrawingContext drawingContext, Point origin, bool rightToLeft, bool sideways) { }
    }
}
