using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICSharpCode.AvalonEdit.Document;
using TablePlsu.App.Features.Workspace;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.IO;
using TablePlsu.Core.Settings;

namespace TablePlsu.App.Features.Settings;

/// <summary>Which JSON document a settings editor shows.</summary>
public enum SettingsDocumentKind
{
    /// <summary>The user's settings.json (editable, validated, saved atomically and hot-reloaded).</summary>
    User,
    /// <summary>The embedded defaults (read-only).</summary>
    Defaults,
}

/// <summary>
/// The settings.json editor (spec §13): an AvalonEdit document with live schema validation (diagnostics panel with
/// jump-to-line), Save = atomic write + hot reload, an error banner when the file cannot be applied (the last good
/// configuration stays active), "Open defaults", "Open Keymap" and "Open Themes Folder". The same view-model backs a
/// workspace tab and the stand-alone settings window used from the welcome screen.
/// </summary>
public sealed partial class SettingsEditorViewModel : TabViewModelBase
{
    public const string DefaultsKind = "settingsDefaults";

    private readonly DispatcherTimer? _validateTimer;
    private string _savedText = "";
    private bool _suppressDirty;
    private readonly Thread _owner = Thread.CurrentThread; // TextDocument is thread-affine

    public SettingsEditorViewModel(SettingsDocumentKind documentKind = SettingsDocumentKind.User)
        : base(documentKind == SettingsDocumentKind.User ? TabKinds.Settings : DefaultsKind)
    {
        DocumentKind = documentKind;
        IsReadOnly = documentKind == SettingsDocumentKind.Defaults;
        Title = IsReadOnly ? "defaults.json" : "settings.json";
        ToolTip = IsReadOnly ? "Built-in defaults (read-only). Copy keys into settings.json to override them." : SettingsFile;
        Document = new TextDocument();
        Document.UndoStack.SizeLimit = 500;

        LoadText(IsReadOnly ? SettingsService.DefaultsJson : ReadOrCreateUserFile(), resetUndo: true);

        if (!IsReadOnly)
        {
            if (Application.Current is not null)
            {
                _validateTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
                _validateTimer.Tick += (_, _) =>
                {
                    _validateTimer.Stop();
                    Validate();
                };
            }
            Document.TextChanged += OnTextChanged;
            if (AppServices.IsInitialized) AppServices.SettingsChanged += OnSettingsChanged;
            UpdateBanner(AppServices.IsInitialized ? AppServices.Settings.LastError : null);
            Validate();
        }
    }

    public SettingsDocumentKind DocumentKind { get; }

    public bool IsReadOnly { get; }

    public TextDocument Document { get; }

    public string SettingsFile => AppServices.IsInitialized ? AppServices.Settings.SettingsFile : "settings.json";

    public ObservableCollection<SettingsDiagnostic> Diagnostics { get; } = [];

    /// <summary>Error banner text: the file could not be applied, so the last good settings stay active.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBanner))]
    public partial string? Banner { get; set; }

    public bool HasBanner => !string.IsNullOrEmpty(Banner);

    /// <summary>"Saved and applied 14:02:11", "3 problems", …</summary>
    [ObservableProperty]
    public partial string? StatusText { get; set; }

    public bool HasDiagnostics => Diagnostics.Count > 0;

    public int ErrorCount => Diagnostics.Count(d => d.IsError);

    public int WarningCount => Diagnostics.Count - ErrorCount;

    public string DiagnosticsSummary =>
        ErrorCount == 0 && WarningCount == 0 ? "No problems"
        : $"{ErrorCount} error{(ErrorCount == 1 ? "" : "s")}, {WarningCount} warning{(WarningCount == 1 ? "" : "s")}";

    /// <summary>Raised to move the caret: (line, column), 1-based. The view focuses the editor.</summary>
    public event EventHandler<(int Line, int Column)>? JumpRequested;

    /// <summary>Raised to select a range (offset, length). The view focuses the editor.</summary>
    public event EventHandler<(int Offset, int Length)>? SelectRequested;

    /// <summary>Raised after <see cref="Diagnostics"/> changed (the view refreshes its underlines).</summary>
    public event EventHandler? DiagnosticsChanged;

