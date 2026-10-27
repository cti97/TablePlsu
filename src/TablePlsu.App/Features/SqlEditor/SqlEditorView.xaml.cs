using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Search;
using TablePlsu.App.Features.Workspace;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Commands;
using TablePlsu.Core.Settings;
using TablePlsu.Core.Themes;

namespace TablePlsu.App.Features.SqlEditor;

/// <summary>
/// The SQL editor tab view: AvalonEdit (theme-generated highlighting, current-statement tint, error underline, search
/// panel, live editor settings) above a splitter, result tabs (Data grid / Message view) and the result bar below.
/// Created once per tab by TabHost and kept while the tab is open.
/// </summary>
public partial class SqlEditorView : UserControl, ISqlEditorView
{
    private readonly SearchPanel _search;
    private readonly CurrentStatementRenderer _currentStatement;
    private readonly ErrorUnderlineRenderer _errorUnderline;
    private SqlEditorTabViewModel? _vm;
    private bool _restored;
    private bool _completionAttached;
    private IDisposable? _completion;
    private bool _highlightCurrent = true;
    private GridLength _resultsHeight = new(1, GridUnitType.Star);
    private DispatcherTimer? _zoomPersistTimer;

    public SqlEditorView()
    {
        InitializeComponent();
        var area = Editor.TextArea;
        var view = area.TextView;

        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        Editor.Options.HighlightCurrentLine = true;
        Editor.Options.EnableRectangularSelection = true;
        Editor.Options.AllowToggleOverstrikeMode = false;
        area.SetResourceReference(TextArea.SelectionBrushProperty, "c.editor.selection");
        area.SelectionForeground = null;
        area.SelectionBorder = null;
        area.SelectionCornerRadius = 0;
        view.SetResourceReference(TextView.CurrentLineBackgroundProperty, "c.editor.currentLine");
        view.CurrentLineBorder = new Pen(Brushes.Transparent, 0);

        _search = SearchPanel.Install(Editor);
        _search.SetResourceReference(SearchPanel.MarkerBrushProperty, "c.editor.selection");

        _currentStatement = new CurrentStatementRenderer(view, () => Editor.CaretOffset, () => _highlightCurrent && Editor.SelectionLength == 0);
        _errorUnderline = new ErrorUnderlineRenderer(view);
        view.BackgroundRenderers.Add(_currentStatement);
        view.BackgroundRenderers.Add(_errorUnderline);

        area.Caret.PositionChanged += (_, _) =>
        {
            view.InvalidateLayer(KnownLayer.Background);
            PushViewState();
        };
        area.SelectionChanged += (_, _) =>
        {
            view.InvalidateLayer(KnownLayer.Background);
            PushViewState();
        };
        view.ScrollOffsetChanged += (_, _) => PushViewState();
        Editor.PreviewMouseWheel += OnEditorMouseWheel;

        ResultsHint.Text = $"{ShortcutText.Tooltip(CommandIds.QueryRunCurrent)}\n{ShortcutText.Tooltip(CommandIds.QueryRunAll)}";

        ApplySettings(AppServices.IsInitialized ? AppServices.Current : new AppSettings());
        ApplyTheme();
        if (AppServices.IsInitialized)
        {
            AppServices.SettingsChanged += OnSettingsChanged;
            AppServices.Theme.ThemeChanged += OnThemeChanged;
        }

        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    /// <summary>The AvalonEdit control (for features and tests).</summary>
    public ICSharpCode.AvalonEdit.TextEditor TextEditor => Editor;

    public Features.Results.ResultGrid ResultGrid => Grid;

    // ---- view-model wiring --------------------------------------------------------------------------------------

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.View = null;
            _vm.ErrorSpanChanged -= OnErrorSpanChanged;
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.Document.Changed -= OnDocumentChanged;
            _vm.Closed -= OnVmClosed;
        }
        _vm = e.NewValue as SqlEditorTabViewModel;
        if (_vm is null) return;
        _restored = false;
        Editor.Document = _vm.Document;
        _vm.View = this;
        _vm.ErrorSpanChanged += OnErrorSpanChanged;
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.Document.Changed += OnDocumentChanged;
        _vm.Closed += OnVmClosed;
        ApplyResultsVisibility();
        ApplyRunButton();
        AttachCompletion();
    }

    /// <summary>Run becomes Cancel (error colour) while a query runs.</summary>
    private void ApplyRunButton()
    {
        var running = _vm?.IsRunning == true;
        RunButton.Content = running ? "■ Cancel" : "▶ Run";
        RunButton.ToolTip = running
            ? ShortcutText.Tooltip(CommandIds.QueryCancel)
            : ShortcutText.Tooltip(_vm?.RunButtonRunsAll == true ? CommandIds.QueryRunAll : CommandIds.QueryRunCurrent);
        if (running)
        {
            RunButton.SetResourceReference(BackgroundProperty, "c.app.error");
            RunButton.SetResourceReference(BorderBrushProperty, "c.app.error");
        }
        else
        {
            RunButton.ClearValue(BackgroundProperty);
            RunButton.ClearValue(BorderBrushProperty);
        }
    }

    /// <summary>
    /// AUTOCOMPLETE MERGE POINT: the single call site for the Completion feature
    /// (<c>TablePlsu.App.Features.Completion.SqlCompletion.Attach(editor, workspace)</c>), reached through
    /// <see cref="SqlEditorHooks.AttachCompletion"/>.
    /// </summary>
    private void AttachCompletion()
    {
        if (_completionAttached || _vm?.Workspace is not { } workspace) return;
        _completionAttached = true;
        try
        {
            _completion = SqlEditorHooks.AttachCompletion?.Invoke(Editor, workspace);
        }
        catch (Exception ex)
        {
            workspace.ConsoleLog.Add("Autocomplete failed to attach: " + ex.Message, ConsoleEntryKind.Error);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _restored) return;
        var doc = _vm.Document;
        var caret = Math.Clamp(_vm.CaretOffset, 0, doc.TextLength);
        var selStart = Math.Clamp(_vm.SelectionStart, 0, doc.TextLength);
        var selLength = Math.Clamp(_vm.SelectionLength, 0, doc.TextLength - selStart);
        if (selLength > 0) Editor.Select(selStart, selLength);
        Editor.CaretOffset = caret;
        var (x, y) = (_vm.ScrollX, _vm.ScrollY);
        Dispatcher.BeginInvoke(() =>
        {
            Editor.ScrollToHorizontalOffset(x);
            Editor.ScrollToVerticalOffset(y);
            _restored = true;
            if (_vm?.IsActive == true) FocusEditor();
        }, DispatcherPriority.Loaded);
    }

    private void PushViewState()
    {
        if (!_restored || _vm is null) return;
        var view = Editor.TextArea.TextView;
        _vm.UpdateViewState(Editor.CaretOffset, Editor.SelectionStart, Editor.SelectionLength, view.HorizontalOffset, view.VerticalOffset);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SqlEditorTabViewModel.IsResultsVisible):
                ApplyResultsVisibility();
                break;
            case nameof(SqlEditorTabViewModel.IsRunning) or nameof(SqlEditorTabViewModel.RunButtonRunsAll):
                ApplyRunButton();
                break;
            case nameof(TabViewModelBase.IsActive) when _vm?.IsActive == true && _restored:
                Dispatcher.BeginInvoke(FocusEditor, DispatcherPriority.Input);
                break;
        }
    }

    private void OnDocumentChanged(object? sender, ICSharpCode.AvalonEdit.Document.DocumentChangeEventArgs e) => _currentStatement.Invalidate();

    private void OnErrorSpanChanged(object? sender, (int Start, int Length)? span)
    {
        if (span is not { } s || _vm is null)
        {
            _errorUnderline.Clear();
            return;
        }
        _errorUnderline.Set(s.Start, s.Length, _vm.Document);
        Editor.Select(s.Start, 0);
        Editor.CaretOffset = s.Start;
        var loc = _vm.Document.GetLocation(s.Start);
        Editor.ScrollTo(loc.Line, loc.Column);
    }

    private void OnVmClosed(object? sender, EventArgs e)
    {
        if (AppServices.IsInitialized)
        {
            AppServices.SettingsChanged -= OnSettingsChanged;
            AppServices.Theme.ThemeChanged -= OnThemeChanged;
        }
        _zoomPersistTimer?.Stop();
        PersistZoom();
        _completion?.Dispose();
        _completion = null;
    }

    // ---- settings and theme -------------------------------------------------------------------------------------

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e) => ApplySettings(e.Settings);

    private void OnThemeChanged(object? sender, ThemeDefinition theme) => ApplyTheme();

    /// <summary>Applies editor.* settings (live on settings reload).</summary>
    public void ApplySettings(AppSettings settings)
    {
        var s = settings.Editor;
        Editor.FontFamily = new FontFamily($"{s.FontFamily}, Cascadia Mono, Consolas");
        if (_zoomPersistTimer is not { IsEnabled: true }) Editor.FontSize = s.FontSize;
        Editor.WordWrap = s.WordWrap;
        Editor.Options.ShowSpaces = s.ShowInvisibles;
        Editor.Options.ShowTabs = s.ShowInvisibles;
        Editor.Options.ShowEndOfLine = s.ShowInvisibles;
        Editor.Options.IndentationSize = s.TabSize;
        Editor.Options.ConvertTabsToSpaces = s.InsertSpaces;
        _highlightCurrent = s.HighlightCurrentQuery;
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    /// <summary>Regenerates the highlighting definition and caret colour from the active theme.</summary>
    public void ApplyTheme()
    {
        var colors = AppServices.IsInitialized ? AppServices.Theme.Current.Colors : new Dictionary<string, string>();
        Editor.SyntaxHighlighting = SqlHighlighting.Create(colors);
        if (TryFindResource("c.editor.caret") is Brush caret) Editor.TextArea.Caret.CaretBrush = caret;
        Editor.TextArea.TextView.Redraw();
    }

    // ---- ISqlEditorView -----------------------------------------------------------------------------------------

    public void FocusEditor()
    {
        if (!IsVisible) return;
        Editor.TextArea.Focus();
        Keyboard.Focus(Editor.TextArea);
    }

    public void OpenFind(bool replace)
    {
        var selected = Editor.SelectionLength > 0 && !Editor.SelectedText.Contains('\n') ? Editor.SelectedText : null;
        if (replace)
        {
            if (!_search.IsClosed) _search.Close();
            ReplaceBar.Visibility = Visibility.Visible;
            if (selected is not null) FindBox.Text = selected;
            else if (string.IsNullOrEmpty(FindBox.Text)) FindBox.Text = _search.SearchPattern ?? "";
            FindBox.Focus();
            FindBox.SelectAll();
            return;
        }
        ReplaceBar.Visibility = Visibility.Collapsed;
        if (selected is not null) _search.SearchPattern = selected;
        _search.Open();
        _search.Reactivate();
    }

    public void FindNext(bool backwards)
    {
        if (ReplaceBar.Visibility == Visibility.Visible)
        {
            FindInReplaceBar(backwards);
            return;
        }
        if (_search.IsClosed && string.IsNullOrEmpty(_search.SearchPattern))
        {
            OpenFind(replace: false);
            return;
        }
        if (backwards) _search.FindPrevious();
        else _search.FindNext();
    }

    public void ToggleLineComment() =>
        ApplyEdit(SqlTextOps.ToggleLineComment(Editor.Document.Text, Editor.SelectionStart, Editor.SelectionLength, Editor.CaretOffset));

    public void ToggleBlockComment() =>
        ApplyEdit(SqlTextOps.ToggleBlockComment(Editor.Document.Text, Editor.SelectionStart, Editor.SelectionLength, Editor.CaretOffset));

    private void ApplyEdit(TextEdit edit)
    {
        var doc = Editor.Document;
        using (doc.RunUpdate()) doc.Replace(edit.Start, edit.Length, edit.Replacement);
        if (edit.SelectionLength > 0) Editor.Select(edit.SelectionStart, edit.SelectionLength);
        else
        {
            Editor.Select(edit.SelectionStart, 0);
            Editor.CaretOffset = edit.SelectionStart;
        }
    }

    /// <summary>Ctrl+= / Ctrl+- / Ctrl+wheel: changes the font size now and persists editor.fontSize shortly after.</summary>
    public void ZoomFont(int delta)
    {
        var size = Math.Clamp(Math.Round(Editor.FontSize) + delta, 9, 100);
        if (size.Equals(Editor.FontSize)) return;
        Editor.FontSize = size;
        _zoomPersistTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) =>
        {
            _zoomPersistTimer!.Stop();
            PersistZoom();
        }, Dispatcher);
        _zoomPersistTimer.Stop();
        _zoomPersistTimer.Start();
    }

    private void PersistZoom()
    {
        if (!AppServices.IsInitialized || AppServices.Current.Editor.FontSize.Equals(Editor.FontSize)) return;
        try
        {
            AppServices.Settings.SetValue("editor.fontSize", JsonValue.Create(Editor.FontSize));
        }
        catch (Exception ex)
        {
            _vm?.Workspace.ConsoleLog.Add("Could not save editor.fontSize: " + ex.Message, ConsoleEntryKind.Error);
        }
    }

    private void OnEditorMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        ZoomFont(e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }

    // ---- results pane -------------------------------------------------------------------------------------------

    private void ApplyResultsVisibility()
    {
        var visible = _vm?.IsResultsVisible ?? true;
        if (!visible && ResultsRow.Height.Value > 0) _resultsHeight = ResultsRow.Height;
        ResultsRow.Height = visible ? _resultsHeight : new GridLength(0);
        SplitterRow.Height = new GridLength(visible ? 4 : 0);
        ResultsPanel.Visibility = ResultsSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible && ResultsPanel.IsKeyboardFocusWithin) FocusEditor();
    }

    private void OnResultsPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None && _vm?.IsRunning == true)
        {
            _vm.Cancel();
            e.Handled = true;
        }
    }

    // ---- run bar ------------------------------------------------------------------------------------------------

    private void OnRunClick(object sender, RoutedEventArgs e)
    {
        _vm?.RunOrCancelAsync().Forget();
        if (_vm?.IsRunning != true) FocusEditor();
    }

    private void OnRunMenuClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var menu = new ContextMenu { PlacementTarget = RunMenuButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(Item("Run Current", CommandIds.QueryRunCurrent, () => _vm.RunCurrentAsync().Forget()));
        menu.Items.Add(Item("Run All", CommandIds.QueryRunAll, () => _vm.RunAllAsync().Forget()));
        var def = new MenuItem { Header = "Default" };
        def.Items.Add(new MenuItem { Header = "Run Current", IsCheckable = true, IsChecked = !_vm.RunButtonRunsAll, Command = new Relay(() => _vm.RunButtonRunsAll = false) });
        def.Items.Add(new MenuItem { Header = "Run All", IsCheckable = true, IsChecked = _vm.RunButtonRunsAll, Command = new Relay(() => _vm.RunButtonRunsAll = true) });
        menu.Items.Add(def);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Save SQL As…", CommandIds.FileSaveAs, () => SqlEditorFeature.SaveAs(_vm)));
        menu.Items.Add(Item("Change Shortcuts…", CommandIds.KeymapOpen, () =>
        {
            if (AppServices.IsInitialized) AppServices.Commands.TryExecute(CommandIds.KeymapOpen, CommandContext.For(CommandIds.KeymapOpen, this));
        }));
        menu.IsOpen = true;
    }

    private static MenuItem Item(string header, string commandId, Action action) => new()
    {
        Header = header,
        InputGestureText = AppServices.IsInitialized ? AppServices.Gestures[commandId] : null,
        Command = new Relay(action),
    };

    private sealed class Relay(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }

    // ---- replace bar --------------------------------------------------------------------------------------------

    private Regex? ReplaceRegex() =>
        SqlTextOps.BuildSearchRegex(FindBox.Text, MatchCaseBox.IsChecked == true, WholeWordBox.IsChecked == true, RegexBox.IsChecked == true);

    private void FindInReplaceBar(bool backwards = false)
    {
        var regex = ReplaceRegex();
        if (regex is null)
        {
            ReplaceStatus.Text = string.IsNullOrEmpty(FindBox.Text) ? "" : "Invalid pattern";
            return;
        }
        var text = Editor.Document.Text;
        Match? match;
        if (!backwards)
        {
            var from = Math.Min(text.Length, Editor.SelectionStart + Editor.SelectionLength);
            match = regex.Match(text, from);
            if (!match.Success) match = regex.Match(text, 0);
        }
        else
        {
            var before = Editor.SelectionStart;
            match = regex.Matches(text).Cast<Match>().LastOrDefault(m => m.Index < before) ?? regex.Matches(text).Cast<Match>().LastOrDefault();
        }
        if (match is not { Success: true })
        {
            ReplaceStatus.Text = "No matches";
            return;
        }
        ReplaceStatus.Text = "";
        Editor.Select(match.Index, match.Length);
        var loc = Editor.Document.GetLocation(match.Index);
        Editor.ScrollTo(loc.Line, loc.Column);
    }

    private void OnFindBoxChanged(object sender, TextChangedEventArgs e) => ReplaceStatus.Text = "";

    private void OnFindNextClick(object sender, RoutedEventArgs e) => FindInReplaceBar();

    private void OnReplaceClick(object sender, RoutedEventArgs e) => ReplaceOne();

    private void ReplaceOne()
    {
        var regex = ReplaceRegex();
        if (regex is null) return;
        var selected = Editor.SelectedText;
        var m = selected.Length > 0 ? regex.Match(selected) : null;
        if (m is { Success: true } && m.Index == 0 && m.Length == selected.Length)
        {
            var replacement = RegexBox.IsChecked == true ? m.Result(ReplaceBox.Text) : ReplaceBox.Text;
            var start = Editor.SelectionStart;
            Editor.Document.Replace(start, Editor.SelectionLength, replacement);
            Editor.Select(start + replacement.Length, 0);
        }
        FindInReplaceBar();
    }

    private void OnReplaceAllClick(object sender, RoutedEventArgs e)
    {
        var regex = ReplaceRegex();
        if (regex is null) return;
        var doc = Editor.Document;
        var (text, count) = SqlTextOps.ReplaceAll(doc.Text, regex, ReplaceBox.Text, RegexBox.IsChecked == true);
        if (count > 0)
        {
            var caret = Editor.CaretOffset;
            doc.Replace(0, doc.TextLength, text);
            Editor.CaretOffset = Math.Min(caret, doc.TextLength);
        }
        ReplaceStatus.Text = $"{count} replaced";
    }

    private void OnReplaceBarKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                CloseReplaceBar();
                e.Handled = true;
                break;
            case Key.Enter when sender == ReplaceBox:
                ReplaceOne();
                e.Handled = true;
                break;
            case Key.Enter:
                FindInReplaceBar(Keyboard.Modifiers == ModifierKeys.Shift);
                e.Handled = true;
                break;
        }
    }

    private void OnCloseReplaceClick(object sender, RoutedEventArgs e) => CloseReplaceBar();

    private void CloseReplaceBar()
    {
        ReplaceBar.Visibility = Visibility.Collapsed;
        FocusEditor();
    }
}
