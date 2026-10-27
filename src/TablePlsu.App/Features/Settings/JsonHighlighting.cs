using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit.Rendering;
using TablePlsu.Core.Settings;
using TablePlsu.Core.Themes;

namespace TablePlsu.App.Features.Settings;

/// <summary>
/// JSON-with-comments highlighting built from the active theme's editor colours (property names = editor.function,
/// strings = editor.string, numbers = editor.number, true/false/null = editor.keyword, comments = editor.comment).
/// Rebuilt when the theme changes, so it follows Light/Dark/overrides live.
/// </summary>
public static class JsonHighlighting
{
    private static (ThemeDefinition Theme, IHighlightingDefinition Definition)? _cache;

    public static IHighlightingDefinition For(ThemeDefinition theme)
    {
        if (_cache is { } c && ReferenceEquals(c.Theme, theme)) return c.Definition;
        var def = Build(theme);
        _cache = (theme, def);
        return def;
    }

    public static IHighlightingDefinition Build(ThemeDefinition theme)
    {
        string Color(string key, string fallback) =>
            SecurityElement.Escape(theme.Colors.TryGetValue(key, out var v) && ThemeColor.TryParse(v, out var tc) ? tc.ToString() : fallback);

        var xshd = $"""
            <SyntaxDefinition name="SettingsJson" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
              <Color name="Comment" foreground="{Color("editor.comment", "#808080")}" />
              <Color name="Key" foreground="{Color("editor.function", "#0000FF")}" />
              <Color name="String" foreground="{Color("editor.string", "#008000")}" />
              <Color name="Number" foreground="{Color("editor.number", "#098658")}" />
              <Color name="Literal" foreground="{Color("editor.keyword", "#0000FF")}" fontWeight="bold" />
              <RuleSet>
                <Span color="Comment" begin="//" />
                <Span color="Comment" multiline="true" begin="/\*" end="\*/" />
                <Rule color="Key">"(?:[^"\\\n]|\\.)*"(?=\s*:)</Rule>
                <Rule color="String">"(?:[^"\\\n]|\\.)*"?</Rule>
                <Rule color="Literal">\b(?:true|false|null)\b</Rule>
                <Rule color="Number">-?\b\d+(?:\.\d+)?(?:[eE][+-]?\d+)?\b</Rule>
              </RuleSet>
            </SyntaxDefinition>
            """;
        using var reader = XmlReader.Create(new StringReader(xshd));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }
}

/// <summary>Draws wavy underlines under validation diagnostics (errors in editor.errorUnderline, warnings in app.warning).</summary>
public sealed class DiagnosticUnderlineRenderer : IBackgroundRenderer
{
    private IReadOnlyList<SettingsDiagnostic> _diagnostics = [];

    public KnownLayer Layer => KnownLayer.Selection;

    public Brush ErrorBrush { get; set; } = Brushes.Red;

    public Brush WarningBrush { get; set; } = Brushes.Orange;

    public void SetDiagnostics(IReadOnlyList<SettingsDiagnostic> diagnostics) => _diagnostics = diagnostics;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_diagnostics.Count == 0 || !textView.VisualLinesValid) return;
        var doc = textView.Document;
        if (doc is null) return;
        foreach (var d in _diagnostics)
        {
            if (d.Line < 1 || d.Line > doc.LineCount) continue;
            var line = doc.GetLineByNumber(d.Line);
            var start = line.Offset + Math.Clamp(d.Column - 1, 0, line.Length);
            var end = TokenEnd(doc, start, line.EndOffset);
            if (end <= start)
            {
                start = line.Offset;
                end = line.EndOffset;
            }
            if (end <= start) continue;
            var pen = new Pen(d.IsError ? ErrorBrush : WarningBrush, 1);
            pen.Freeze();
            var segment = new TextSegment { StartOffset = start, EndOffset = end };
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
            {
                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    var y = rect.Bottom - 1;
                    ctx.BeginFigure(new Point(rect.Left, y), false, false);
                    var up = true;
                    for (var x = rect.Left + 2; x <= rect.Right + 0.1; x += 2)
                    {
                        ctx.LineTo(new Point(x, up ? y - 2 : y), true, false);
                        up = !up;
                    }
                }
                geometry.Freeze();
                drawingContext.DrawGeometry(null, pen, geometry);
            }
        }
    }

    /// <summary>End of the JSON token at <paramref name="start"/>: a whole string, or a run of word characters.</summary>
    private static int TokenEnd(TextDocument doc, int start, int lineEnd)
    {
        if (start >= lineEnd) return start;
        var i = start;
        if (doc.GetCharAt(i) == '"')
        {
            i++;
            while (i < lineEnd)
            {
                var c = doc.GetCharAt(i);
                if (c == '\\') { i += 2; continue; }
                i++;
                if (c == '"') break;
            }
            return Math.Min(i, lineEnd);
        }
        while (i < lineEnd)
        {
            var c = doc.GetCharAt(i);
            if (char.IsWhiteSpace(c) || c is ',' or ':' or '}' or ']') break;
            i++;
        }
        return i == start ? Math.Min(start + 1, lineEnd) : i;
    }
}