    /// <summary>Opens the defaults/keymap reference next to this editor (a tab in a workspace, else a window).</summary>
    internal Action<string>? OpenRelated { get; set; }

    public override FrameworkElement CreateView() => new SettingsEditorView { DataContext = this };

    public override bool Matches(TabRequest request) => request.Kind == Kind;

    // ---- loading / saving ----

    private static string ReadOrCreateUserFile()
    {
        if (!AppServices.IsInitialized) return SettingsJsonEdits.StarterText;
        var file = AppServices.Settings.SettingsFile;
        try
        {
            if (!File.Exists(file))
            {
                AtomicFile.WriteAllText(file, SettingsJsonEdits.StarterText);
                return SettingsJsonEdits.StarterText;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SettingsJsonEdits.StarterText;
        }
        return AppServices.Settings.ReadUserJson();
    }

    private void LoadText(string text, bool resetUndo)
    {
        _suppressDirty = true;
        try
        {
            if (resetUndo)
            {
                Document.Text = text;
                Document.UndoStack.ClearAll();
            }
            else
            {
                Document.Replace(0, Document.TextLength, text);
            }
            _savedText = text;
            Document.UndoStack.MarkAsOriginalFile();
            IsDirty = false;
        }
        finally
        {
            _suppressDirty = false;
        }
        UpdateTitle();
    }

    private void OnTextChanged(object? sender, EventArgs e)
    {
        if (!_suppressDirty) IsDirty = !string.Equals(Document.Text, _savedText, StringComparison.Ordinal);
        UpdateTitle();
        if (_validateTimer is not null)
        {
            _validateTimer.Stop();
            _validateTimer.Start();
        }
        else
        {
            Validate();
        }
    }

    private void UpdateTitle()
    {
        if (IsReadOnly) return;
        Title = IsDirty ? "settings.json •" : "settings.json";
    }

    /// <summary>Validates the current text against the schema and refreshes <see cref="Diagnostics"/>.</summary>
    public IReadOnlyList<SettingsDiagnostic> Validate()
    {
        _validateTimer?.Stop();
        var list = SettingsValidator.ValidateText(Document.Text);
        Diagnostics.Clear();
        foreach (var d in list) Diagnostics.Add(d);
        OnPropertyChanged(nameof(HasDiagnostics));
        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(DiagnosticsSummary));
        DiagnosticsChanged?.Invoke(this, EventArgs.Empty);
        return list;
    }

    /// <summary>Ctrl+S: validate, write atomically and hot-reload. Invalid JSON is still saved (it is the user's file); the
    /// last good settings stay active and the banner explains why.</summary>
    public override void Save()
    {
        if (IsReadOnly || !AppServices.IsInitialized) return;
        var text = Document.Text;
        var diagnostics = Validate();
        try
        {
            AppServices.Settings.SaveUserJson(text); // atomic write + reload; Changed → AppServices.ApplySettings (UI thread)
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Banner = "Could not save settings.json: " + ex.Message;
            return;
        }
        _savedText = text;
        Document.UndoStack.MarkAsOriginalFile();
        IsDirty = false;
        UpdateTitle();

        var error = AppServices.Settings.LastError;
        UpdateBanner(error);
        var errors = diagnostics.Count(d => d.IsError);
        StatusText = error is not null
            ? $"Saved {DateTime.Now:HH:mm:ss} — not applied (last good settings kept)"
            : errors > 0
                ? $"Saved and applied {DateTime.Now:HH:mm:ss} — {errors} value{(errors == 1 ? "" : "s")} clamped or ignored"
                : $"Saved and applied {DateTime.Now:HH:mm:ss}";
        AddThemeWarnings();
    }

