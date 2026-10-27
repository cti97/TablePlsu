using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace TablePlsu.App.Features.Results;

/// <summary>
/// The lightweight display element of every result-grid cell (instead of a bound TextBlock). It reads
/// <c>((object?[])DataContext)[ColumnIndex]</c> directly when the row is (re)used, formats it with
/// <see cref="CellFormatter.Current"/> and draws it as a cached <see cref="GlyphRun"/> (FormattedText fallback for
/// characters the font lacks or complex scripts). No binding, no text layout objects: this is what keeps scrolling
/// 100k × 30 grids smooth. NULL is drawn with <see cref="NullForeground"/> (c.grid.nullForeground).
/// </summary>
public sealed class CellText : FrameworkElement
{
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(CellText),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(typeof(CellText),
        new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure, OnTextAffectingChanged));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(typeof(CellText),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure, OnTextAffectingChanged));

    public static readonly DependencyProperty NullForegroundProperty = DependencyProperty.Register(nameof(NullForeground), typeof(Brush), typeof(CellText),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(nameof(Padding), typeof(Thickness), typeof(CellText),
        new FrameworkPropertyMetadata(new Thickness(4, 2, 4, 2), FrameworkPropertyMetadataOptions.AffectsMeasure));

    private string? _text;
    private bool _isNull;
    private bool _dirty = true;
    private GlyphRun? _run;
    private FormattedText? _formatted;
    private double _textWidth;
    private double _runPixelsPerDip;

    public CellText(int columnIndex, string? dataType, bool alignRight)
    {
        ColumnIndex = columnIndex;
        DataType = dataType;
        AlignRight = alignRight;
        Focusable = false;
        SnapsToDevicePixels = true;
        ClipToBounds = true;
        DataContextChanged += (_, _) => Refresh();
        SetResourceReference(NullForegroundProperty, "c.grid.nullForeground");
        SetResourceReference(PaddingProperty, GridResources.CellPaddingKey);
    }

    public int ColumnIndex { get; }
    public string? DataType { get; }
    public bool AlignRight { get; }

    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public Brush NullForeground { get => (Brush)GetValue(NullForegroundProperty); set => SetValue(NullForegroundProperty, value); }
    public Thickness Padding { get => (Thickness)GetValue(PaddingProperty); set => SetValue(PaddingProperty, value); }

    /// <summary>The displayed text (NULL text for null).</summary>
    public string Text
    {
        get
        {
            EnsureText();
            return _text!;
        }
    }

    public bool IsNull
    {
        get
        {
            EnsureText();
            return _isNull;
        }
    }

    /// <summary>Re-reads the value from the row (call after changing the row array in place).</summary>
    public void Refresh()
    {
        _dirty = true;
        _run = null;
        _formatted = null;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private static void OnTextAffectingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (CellText)d;
        c._run = null;
        c._formatted = null;
    }

    private void EnsureText()
    {
        if (!_dirty) return;
        _dirty = false;
        var value = DataContext is object?[] row && ColumnIndex < row.Length ? row[ColumnIndex] : null;
        _isNull = value is null or DBNull;
        _text = CellFormatter.Current.Display(value, DataType);
    }

    private void EnsureVisual()
    {
        EnsureText();
        var ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if ((_run is not null || _formatted is not null) && ppd == _runPixelsPerDip) return;
        _runPixelsPerDip = ppd;
        _run = null;
        _formatted = null;
        var text = _text!;
        var size = FontSize;
        var glyphFace = GlyphCache.Get(FontFamily);
        if (glyphFace is not null && TryBuildRun(glyphFace, text, size, ppd, out var run, out var width))
        {
            _run = run;
            _textWidth = width;
            return;
        }
        _formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), size, Brushes.Black, ppd)
        {
            MaxLineCount = 1,
            Trimming = TextTrimming.None,
        };
        _textWidth = _formatted.WidthIncludingTrailingWhitespace;
    }

    private static bool TryBuildRun(GlyphTypeface face, string text, double size, double ppd, out GlyphRun? run, out double width)
    {
        run = null;
        width = 0;
        if (text.Length == 0) return true;
        var map = face.CharacterToGlyphMap;
        var advances = face.AdvanceWidths;
        var glyphs = new ushort[text.Length];
        var widths = new double[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            // Complex scripts (Hebrew, Arabic, Indic…) and surrogates need shaping: let FormattedText handle them.
            if (ch >= '֐' && ch != '↵' && ch != '…') return false;
            if (!map.TryGetValue(ch, out var g)) return false;
            glyphs[i] = g;
            var w = advances[g] * size;
            widths[i] = w;
            width += w;
        }
        run = new GlyphRun(face, 0, false, size, (float)ppd, glyphs, new Point(0, 0), widths, null, null, null, null, null, null);
        return true;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureVisual();
        var p = Padding;
        var line = Math.Ceiling(FontSize * (FontFamily.LineSpacing > 0 ? FontFamily.LineSpacing : 1.33));
        return new Size(_textWidth + p.Left + p.Right, line + p.Top + p.Bottom);
    }

    protected override Size ArrangeOverride(Size finalSize) => finalSize;

    protected override void OnRender(DrawingContext dc)
    {
        EnsureVisual();
        var p = Padding;
        var brush = _isNull ? NullForeground : Foreground;
        var height = RenderSize.Height;
        var x = AlignRight ? Math.Max(p.Left, RenderSize.Width - p.Right - _textWidth) : p.Left;
        if (_run is not null)
        {
            var face = _run.GlyphTypeface;
            var size = _run.FontRenderingEmSize;
            var lineHeight = face.Height * size;
            var top = Math.Max(p.Top, (height - lineHeight) / 2);
            var baseline = Math.Round(top + face.Baseline * size);
            dc.PushTransform(new TranslateTransform(Math.Round(x), baseline));
            dc.DrawGlyphRun(brush, _run);
            dc.Pop();
        }
        else if (_formatted is not null)
        {
            _formatted.SetForegroundBrush(brush);
            var top = Math.Max(p.Top, (height - _formatted.Height) / 2);
            dc.DrawText(_formatted, new Point(Math.Round(x), Math.Round(top)));
        }
    }

    /// <summary>GlyphTypeface per font family (regular weight), resolved once.</summary>
    private static class GlyphCache
    {
        private static readonly Dictionary<FontFamily, GlyphTypeface?> Cache = [];

        public static GlyphTypeface? Get(FontFamily family)
        {
            if (Cache.TryGetValue(family, out var face)) return face;
            var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            face = typeface.TryGetGlyphTypeface(out var g) ? g : null;
            Cache[family] = face;
            return face;
        }
    }
}
