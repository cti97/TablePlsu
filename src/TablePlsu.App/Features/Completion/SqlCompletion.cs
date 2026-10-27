using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
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
/// <item>Accept with Enter and/or Tab (<c>editor.completeKey</c>), or double-click. Up/Down, Ctrl+N/Ctrl+P, PageUp/PageDown
/// move. Esc closes the popup, and opens it when closed (Ctrl+Space also opens).</item>
/// <item>Auto-closes <c>( ' "</c> when <c>editor.autocomplete.autoClosePairs</c> is on (typing the closer steps over it,
/// Backspace between an empty pair deletes both).</item>
/// <item>Lazy schemas: when the answer needs a schema that is not loaded yet, it is loaded in the background and the open
/// popup refreshes.</item>
/// </list>
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
    private readonly Dictionary<CompletionItem, SqlCompletionData> _dataCache = [];
    private CompletionWindow? _window;
    private bool _scheduled, _pendingOpen, _pendingExplicit, _pendingAfterSpace;
    private bool _disposed;

    /// <summary>Maximum rows in the popup.</summary>
    public int MaxItems { get; set; } = 100;

    /// <summary>The instance whose popup is open (for the Ctrl+N / Ctrl+P command overrides).</summary>
    public static SqlCompletion? Active { get; private set; }

    public TextEditor Editor => _editor;

    public bool IsOpen => _window is not null;

    public CompletionWindow? Window => _window;

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
        if (_area.Document is { } doc)
        {
            doc.Changed += OnDocumentChanged;
            _trackedDocs.Add(doc);
        }
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
        foreach (var d in _trackedDocs) d.Changed -= OnDocumentChanged;
        _trackedDocs.Clear();
        if (_cache is not null) _cache.Changed -= OnMetadataChanged;
    }

    // ───────────────────────── public actions ─────────────────────────

    /// <summary>Opens the popup at the caret (Esc / Ctrl+Space), even with nothing typed.</summary>
    public void Open() => Refresh(open: true, explicitOpen: true, afterSpace: false);

    public void Close()
    {
        var w = _window;
        if (w is null) return;
        _window = null;
        if (Active == this) Active = null;
        w.Close();
    }

    /// <summary>Moves the popup selection by <paramref name="delta"/> rows (wraps at the ends).</summary>
    public void MoveSelection(int delta)
    {
        if (_window?.CompletionList.ListBox is not { } lb || lb.Items.Count == 0) return;
        var i = lb.SelectedIndex + delta;
        if (i < 0) i = lb.Items.Count - 1;
        else if (i >= lb.Items.Count) i = 0;
        lb.SelectedIndex = i;
        lb.ScrollIntoView(lb.SelectedItem);
    }

    /// <summary>Accepts the selected item (as Enter/Tab would).</summary>
    public void AcceptSelected() => _window?.CompletionList.RequestInsertion(EventArgs.Empty);

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

        if (_window is not null)
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
            }
            return;
        }

        if (key == Key.Escape && mods == ModifierKeys.None || key == Key.Space && mods == ModifierKeys.Control)
        {
            if (_settings().Autocomplete.Disabled) return;
            Open();
            if (_window is not null || key == Key.Space) e.Handled = true;
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
            Schedule(open: _window is null, afterSpace: true);
        else if (_window is not null)
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
        if (_window is not null) Schedule(open: false, afterSpace: false);
    }

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_window is not null) Schedule(open: false, afterSpace: false);
    }

    private void OnDocumentReplaced(object? sender, EventArgs e)
    {
        Close();
        // TextArea.DocumentChanged: the old document is gone; follow the new one.
        foreach (var d in _trackedDocs) d.Changed -= OnDocumentChanged;
        _trackedDocs.Clear();
        if (_area.Document is { } doc)
        {
            doc.Changed += OnDocumentChanged;
            _trackedDocs.Add(doc);
        }
    }

    private readonly List<TextDocument> _trackedDocs = [];

    private void OnMetadataChanged(object? sender, MetadataChangedEventArgs e)
    {
        var dispatcher = _editor.Dispatcher;
        dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            _requestedSchemas.Clear();
            if (_window is not null) Schedule(open: false, afterSpace: false);
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

            if (_window is null)
            {
                if (!open && !explicitOpen) return;
                CreateWindow();
            }
            var w = _window!;
            w.StartOffset = result.ReplaceStart;
            w.EndOffset = caret;
            SetItems(w, items);
        }
        finally
        {
            LastRefreshMs = sw.Elapsed.TotalMilliseconds;
            Refreshed?.Invoke(this, EventArgs.Empty);
        }
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
                    if (!_disposed && _window is not null) Schedule(open: false, afterSpace: false);
                }, DispatcherPriority.Background);
            }, TaskScheduler.Default);
        }
    }

    private void CreateWindow()
    {
        var w = new CompletionWindow(_area)
        {
            CloseAutomatically = true,
            CloseWhenCaretAtBeginning = false,
            Width = 420,
            MaxHeight = 320,
            SizeToContent = SizeToContent.Height,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
        };
        w.SetResourceReference(Control.BackgroundProperty, "c.app.sidebar");
        w.SetResourceReference(Control.ForegroundProperty, "c.app.sidebarForeground");
        w.SetResourceReference(Control.BorderBrushProperty, "c.app.border");
        var list = w.CompletionList;
        list.IsFiltering = false;
        list.SetResourceReference(Control.BackgroundProperty, "c.app.sidebar");
        list.SetResourceReference(Control.ForegroundProperty, "c.app.sidebarForeground");
        list.Loaded += (_, _) => StyleList(list);
        list.ApplyTemplate();
        StyleList(list);
        w.Closed += (_, _) =>
        {
            if (_window == w) _window = null;
            if (Active == this && _window is null) Active = null;
        };
        _window = w;
        Active = this;
        w.Show();
    }

    private static Style? _itemStyle;

    private static void StyleList(CompletionList list)
    {
        if (list.ListBox is not { } lb || lb.ItemContainerStyle is not null) return;
        lb.ItemContainerStyle = _itemStyle ??= (Style)XamlReader.Parse(ItemStyleXaml);
        lb.SetResourceReference(Control.BackgroundProperty, "c.app.sidebar");
        lb.SetResourceReference(Control.ForegroundProperty, "c.app.sidebarForeground");
        lb.BorderThickness = new Thickness(0);
        ScrollViewer.SetHorizontalScrollBarVisibility(lb, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(lb, true);
        VirtualizingPanel.SetVirtualizationMode(lb, VirtualizationMode.Recycling);
    }

    private const string ItemStyleXaml = """
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ListBoxItem">
          <Setter Property="Foreground" Value="{DynamicResource c.app.sidebarForeground}" />
          <Setter Property="HorizontalContentAlignment" Value="Stretch" />
          <Setter Property="Padding" Value="4,1,6,1" />
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="ListBoxItem">
                <Border x:Name="Bd" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Background="Transparent" Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
                  <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center" />
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True">
                    <Setter TargetName="Bd" Property="Background" Value="{DynamicResource c.app.hover}" />
                  </Trigger>
                  <Trigger Property="IsSelected" Value="True">
                    <Setter TargetName="Bd" Property="Background" Value="{DynamicResource c.app.sidebarSelection}" />
                  </Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """;

    private void SetItems(CompletionWindow w, IReadOnlyList<CompletionItem> items)
    {
        var data = w.CompletionList.CompletionData;
        var same = data.Count == items.Count;
        for (var i = 0; same && i < items.Count; i++)
            same = ((SqlCompletionData)data[i]).Item == items[i];
        if (!same)
        {
            if (_dataCache.Count > 2000) _dataCache.Clear();
            data.Clear();
            foreach (var item in items)
            {
                if (!_dataCache.TryGetValue(item, out var d)) _dataCache[item] = d = new SqlCompletionData(item, this);
                data.Add(d);
            }
        }
        var lb = w.CompletionList.ListBox;
        if (lb is not null && lb.Items.Count > 0)
        {
            lb.SelectedIndex = 0;
            lb.ScrollIntoView(lb.Items[0]);
        }
        else if (data.Count > 0) w.CompletionList.SelectedItem = data[0];
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
