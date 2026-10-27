using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using TablePlsu.Core.Sql;

namespace TablePlsu.App.Features.SqlEditor;

/// <summary>
/// Tints the statement Run Current would execute (spec §9 "Highlight current query") with
/// <c>c.editor.currentQueryBackground</c>. The split is cached per document change.
/// </summary>
public sealed class CurrentStatementRenderer(TextView textView, Func<int> caret, Func<bool> enabled) : IBackgroundRenderer
{
    private TextDocument? _splitFor;
    private int _splitVersion = -1;
    private int _version;
    private IReadOnlyList<SqlStatement> _statements = [];

    public KnownLayer Layer => KnownLayer.Background;

    /// <summary>Call on every document change.</summary>
    public void Invalidate() => _version++;

    public void Draw(TextView view, DrawingContext drawingContext)
    {
        if (!enabled() || view.Document is not { } doc || doc.TextLength == 0) return;
        if (!ReferenceEquals(_splitFor, doc) || _splitVersion != _version)
        {
            _statements = StatementSplitter.Split(doc.Text);
            _splitFor = doc;
            _splitVersion = _version;
        }
        if (StatementSplitter.StatementAt(_statements, Math.Clamp(caret(), 0, doc.TextLength)) is not { } st) return;
        if (st.End > doc.TextLength) return;

        var brush = textView.TryFindResource("c.editor.currentQueryBackground") as Brush;
        if (brush is null) return;
        var builder = new BackgroundGeometryBuilder { AlignToWholePixels = true, ExtendToFullWidthAtLineEnd = true, CornerRadius = 0 };
        builder.AddSegment(view, new TextSegment { StartOffset = st.Start, EndOffset = st.End });
        if (builder.CreateGeometry() is { } geometry) drawingContext.DrawGeometry(brush, null, geometry);
    }
}

/// <summary>Draws a squiggly underline (<c>c.editor.errorUnderline</c>) under the error reported by the last run.</summary>
public sealed class ErrorUnderlineRenderer(TextView textView) : IBackgroundRenderer
{
    private TextSegment? _segment;

    public KnownLayer Layer => KnownLayer.Selection;

    public void Set(int start, int length, TextDocument document)
    {
        if (start < 0 || start + length > document.TextLength) { _segment = null; return; }
        _segment = new TextSegment { StartOffset = start, Length = Math.Max(1, length) };
        textView.InvalidateLayer(Layer);
    }

    public void Clear()
    {
        if (_segment is null) return;
        _segment = null;
        textView.InvalidateLayer(Layer);
    }

    public void Draw(TextView view, DrawingContext dc)
    {
        if (_segment is null || view.Document is null || _segment.EndOffset > view.Document.TextLength) return;
        var brush = textView.TryFindResource("c.editor.errorUnderline") as Brush ?? Brushes.Red;
        var pen = new Pen(brush, 1.2);
        pen.Freeze();
        foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(view, _segment))
        {
            var y = rect.Bottom - 1;
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(rect.Left, y), false, false);
                var up = true;
                for (var x = rect.Left + 2; x <= rect.Right + 2; x += 2)
                {
                    ctx.LineTo(new Point(x, up ? y - 2 : y), true, false);
                    up = !up;
                }
            }
            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }
    }
}
