using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TablePlsu.Core.Completion;

namespace TablePlsu.App.Features.Completion;

/// <summary>
/// The autocomplete list: a themed border around a virtualized, recycling ListBox of fixed-height owner-drawn rows.
/// One instance per UI thread (<see cref="Shared"/>), shown inside the owning editor's window through a
/// <see cref="CompletionAdorner"/> — no top-level window, so opening it costs a layout pass, not an HWND. Rows are
/// reusable <see cref="SqlCompletionData"/> slots: a refresh repoints them instead of rebuilding the list.
/// Never takes keyboard focus (the editor keeps it); a click selects, a double-click accepts.
/// </summary>
public sealed class CompletionPopup : Border
{
    [ThreadStatic] private static CompletionPopup? _shared;

    /// <summary>The popup of this UI thread (created on first use, then moved between editors).</summary>
    public static CompletionPopup Shared => _shared ??= new CompletionPopup();

    private readonly ListBox _list;
    private readonly ObservableCollection<SqlCompletionData> _slots = [];
    private readonly Stack<SqlCompletionData> _pool = new();
    private ScrollViewer? _scroll;

    /// <summary>The completion that currently shows the popup (it receives clicks).</summary>
    internal SqlCompletion? Owner { get; set; }

    private CompletionPopup()
    {
        Width = 420;
        MaxHeight = 320;
        BorderThickness = new Thickness(1);
        Padding = new Thickness(0);
        SetResourceReference(BackgroundProperty, "c.app.sidebar");
        SetResourceReference(BorderBrushProperty, "c.app.border");
        SetResourceReference(TextElement.ForegroundProperty, "c.app.sidebarForeground");

        _list = new ListBox
        {
            Focusable = false,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            ItemContainerStyle = ItemStyle,
            ItemsSource = _slots,
        };
        _list.SetResourceReference(BackgroundProperty, "c.app.sidebar");
        _list.SetResourceReference(Control.ForegroundProperty, "c.app.sidebarForeground");
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(_list, ScrollUnit.Item);
        KeyboardNavigation.SetIsTabStop(_list, false);
        AutomationProperties.SetName(_list, "Completions");
        AutomationProperties.SetAutomationId(_list, "CompletionList");
        _list.PreviewMouseLeftButtonDown += OnMouseDown;
        Child = _list;
    }

    /// <summary>The rows (slots) in display order.</summary>
    public IReadOnlyList<SqlCompletionData> Items => _slots;

    public ListBox List => _list;

    public int SelectedIndex => _list.SelectedIndex;

    public SqlCompletionData? SelectedItem => _list.SelectedItem as SqlCompletionData;

    /// <summary>Rows that fit in the viewport (PageUp / PageDown step).</summary>
    public int VisibleItemCount
    {
        get
        {
            var sv = Scroll();
            return sv is null || sv.ViewportHeight <= 0 ? 10 : Math.Max(1, (int)sv.ViewportHeight);   // item scrolling: viewport is in rows
        }
    }

    /// <summary>Shows <paramref name="items"/>, first row selected and scrolled into view. Only realized rows whose item changed repaint.</summary>
    internal void SetItems(IReadOnlyList<CompletionItem> items)
    {
        var n = items.Count;
        var keep = Math.Min(n, _slots.Count);
        for (var i = 0; i < keep; i++) _slots[i].Set(items[i]);
        while (_slots.Count > n)
        {
            var last = _slots.Count - 1;
            _pool.Push(_slots[last]);
            _slots.RemoveAt(last);
        }
        for (var i = _slots.Count; i < n; i++)
        {
            SqlCompletionData slot;
            if (_pool.Count > 0) { slot = _pool.Pop(); slot.Set(items[i]); }
            else slot = new SqlCompletionData(items[i]);
            _slots.Add(slot);
        }
        if (n == 0) return;
        if (_list.SelectedIndex != 0) _list.SelectedIndex = 0;
        if (Scroll() is { VerticalOffset: > 0 } sv) sv.ScrollToTop();
    }

    private Task? _warmUp;

    /// <summary>Completed once <see cref="WarmUp"/> has run (null before it was asked for).</summary>
    internal Task? WarmUpTask => _warmUp;