    /// <summary>Theme problems only show up after resolving (unknown theme name, bad override): list them as warnings.</summary>
    private void AddThemeWarnings()
    {
        if (!AppServices.IsInitialized) return;
        var added = false;
        // Resolve is cheap and side-effect free apart from refreshing ThemeService.Warnings.
        AppServices.Themes.Resolve(AppServices.Current.Theme, ThemeApplier.IsSystemDark());
        foreach (var w in AppServices.Themes.Warnings.ToList())
        {
            if (Diagnostics.Any(d => d.Message == w)) continue;
            var path = w.Split(':')[0];
            var (line, col) = LocateKey(path);
            Diagnostics.Add(new SettingsDiagnostic(DiagnosticSeverity.Warning, path.StartsWith("theme.", StringComparison.Ordinal) ? path : "theme", w, line, col));
            added = true;
        }
        if (!added) return;
        OnPropertyChanged(nameof(HasDiagnostics));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(DiagnosticsSummary));
        DiagnosticsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Line/column of a dotted key ("theme.dark") in the current text, else 1:1.</summary>
    private (int Line, int Column) LocateKey(string dotted)
    {
        try
        {
            var node = JsonTree.Parse(Document.Text);
            JsonTreeProperty? prop = null;
            foreach (var part in dotted.Split('.'))
            {
                prop = node.Find(part);
                if (prop is null) break;
                node = prop.Value;
            }
            if (prop is not null) return JsonTree.LineColumn(Document.Text, prop.NameStart);
        }
        catch (JsonTreeException)
        {
        }
        return (1, 1);
    }

    private void UpdateBanner(string? error) =>
        Banner = error is null ? null : $"{error}\nThe last good settings are still active. Fix the file and save again.";

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (Thread.CurrentThread != _owner) return; // a window on another UI thread (tests); not ours to touch
        UpdateBanner(e.Error);
        if (IsDirty) return;
        // External edit (or another editor window): pick up the new text when we have no local changes.
        var disk = AppServices.Settings.ReadUserJson();
        if (!string.Equals(disk, Document.Text, StringComparison.Ordinal))
        {
            LoadText(disk, resetUndo: false);
            Validate();
        }
    }

    /// <summary>Re-reads settings.json from disk, dropping local edits.</summary>
    [RelayCommand]
    public void Revert()
    {
        if (IsReadOnly || !AppServices.IsInitialized) return;
        LoadText(AppServices.Settings.ReadUserJson(), resetUndo: false);
        Validate();
        StatusText = "Reloaded from disk";
    }

    // ---- navigation ----

    [RelayCommand]
    private void Jump(SettingsDiagnostic? diagnostic)
    {
        if (diagnostic is null) return;
        JumpRequested?.Invoke(this, (diagnostic.Line, diagnostic.Column));
    }

    /// <summary>"Open Keymap": inserts (or finds) the "keymap" section and selects it. False when the JSON is broken.</summary>
    public bool RevealSection(string key)
    {
        if (IsReadOnly) return false;
        var edit = SettingsJsonEdits.EnsureObjectSection(Document.Text, key);
        if (edit is null)
        {
            StatusText = $"Fix the JSON errors first, then use Open Keymap to add the \"{key}\" section.";
            Validate();
            if (Diagnostics.FirstOrDefault() is { } first) JumpRequested?.Invoke(this, (first.Line, first.Column));
            return false;
        }
        if (edit.Changed)
        {
            // One undoable change; the text is not saved until the user saves.
            Document.Replace(0, Document.TextLength, edit.Text);
        }
        SelectRequested?.Invoke(this, (edit.SelectionStart, edit.SelectionLength));
        return true;
    }

    [RelayCommand]
    private void OpenDefaults() => OpenRelated?.Invoke(DefaultsKind);

    [RelayCommand]
    private void OpenKeymapReference() => OpenRelated?.Invoke(KeymapReferenceViewModel.TabKind);

    [RelayCommand]
    private void OpenThemesFolder() => SettingsActions.OpenThemesFolder();

    [RelayCommand]
    private void SaveFile() => Save();

    public override bool CanCloseNow(TabCloseReason reason)
    {
        if (!IsDirty || reason != TabCloseReason.User) return true;
        return Dialogs.Confirm("settings.json has unsaved changes. Close and discard them?", "Close settings.json", "Discard", danger: true);
    }

    public override void OnClosed(TabCloseReason reason)
    {
        _validateTimer?.Stop();
        Document.TextChanged -= OnTextChanged;
        if (AppServices.IsInitialized) AppServices.SettingsChanged -= OnSettingsChanged;
    }
}
