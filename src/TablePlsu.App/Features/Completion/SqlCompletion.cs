using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Completion;
using TablePlsu.Core.Metadata;
using TablePlsu.Core.Settings;

namespace TablePlsu.App.Features.Completion;

/// <summary>
/// Autocomplete for an AvalonEdit SQL editor, fed by <see cref="CompletionEngine"/> and the workspace metadata cache.
/// <list type="bullet">
/// <item>Pops up while typing identifiers, after <c>.</c>, and after a space that follows FROM/JOIN/UPDATE/INTO/TABLE.
/// The list is recomputed (not filtered) on every keystroke so ranking stays the engine's.</item>
/// <item>Accept with Enter and/or Tab (<c>editor.completeKey</c>), or double-click. Up/Down, PageUp/PageDown, Home/End,
/// Ctrl+N/Ctrl+P move. Esc closes the popup, and opens it when closed (Ctrl+Space also opens).</item>
/// <item>Closes when the editor loses focus or is hidden, when the caret leaves the word, and when scrolling takes
/// the word out of view.</item>
/// <item>Auto-closes <c>( ' "</c> when <c>editor.autocomplete.autoClosePairs</c> is on (typing the closer steps over it,
/// Backspace between an empty pair deletes both).</item>
/// <item>Lazy schemas: when the answer needs a schema that is not loaded yet, it is loaded in the background and the open
/// popup refreshes.</item>
/// </list>
/// The popup is the per-thread <see cref="CompletionPopup"/> shown in this editor's adorner layer (no window per open).
/// Usage: <c>var completion = SqlCompletion.Attach(editor, workspace);</c> and dispose it with the editor.
/// </summary>
public sealed class SqlCompletion : IDisposable
{
    private readonly TextEditor _editor;
    private readonly TextArea _area;
    private readonly Func<DbMetadata> _metadata;
    private readonly Func<EditorSettings> _settings;
    private readonly Func<string, Task>? _ensureSchema;
    private readonly MetadataCache? _cache;
    private readonly HashSet<string> _requestedSchemas = new(StringComparer.Ordinal);
    private CompletionAdorner? _adorner;
    private AdornerLayer? _layer;
    private bool _open;
    private int _startOffset, _endOffset;
    private bool _scheduled, _pendingOpen, _pendingExplicit, _pendingAfterSpace;
    private bool _disposed;

    /// <summary>Maximum rows in the popup.</summary>
    public int MaxItems { get; set; } = 100;

    /// <summary>The instance whose popup is open (for the Ctrl+N / Ctrl+P command overrides).</summary>
    public static SqlCompletion? Active { get; private set; }

    public TextEditor Editor => _editor;

    public bool IsOpen => _open;

    /// <summary>The popup while it is open here (null when closed).</summary>
    public CompletionPopup? Popup => _open ? CompletionPopup.Shared : null;

    /// <summary>The rows shown (empty when closed).</summary>
    public IReadOnlyList<SqlCompletionData> Items => Popup?.Items ?? [];

    /// <summary>The selected row (null when closed).</summary>
    public SqlCompletionData? SelectedItem => Popup?.SelectedItem;

    /// <summary>Document offset where the replaced word starts / the caret was at the last refresh.</summary>
    public int StartOffset => _startOffset;

    public int EndOffset => _endOffset;

    /// <summary>The last engine answer (diagnostics / tests).</summary>
    public CompletionResult? LastResult { get; private set; }

    /// <summary>Milliseconds the last refresh took (engine + popup update).</summary>
    public double LastRefreshMs { get; private set; }

    /// <summary>Raised after every refresh (tests).</summary>
    public event EventHandler? Refreshed;

    /// <summary>Attaches autocomplete to <paramref name="editor"/> using <paramref name="workspace"/>'s metadata cache.</summary>
    public static SqlCompletion Attach(TextEditor editor, IWorkspace workspace) =>
        new(editor, () => workspace.Metadata.Current, CurrentEditorSettings,
            schema => workspace.Metadata.EnsureSchemaLoadedAsync(schema), workspace.Metadata);