    /// <summary>
    /// Pays the popup's one-time costs ahead of the first show, at ApplicationIdle in small steps: realize rows for a
    /// sample list (templates, containers, row layout), then draw them once off-screen (text formatting, fonts). The
    /// sample rows stay as slots, so the first real list only repoints them. Idempotent.
    /// </summary>
    internal Task WarmUp()
    {
        if (_warmUp is not null) return _warmUp;
        var d = Dispatcher;
        return _warmUp = Run();

        async Task Run()
        {
            await d.InvokeAsync(() =>
            {
                if (_slots.Count > 0) return;
                CompletionKind[] kinds = [CompletionKind.Keyword, CompletionKind.Table, CompletionKind.Column, CompletionKind.Function, CompletionKind.Schema];
                var sample = new CompletionItem[16];
                for (var i = 0; i < sample.Length; i++) sample[i] = new CompletionItem(kinds[i % kinds.Length], "warm_up_" + i, "warm_up_" + i, "detail");
                SetItems(sample);
                Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Arrange(new Rect(DesiredSize));
            }, DispatcherPriority.ApplicationIdle);
            await d.InvokeAsync(() =>
            {
                if (ActualWidth <= 0 || ActualHeight <= 0) return;
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(8, 8, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(this);
            }, DispatcherPriority.ApplicationIdle);
        }
    }

    /// <summary>Selects row <paramref name="index"/> (clamped) and scrolls it into view.</summary>
    internal void Select(int index)
    {
        if (_slots.Count == 0) return;
        index = Math.Clamp(index, 0, _slots.Count - 1);
        _list.SelectedIndex = index;
        _list.ScrollIntoView(_slots[index]);
    }

    private ScrollViewer? Scroll()
    {
        if (_scroll is not null) return _scroll;
        if (!_list.ApplyTemplate() && VisualTreeHelper.GetChildrenCount(_list) == 0) return null;
        return _scroll = FindScroll(_list);
    }

    private static ScrollViewer? FindScroll(DependencyObject d)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is ScrollViewer sv) return sv;
            if (FindScroll(c) is { } found) return found;
        }
        return null;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Select / accept without taking focus from the editor; scroll bar clicks pass through.
        if (ItemsControl.ContainerFromElement(_list, e.OriginalSource as DependencyObject) is not ListBoxItem container) return;
        var index = _list.ItemContainerGenerator.IndexFromContainer(container);
        if (index < 0) return;
        _list.SelectedIndex = index;
        e.Handled = true;
        if (e.ClickCount >= 2) Owner?.AcceptSelected();
    }

    private static Style? _itemStyle;

    private static Style ItemStyle => _itemStyle ??= BuildItemStyle();

    /// <summary>
    /// Row container: Border "Bd" (hover / selection background from the theme) around a <see cref="CompletionRow"/>.
    /// Built in code: parsing the equivalent XAML at runtime costs tens of milliseconds the first time.
    /// </summary>
    private static Style BuildItemStyle()
    {
        var bd = new FrameworkElementFactory(typeof(Border), "Bd");
        bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        bd.SetValue(SnapsToDevicePixelsProperty, true);
        bd.AppendChild(new FrameworkElementFactory(typeof(CompletionRow)));
        var template = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = bd };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("c.app.hover"), "Bd"));
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("c.app.sidebarSelection"), "Bd"));
        template.Triggers.Add(hover);
        template.Triggers.Add(selected);
        template.Seal();

        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("c.app.sidebarForeground")));
        style.Setters.Add(new Setter(FocusableProperty, false));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Seal();
        return style;
    }
}

