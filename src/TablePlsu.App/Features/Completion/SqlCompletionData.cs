using TablePlsu.Core.Completion;

namespace TablePlsu.App.Features.Completion;

/// <summary>
/// One popup row. Rows are reusable slots: the popup keeps one per visible position and swaps <see cref="Item"/> on
/// every refresh, so a keystroke allocates no row objects and the list's recycled containers stay in place.
/// <see cref="ToString"/> is the UI Automation name of the row (name and detail).
/// </summary>
public sealed class SqlCompletionData
{
    private CompletionItem _item;

    internal SqlCompletionData(CompletionItem item) => _item = item;

    public CompletionItem Item => _item;

    /// <summary>The row text (the item's display name).</summary>
    public string Text => _item.Display;

    /// <summary>Raised when the slot shows a different item (the realized row repaints).</summary>
    internal event Action? Changed;

    /// <summary>Points the slot at <paramref name="item"/>; true (and <see cref="Changed"/>) when it differs.</summary>
    internal bool Set(CompletionItem item)
    {
        if (ReferenceEquals(_item, item)) return false;
        var same = _item == item;
        _item = item;
        if (same) return false;
        Changed?.Invoke();
        return true;
    }

    public override string ToString() => _item.Detail is null ? _item.Display : _item.Display + ", " + _item.Detail;

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
}
