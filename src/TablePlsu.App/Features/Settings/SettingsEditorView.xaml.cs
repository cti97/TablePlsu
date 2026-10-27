using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Search;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Commands;
using TablePlsu.Core.Settings;
using TablePlsu.Core.Themes;

namespace TablePlsu.App.Features.Settings;

/// <summary>View for <see cref="SettingsEditorViewModel"/>: AvalonEdit + problems panel + banner.</summary>
public partial class SettingsEditorView : UserControl
{
    private readonly DiagnosticUnderlineRenderer _underlines = new();
    private SettingsEditorViewModel? _vm;

    public SettingsEditorView()
    {
        SettingsResources.EnsureInitialized();
        InitializeComponent();
        SearchPanel.Install(Editor.TextArea);
        Editor.TextArea.TextView.BackgroundRenderers.Add(_underlines);
        Editor.TextArea.SetResourceReference(ICSharpCode.AvalonEdit.Editing.TextArea.SelectionBrushProperty, "c.editor.selection");
        Editor.TextArea.SelectionForeground = null;
        Editor.TextArea.SelectionBorder = null;
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        Editor.Options.ConvertTabsToSpaces = true;
        Editor.Options.IndentationSize = 2;
        Editor.Options.HighlightCurrentLine = true;
        ApplyTheme();

        DataContextChanged += (_, e) => Bind(e.NewValue as SettingsEditorViewModel);
        Loaded += (_, _) =>
        {
            if (AppServices.IsInitialized) AppServices.Theme.ThemeChanged += OnThemeChanged;
            Dispatcher.BeginInvoke(() => Editor.TextArea.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        };
        Unloaded += (_, _) =>
        {
            if (AppServices.IsInitialized) AppServices.Theme.ThemeChanged -= OnThemeChanged;
        };
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void Bind(SettingsEditorViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.JumpRequested -= OnJump;
            _vm.SelectRequested -= OnSelect;
            _vm.DiagnosticsChanged -= OnDiagnosticsChanged;
        }
        _vm = vm;
        if (vm is null) return;
        Editor.Document = vm.Document;
        Editor.IsReadOnly = vm.IsReadOnly;
        vm.JumpRequested += OnJump;
        vm.SelectRequested += OnSelect;
        vm.DiagnosticsChanged += OnDiagnosticsChanged;
        if (vm.IsReadOnly)
        {
            SaveButton.Visibility = Visibility.Collapsed;
            ((FrameworkElement)SaveButton.Parent).Visibility = Visibility.Collapsed;
            ProblemsPanel.Visibility = Visibility.Collapsed;
            ProblemsSplitter.Visibility = Visibility.Collapsed;
            ProblemsRow.Height = new GridLength(0);
            ProblemsRow.MinHeight = 0;
        }
        OnDiagnosticsChanged(null, EventArgs.Empty);
    }

    private void OnThemeChanged(object? sender, ThemeDefinition theme) => ApplyTheme();

    private void ApplyTheme()
    {
        var theme = AppServices.IsInitialized ? AppServices.Theme.Current : ThemeService.BuiltInLight;
        Editor.SyntaxHighlighting = JsonHighlighting.For(theme);
        Editor.TextArea.Caret.CaretBrush = Brush(theme, "editor.caret") ?? Brushes.Black;
        Editor.TextArea.TextView.CurrentLineBackground = Brush(theme, "editor.currentLine");
        Editor.TextArea.TextView.CurrentLineBorder = null;
        _underlines.ErrorBrush = Brush(theme, "editor.errorUnderline") ?? Brushes.Red;
        _underlines.WarningBrush = Brush(theme, "app.warning") ?? Brushes.Orange;
        Editor.TextArea.TextView.InvalidateLayer(_underlines.Layer);
    }

    private static SolidColorBrush? Brush(ThemeDefinition theme, string key) =>
        theme.Colors.TryGetValue(key, out var v) ? ThemeApplier.ParseBrush(v) : null;

    private void OnDiagnosticsChanged(object? sender, EventArgs e)
    {
        _underlines.SetDiagnostics(_vm is null ? [] : [.. _vm.Diagnostics]);
        Editor.TextArea.TextView.InvalidateLayer(_underlines.Layer);
    }

    private void OnJump(object? sender, (int Line, int Column) at)
    {
        var doc = Editor.Document;
        if (doc is null || doc.LineCount == 0) return;
        var line = doc.GetLineByNumber(Math.Clamp(at.Line, 1, doc.LineCount));
        var offset = line.Offset + Math.Clamp(at.Column - 1, 0, line.Length);
        Editor.TextArea.Caret.Offset = offset;
        Editor.Select(offset, 0);
        Editor.ScrollTo(line.LineNumber, at.Column);
        Editor.TextArea.Focus();
    }

    private void OnSelect(object? sender, (int Offset, int Length) range)
    {
        var doc = Editor.Document;
        if (doc is null) return;
        var start = Math.Clamp(range.Offset, 0, doc.TextLength);
        var length = Math.Clamp(range.Length, 0, doc.TextLength - start);
        Editor.Select(start, length);
        Editor.TextArea.Caret.Offset = start + length;
        var loc = doc.GetLocation(start);
        Editor.ScrollTo(loc.Line, loc.Column);
        Editor.TextArea.Focus();
    }

    private void OnProblemClicked(object sender, MouseButtonEventArgs e)
    {
        if (ProblemList.SelectedItem is SettingsDiagnostic d) _vm?.JumpCommand.Execute(d);
    }

    private void OnProblemKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || ProblemList.SelectedItem is not SettingsDiagnostic d) return;
        _vm?.JumpCommand.Execute(d);
        e.Handled = true;
    }

    /// <summary>
    /// Save outside a workspace (the stand-alone settings window has no Workspace scope): whatever chord is bound to
    /// changes.commit (Ctrl+S) saves. Inside a workspace the key router handles it first and calls the tab's Save().
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || _vm is null || _vm.IsReadOnly || !AppServices.IsInitialized) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!KeyBindingRouter.TryGetChord(key, Keyboard.Modifiers, out var chord)) return;
        if (AppServices.Keymap.Resolve(chord, CommandScope.Workspace) != CommandIds.ChangesCommit) return;
        _vm.Save();
        e.Handled = true;
    }
}