/// <summary>
/// One owner-drawn popup row (kind glyph, name, right-aligned muted detail), fixed height from the font. Draws its
/// DataContext slot and repaints when the slot is repointed — no per-row child elements or bindings.
/// </summary>
public sealed class CompletionRow : FrameworkElement
{
    public static readonly DependencyProperty GlyphBrushProperty = DependencyProperty.Register(
        nameof(GlyphBrush), typeof(Brush), typeof(CompletionRow), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MutedBrushProperty = DependencyProperty.Register(
        nameof(MutedBrush), typeof(Brush), typeof(CompletionRow), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(CompletionRow),
        new FrameworkPropertyMetadata(SystemColors.ControlTextBrush, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(typeof(CompletionRow),
        new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(typeof(CompletionRow),
        new FrameworkPropertyMetadata(SystemFonts.MessageFontSize, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    private const double PadLeft = 4, PadRight = 6, GlyphWidth = 18, GlyphGap = 6, DetailGap = 12, DetailMax = 200;

    private SqlCompletionData? _slot;
    private CompletionKind? _glyphKind;

    public CompletionRow()
    {
        SetResourceReference(MutedBrushProperty, "c.app.mutedForeground");
        DataContextChanged += (_, e) =>
        {
            if (_slot is not null) _slot.Changed -= Repaint;
            _slot = e.NewValue as SqlCompletionData;
            if (_slot is not null) _slot.Changed += Repaint;
            Repaint();
        };
    }

    public Brush? GlyphBrush { get => (Brush?)GetValue(GlyphBrushProperty); set => SetValue(GlyphBrushProperty, value); }

    public Brush? MutedBrush { get => (Brush?)GetValue(MutedBrushProperty); set => SetValue(MutedBrushProperty, value); }

    private void Repaint()
    {
        if (_slot is { } s && s.Item.Kind != _glyphKind)
        {
            _glyphKind = s.Item.Kind;
            SetResourceReference(GlyphBrushProperty, SqlCompletionData.GlyphBrushKey(s.Item.Kind));
        }
        InvalidateVisual();
    }

    private double RowHeight()
    {
        var family = (FontFamily)GetValue(FontFamilyProperty);
        var size = (double)GetValue(FontSizeProperty);
        return Math.Ceiling(size * family.LineSpacing) + 2;
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, RowHeight());

    protected override void OnRender(DrawingContext dc)
    {
        if (_slot is null) return;
        var item = _slot.Item;
        var family = (FontFamily)GetValue(FontFamilyProperty);
        var size = (double)GetValue(FontSizeProperty);
        var fg = (Brush)GetValue(ForegroundProperty);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var culture = CultureInfo.CurrentUICulture;
        var width = ActualWidth;
        var height = ActualHeight;

        var glyph = new FormattedText(SqlCompletionData.Glyph(item.Kind), culture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), size, GlyphBrush ?? fg, dpi)
        { TextAlignment = TextAlignment.Center, MaxTextWidth = GlyphWidth };
        dc.DrawText(glyph, new Point(PadLeft, (height - glyph.Height) / 2));

        var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var right = width - PadRight;
        if (!string.IsNullOrEmpty(item.Detail))
        {
            var detail = new FormattedText(item.Detail, culture, FlowDirection.LeftToRight, typeface, size, MutedBrush ?? fg, dpi)
            { MaxTextWidth = DetailMax, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            var w = Math.Min(detail.WidthIncludingTrailingWhitespace, DetailMax);
            dc.DrawText(detail, new Point(right - w, (height - detail.Height) / 2));
            right -= w + DetailGap;
        }

        var nameX = PadLeft + GlyphWidth + GlyphGap;
        if (right - nameX < 1) return;
        var name = new FormattedText(item.Display, culture, FlowDirection.LeftToRight, typeface, size, fg, dpi)
        { MaxTextWidth = right - nameX, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawText(name, new Point(nameX, (height - name.Height) / 2));
    }
}

/// <summary>
/// Hosts the shared <see cref="CompletionPopup"/> in an editor's adorner layer, at the caret: below the line, or above
/// it when there is no room below inside the window, and kept inside the window horizontally.
/// </summary>
public sealed class CompletionAdorner : Adorner
{
    private UIElement? _child;
    private Point _below, _above;   // anchor points in the adorned element's coordinates

    public CompletionAdorner(UIElement adornedElement) : base(adornedElement)
    {
        Visibility = Visibility.Collapsed;
    }

    public UIElement? Child
    {
        get => _child;
        set
        {
            if (ReferenceEquals(_child, value)) return;
            if (_child is not null) RemoveVisualChild(_child);
            _child = value;
            if (_child is not null) AddVisualChild(_child);
            InvalidateMeasure();
        }
    }

    /// <summary>Places the popup's top-left at <paramref name="below"/>, or its bottom-left at <paramref name="above"/>.</summary>
    public void SetAnchor(Point below, Point above)
    {
        if (_below == below && _above == above) return;
        _below = below;
        _above = above;
        InvalidateArrange();
    }

    protected override int VisualChildrenCount => _child is null ? 0 : 1;

    protected override Visual GetVisualChild(int index) => _child ?? throw new ArgumentOutOfRangeException(nameof(index));

    protected override Size MeasureOverride(Size constraint)
    {
        _child?.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return AdornedElement.RenderSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_child is null) return finalSize;
        var size = _child.DesiredSize;
        var pos = _below;
        if (VisualTreeHelper.GetParent(this) is UIElement layer && layer.RenderSize is { Width: > 0, Height: > 0 } bounds
            && AdornedElement.IsDescendantOf(layer))
        {
            var origin = AdornedElement.TranslatePoint(default, layer);
            var below = _below + (Vector)origin;
            var above = _above + (Vector)origin;
            if (below.Y + size.Height > bounds.Height && above.Y - size.Height >= 0) below.Y = above.Y - size.Height;
            below.X = Math.Max(0, Math.Min(below.X, bounds.Width - size.Width));
            pos = below - (Vector)origin;
        }
        _child.Arrange(new Rect(pos, size));
        return finalSize;
    }
}