    /// <summary>Test/advanced overload: explicit metadata and settings providers.</summary>
    public static SqlCompletion Attach(TextEditor editor, Func<DbMetadata> metadata, Func<EditorSettings>? settings = null, Func<string, Task>? ensureSchema = null) =>
        new(editor, metadata, settings ?? CurrentEditorSettings, ensureSchema, null);

    private static EditorSettings CurrentEditorSettings() => AppServices.IsInitialized ? AppServices.Current.Editor : new EditorSettings();

    private SqlCompletion(TextEditor editor, Func<DbMetadata> metadata, Func<EditorSettings> settings, Func<string, Task>? ensureSchema, MetadataCache? cache)
    {
        _editor = editor;
        _area = editor.TextArea;
        _metadata = metadata;
        _settings = settings;
        _ensureSchema = ensureSchema;
        _cache = cache;

        _editor.PreviewKeyDown += OnPreviewKeyDown;
        _area.TextEntering += OnTextEntering;
        _area.TextEntered += OnTextEntered;
        _area.Caret.PositionChanged += OnCaretChanged;
        _area.DocumentChanged += OnDocumentReplaced;
        _area.LostKeyboardFocus += OnLostFocus;
        _area.TextView.ScrollOffsetChanged += OnScrolled;
        _editor.IsVisibleChanged += OnVisibleChanged;
        _editor.Loaded += OnEditorLoaded;
        if (_editor.IsLoaded) SchedulePrepare();
        Track(_area.Document);
        if (_cache is not null) _cache.Changed += OnMetadataChanged;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
        _editor.PreviewKeyDown -= OnPreviewKeyDown;
        _area.TextEntering -= OnTextEntering;
        _area.TextEntered -= OnTextEntered;
        _area.Caret.PositionChanged -= OnCaretChanged;
        _area.DocumentChanged -= OnDocumentReplaced;
        _area.LostKeyboardFocus -= OnLostFocus;
        _area.TextView.ScrollOffsetChanged -= OnScrolled;
        _editor.IsVisibleChanged -= OnVisibleChanged;
        _editor.Loaded -= OnEditorLoaded;
        Track(null);
        if (_cache is not null) _cache.Changed -= OnMetadataChanged;
        var popup = CompletionPopup.Shared;
        if (popup.Owner == this) popup.Owner = null;
        if (_adorner is not null)
        {
            _adorner.Child = null;
            _layer?.Remove(_adorner);
            _adorner = null;
            _layer = null;
        }
    }

    // ───────────────────────── public actions ─────────────────────────

    /// <summary>Opens the popup at the caret (Esc / Ctrl+Space), even with nothing typed.</summary>
    public void Open() => Refresh(open: true, explicitOpen: true, afterSpace: false);

    public void Close()
    {
        if (!_open) return;
        _open = false;
        if (Active == this) Active = null;
        if (_adorner is not null) _adorner.Visibility = Visibility.Collapsed;
    }

    /// <summary>Moves the popup selection by <paramref name="delta"/> rows (wraps at the ends).</summary>
    public void MoveSelection(int delta)
    {
        if (Popup is not { } p || p.Items.Count == 0) return;
        var i = p.SelectedIndex + delta;
        if (i < 0) i = p.Items.Count - 1;
        else if (i >= p.Items.Count) i = 0;
        p.Select(i);
    }

    /// <summary>Moves the popup selection by <paramref name="delta"/> rows, stopping at the ends (arrow keys, paging).</summary>
    private void StepSelection(int delta)
    {
        if (Popup is not { } p || p.Items.Count == 0) return;
        p.Select(p.SelectedIndex + delta);
    }

    /// <summary>Accepts the selected item (as Enter/Tab would): closes the popup, then replaces the word.</summary>
    public void AcceptSelected()
    {
        if (!_open) return;
        var item = SelectedItem?.Item;
        var start = _startOffset;
        var end = _endOffset;
        Close();
        if (item is null || _area.Document is not { } doc) return;
        start = Math.Clamp(start, 0, doc.TextLength);
        end = Math.Clamp(end, start, doc.TextLength);
        Insert(_area, new TextSegment { StartOffset = start, EndOffset = end }, item);
    }

    /// <summary>Runs any scheduled refresh now (tests).</summary>
    public void FlushPending()
    {
        if (_scheduled) RunScheduled();
    }

    // ───────────────────────── events ─────────────────────────

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || !_area.IsKeyboardFocusWithin && !ReferenceEquals(e.OriginalSource, _area)) return;
        if (e.OriginalSource is not TextArea) return;   // e.g. the search panel's text box
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        var completeKey = _settings().CompleteKey;

        if (_open)
        {
            switch (key)
            {
                case Key.Escape when mods == ModifierKeys.None:
                    Close();
                    e.Handled = true;
                    return;
                case Key.Enter when mods == ModifierKeys.None:
                    if (completeKey == CompleteKey.Tab) Close();  // newline instead
                    else { AcceptSelected(); e.Handled = true; }
                    return;
                case Key.Tab when mods == ModifierKeys.None:
                    if (completeKey == CompleteKey.Enter) Close(); // indent instead
                    else { AcceptSelected(); e.Handled = true; }
                    return;
                case Key.N when mods == ModifierKeys.Control:
                    MoveSelection(1);
                    e.Handled = true;
                    return;
                case Key.P when mods == ModifierKeys.Control:
                    MoveSelection(-1);
                    e.Handled = true;
                    return;
                case Key.Down: StepSelection(1); e.Handled = true; return;
                case Key.Up: StepSelection(-1); e.Handled = true; return;
                case Key.PageDown: StepSelection(CompletionPopup.Shared.VisibleItemCount); e.Handled = true; return;
                case Key.PageUp: StepSelection(-CompletionPopup.Shared.VisibleItemCount); e.Handled = true; return;
                case Key.Home: StepSelection(int.MinValue / 2); e.Handled = true; return;
                case Key.End: StepSelection(int.MaxValue / 2); e.Handled = true; return;
            }
            return;
        }

        if (key == Key.Escape && mods == ModifierKeys.None || key == Key.Space && mods == ModifierKeys.Control)
        {
            if (_settings().Autocomplete.Disabled) return;
            Open();
            if (_open || key == Key.Space) e.Handled = true;
            return;
        }

        if (key == Key.Back && mods == ModifierKeys.None && _settings().Autocomplete.AutoClosePairs && _area.Selection.IsEmpty)
        {
            var doc = _area.Document;
            var o = _area.Caret.Offset;
            if (o > 0 && o < doc.TextLength && IsPair(doc.GetCharAt(o - 1), doc.GetCharAt(o)))
            {
                doc.Remove(o - 1, 2);
                e.Handled = true;
            }
        }
    }

    private static bool IsPair(char open, char close) => open == '(' && close == ')' || open == '\'' && close == '\'' || open == '"' && close == '"';

    private void OnTextEntering(object? sender, TextCompositionEventArgs e)
    {
        if (e.Text.Length != 1 || !_settings().Autocomplete.AutoClosePairs || !_area.Selection.IsEmpty) return;
        var c = e.Text[0];
        if (c is not (')' or '\'' or '"')) return;
        var doc = _area.Document;
        var o = _area.Caret.Offset;
        // Step over a closer that is already there.
        if (o < doc.TextLength && doc.GetCharAt(o) == c && (c == ')' || IsOwnClosingQuote(doc, o, c)))
        {
            _area.Caret.Offset = o + 1;
            e.Handled = true;
        }
    }

    /// <summary>True when the quote at <paramref name="o"/> closes a quote opened earlier on the line (odd count before it).</summary>
    private static bool IsOwnClosingQuote(TextDocument doc, int o, char q)
    {
        var line = doc.GetLineByOffset(o);
        var count = 0;
        for (var i = line.Offset; i < o; i++)
            if (doc.GetCharAt(i) == q) count++;
        return count % 2 == 1;
    }

    private void OnTextEntered(object? sender, TextCompositionEventArgs e)
    {
        if (e.Text.Length == 0) return;
        var c = e.Text[^1];
        var settings = _settings();

        if (e.Text.Length == 1 && settings.Autocomplete.AutoClosePairs && c is '(' or '\'' or '"') AutoClose(c);

        if (settings.Autocomplete.Disabled) return;
        if (char.IsLetter(c) || c == '_' || c == '.' || c == '"' && IsOpeningQuotedIdent())
            Schedule(open: true, afterSpace: false);
        else if (c == ' ' && e.Text.Length == 1)
            Schedule(open: !_open, afterSpace: true);
        else if (_open)
            Schedule(open: false, afterSpace: false);
    }

    private bool IsOpeningQuotedIdent()
    {
        var o = _area.Caret.Offset;
        return o >= 1 && (o < 2 || !SqlLexerChars.IsIdentPart(_area.Document.GetCharAt(o - 2)));
    }

    private void AutoClose(char open)
    {
        var doc = _area.Document;
        var o = _area.Caret.Offset;           // just after the typed opener
        var next = o < doc.TextLength ? doc.GetCharAt(o) : '\n';
        if (!(char.IsWhiteSpace(next) || next is ')' or ',' or ';')) return;
        if (open != '(')
        {
            var before = o >= 2 ? doc.GetCharAt(o - 2) : ' ';
            if (SqlLexerChars.IsIdentPart(before) || before == open) return;   // don't, it's, ''
            if (IsOwnClosingQuote(doc, o - 1, open)) return;                  // this quote closes an earlier one
        }
        var close = open == '(' ? ")" : open.ToString();
        doc.Insert(o, close);
        _area.Caret.Offset = o;
    }

    private void OnCaretChanged(object? sender, EventArgs e)
    {
        if (!_open) return;
        // The caret left the word being completed (click elsewhere, arrow past it): close, as AvalonEdit's window did.
        var o = _area.Caret.Offset;
        if (o < _startOffset || o > _endOffset) { Close(); return; }
        Schedule(open: false, afterSpace: false);
    }

    private void OnDocumentChanging(object? sender, DocumentChangeEventArgs e)
    {
        if (!_open) return;
        // Deleting right in front of the word (Backspace after "u.") closes; otherwise the word's ends follow the edit.
        if (e.RemovalLength > 0 && e.Offset + e.RemovalLength == _startOffset) { Close(); return; }
        _startOffset = e.GetNewOffset(_startOffset, AnchorMovementType.BeforeInsertion);
        _endOffset = e.GetNewOffset(_endOffset, AnchorMovementType.AfterInsertion);
    }

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_open) Schedule(open: false, afterSpace: false);
    }

    private void OnDocumentReplaced(object? sender, EventArgs e)
    {
        Close();
        // TextArea.DocumentChanged: the old document is gone; follow the new one.
        Track(_area.Document);
    }

    private TextDocument? _trackedDoc;

    private void Track(TextDocument? doc)
    {
        if (_trackedDoc is not null)
        {
            _trackedDoc.Changing -= OnDocumentChanging;
            _trackedDoc.Changed -= OnDocumentChanged;
        }
        _trackedDoc = doc;
        if (doc is not null)
        {
            doc.Changing += OnDocumentChanging;
            doc.Changed += OnDocumentChanged;
        }
    }

    private void OnLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_open && !_area.IsKeyboardFocusWithin) Close();
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!_editor.IsVisible) Close();
    }

    private void OnScrolled(object? sender, EventArgs e)
    {
        if (!_open) return;
        // Follow the word while it stays in view; close once scrolling takes it out (AvalonEdit's rule).
        if (!PlaceAtWord()) Close();
    }

    private void OnMetadataChanged(object? sender, MetadataChangedEventArgs e)
    {
        var dispatcher = _editor.Dispatcher;
        dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            _requestedSchemas.Clear();
            if (_open) Schedule(open: false, afterSpace: false);
        }, DispatcherPriority.Background);
    }

    // ───────────────────────── refresh ─────────────────────────

    private void Schedule(bool open, bool afterSpace)
    {
        _pendingOpen |= open;
        _pendingAfterSpace |= afterSpace;
        if (_scheduled) return;
        _scheduled = true;
        // Normal priority: runs after AvalonEdit's own caret handling for this keystroke, before the next render.
        _editor.Dispatcher.BeginInvoke(RunScheduled, DispatcherPriority.Normal);
    }

    private void RunScheduled()
    {
        if (!_scheduled || _disposed) return;
        _scheduled = false;
        var open = _pendingOpen;
        var explicitOpen = _pendingExplicit;
        var afterSpace = _pendingAfterSpace;
        _pendingOpen = _pendingExplicit = _pendingAfterSpace = false;
        Refresh(open, explicitOpen, afterSpace);
    }

    private void Refresh(bool open, bool explicitOpen, bool afterSpace)
    {
        if (_disposed || _area.Document is null) return;
        var sw = Stopwatch.StartNew();
        try
        {
            var settings = _settings();
            if (settings.Autocomplete.Disabled) { Close(); return; }

            var doc = _area.Document;
            var caret = _area.Caret.Offset;
            var options = CompletionOptions.From(settings.Autocomplete, MaxItems);
            var result = CompletionEngine.Complete(doc.Text, caret, _metadata(), options);
            LastResult = result;
            RequestSchemas(result);

            var items = result.Items;
            var wordless = result.ReplaceLength == 0 && result.Qualifier.Count == 0;
            if (!explicitOpen && wordless && !(afterSpace && result.Context == CompletionContextKind.Table)
                && result.Context != CompletionContextKind.InsertColumns)
            {
                Close();
                return;
            }
            if (items.Count == 0) { Close(); return; }
            if (!explicitOpen && items.Count == 1 && result.ReplaceLength > 0
                && string.Equals(items[0].InsertText.TrimEnd(), doc.GetText(result.ReplaceStart, result.ReplaceLength), StringComparison.Ordinal))
            {
                Close();   // the word is already complete
                return;
            }

            if (!_open && !open && !explicitOpen) return;
            _startOffset = result.ReplaceStart;
            _endOffset = caret;
            if (!_open && !Show()) return;
            CompletionPopup.Shared.SetItems(items);
            PlaceAtWord();
        }
        finally
        {
            LastRefreshMs = sw.Elapsed.TotalMilliseconds;
            Refreshed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Makes the popup visible here (after <see cref="Adopt"/>). False when the editor is not in a window.</summary>
    private bool Show()
    {
        if (!Adopt()) return false;
        _adorner!.Visibility = Visibility.Visible;
        _open = true;
        Active = this;
        return true;
    }

    private static readonly Lazy<Task> _engineWarmUp = new(() => Task.Run(WarmUpEngine));

    /// <summary>
    /// Once per process, when the first editor appears: runs the engine over a tiny catalog on a pool thread so its
    /// code is compiled (JIT is process-wide) before the first keystroke needs it on the UI thread.
    /// </summary>
    public static Task EngineWarmUp => _engineWarmUp.Value;

    private static void WarmUpEngine()
    {
        var md = new DbMetadata
        {
            SearchPath = ["public"],
            Schemas =
            [
                new SchemaInfo
                {
                    Name = "public", IsLoaded = true,
                    Tables = [new TableInfo { Schema = "public", Name = "items", Columns = [new ColumnInfo { Name = "id", DataType = "integer" }] }],
                    Functions = [new FunctionInfo { Schema = "public", Name = "item_count", Arguments = "x integer", ReturnType = "integer" }],
                },
                new SchemaInfo { Name = "other", IsLoaded = true, Tables = [new TableInfo { Schema = "other", Name = "things" }] },
            ],
        };
        string[] samples = ["sel", "select * from it", "select * from i", "select * from itm", "select i.id from items i where i", "select x from other.t",
                            "select * from items where ite", "insert into items (i", "with c as (select id from items) select * from c join items on c."];
        // MaxItems 1 also takes the top-n heap path that big catalogs use.
        var options = new[] { CompletionOptions.Default, CompletionOptions.Default with { MaxItems = 1 } };
        try
        {
            foreach (var o in options)
            foreach (var sql in samples)
                CompletionEngine.Complete(sql, sql.Length, md, o);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("completion warm-up failed: " + ex);   // only a warm-up; the real call reports its own errors
        }
    }

    private void OnEditorLoaded(object sender, RoutedEventArgs e) => SchedulePrepare();

    private void SchedulePrepare() => _editor.Dispatcher.BeginInvoke(Prepare, DispatcherPriority.ApplicationIdle);

    /// <summary>
    /// Idle-time warm-up when the editor appears: moves the (hidden) shared popup into this editor's adorner layer so the
    /// first popup here does not pay for re-parenting (resource / inherited-property invalidation, template, row repaint).
    /// Skipped while another editor shows it.
    /// </summary>
    private void Prepare()
    {
        if (_disposed || _open || !_editor.IsLoaded) return;
        _ = EngineWarmUp;
        var popup = CompletionPopup.Shared;
        if (popup.Owner is { IsOpen: true }) return;
        if (Adopt()) _ = popup.WarmUp();
    }

    /// <summary>Puts the shared popup (hidden) into this editor's adorner layer. False when the editor is not in a window.</summary>
    private bool Adopt()
    {
        var layer = AdornerLayer.GetAdornerLayer(_editor);
        if (layer is null) return false;
        _adorner ??= new CompletionAdorner(_editor);
        if (!ReferenceEquals(_layer, layer))
        {
            _layer?.Remove(_adorner);
            layer.Add(_adorner);
            _layer = layer;
        }
        var popup = CompletionPopup.Shared;
        if (popup.Owner != this)
        {
            if (popup.Owner is { } previous)
            {
                previous.Close();
                if (previous._adorner is not null) previous._adorner.Child = null;
            }
            popup.Owner = this;
        }
        _adorner.Child = popup;
        return true;
    }

    /// <summary>Anchors the popup under (or above) the start of the word. False when the word is scrolled out of view.</summary>
    private bool PlaceAtWord()
    {
        if (_adorner is null || _area.Document is not { } doc) return false;
        var view = _area.TextView;
        var pos = new TextViewPosition(doc.GetLocation(Math.Clamp(_startOffset, 0, doc.TextLength)));
        var bottom = view.GetVisualPosition(pos, VisualYPosition.LineBottom);
        var top = view.GetVisualPosition(pos, VisualYPosition.LineTop);
        var visible = new Rect(view.HorizontalOffset, view.VerticalOffset, view.ActualWidth, view.ActualHeight);
        if (!visible.Contains(bottom) && !visible.Contains(top)) return false;
        var shift = (Vector)view.TranslatePoint(default, _editor) - view.ScrollOffset;
        _adorner.SetAnchor(bottom + shift, top + shift);
        return true;
    }

    private void RequestSchemas(CompletionResult result)
    {
        if (_ensureSchema is null || result.MissingSchemas.Count == 0) return;
        foreach (var s in result.MissingSchemas)
        {
            if (!_requestedSchemas.Add(s)) continue;
            var schema = s;
            _ensureSchema(schema).ContinueWith(t =>
            {
                if (t.IsFaulted) _ = t.Exception; // observed; nothing to show
                _editor.Dispatcher.BeginInvoke(() =>
                {
                    if (!_disposed && _open) Schedule(open: false, afterSpace: false);
                }, DispatcherPriority.Background);
            }, TaskScheduler.Default);
        }
    }

    /// <summary>Called by an accepted item: replace the word (and the rest of it after the caret), place the caret.</summary>
    internal void Insert(TextArea area, ISegment segment, CompletionItem item)
    {
        var doc = area.Document;
        var start = segment.Offset;
        var end = Math.Max(segment.EndOffset, area.Caret.Offset);
        // Swallow the rest of the identifier after the caret (completing in the middle of a word).
        while (end < doc.TextLength && SqlLexerChars.IsIdentPart(doc.GetCharAt(end))) end++;
        if (item.InsertText.StartsWith('"') && end < doc.TextLength && doc.GetCharAt(end) == '"') end++;
        var text = item.InsertText;
        // Don't double a space that is already there.
        if (text.EndsWith(' ') && end < doc.TextLength && doc.GetCharAt(end) == ' ') text = text[..^1];
        doc.Replace(start, end - start, text);
        area.Caret.Offset = start + text.Length - item.CaretBack;
        if (item.ReopensCompletion)
        {
            _pendingExplicit = true;
            Schedule(open: true, afterSpace: false);
        }
    }
}

/// <summary>Identifier character rules shared with the Core lexer.</summary>
internal static class SqlLexerChars
{
    public static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';
}
