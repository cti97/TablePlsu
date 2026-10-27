using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TablePlsu.App.Features.Workspace;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Commands;
using TablePlsu.Core.Keymap;

namespace TablePlsu.App.Features.Settings;

/// <summary>One row of the keymap reference.</summary>
public sealed record KeymapRow(string Id, string Title, string Scope, string Bindings, string Defaults, bool IsCustomized, bool IsUnbound)
{
    public string Status => IsUnbound ? "unbound" : IsCustomized ? "custom" : "";
}

/// <summary>
/// Read-only reference of every command id with its title, scope, current bindings (after settings.json "keymap")
/// and defaults. Filterable; refreshes live when the keymap changes. Also lists keymap warnings (conflicts, bad chords).
/// </summary>
public sealed partial class KeymapReferenceViewModel : TabViewModelBase
{
    public const string TabKind = "keymapReference";

    private List<KeymapRow> _all = [];

    public KeymapReferenceViewModel() : base(TabKind)
    {
        Title = "Keymap";
        ToolTip = "All command ids with their scopes and current bindings. Rebind them in settings.json → \"keymap\".";
        Refresh();
        if (AppServices.IsInitialized) AppServices.KeymapChanged += OnKeymapChanged;
    }

    public ObservableCollection<KeymapRow> Rows { get; } = [];

    public ObservableCollection<string> Warnings { get; } = [];

    public bool HasWarnings => Warnings.Count > 0;

    [ObservableProperty]
    public partial string FilterText { get; set; } = "";

    /// <summary>Opens settings.json at the "keymap" section (wired by the feature).</summary>
    internal Action? OpenKeymapSection { get; set; }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    public override FrameworkElement CreateView() => new KeymapReferenceView { DataContext = this };

    public override bool Matches(TabRequest request) => request.Kind == Kind;

    public override void Reload() => Refresh();

    private readonly Thread _owner = Thread.CurrentThread;

    private void OnKeymapChanged(object? sender, EventArgs e)
    {
        if (Thread.CurrentThread == _owner) Refresh();
    }

    public void Refresh()
    {
        var keymap = AppServices.KeymapOrNull ?? new KeymapService();
        var overrides = AppServices.IsInitialized ? AppServices.Current.Keymap : new Dictionary<string, string[]?>();
        _all = [.. CommandIds.All.Select(def =>
        {
            var current = keymap.GetBindings(def.Id).Select(c => c.ToString()).ToList();
            var customized = overrides.ContainsKey(def.Id);
            return new KeymapRow(
                def.Id,
                def.Title,
                def.Scope.ToString(),
                current.Count == 0 ? "—" : string.Join("   ", current),
                def.DefaultBindings.Count == 0 ? "—" : string.Join("   ", def.DefaultBindings),
                customized,
                current.Count == 0);
        })];
        Warnings.Clear();
        foreach (var w in keymap.Warnings) Warnings.Add(w);
        OnPropertyChanged(nameof(HasWarnings));
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        Rows.Clear();
        var text = FilterText.Trim();
        IEnumerable<KeymapRow> rows = text.Length == 0
            ? _all
            : _all.Where(r => r.Id.Contains(text, StringComparison.OrdinalIgnoreCase)
                              || r.Title.Contains(text, StringComparison.OrdinalIgnoreCase)
                              || r.Bindings.Contains(text, StringComparison.OrdinalIgnoreCase)
                              || r.Scope.Contains(text, StringComparison.OrdinalIgnoreCase));
        foreach (var r in rows) Rows.Add(r);
    }

    [RelayCommand]
    private void EditKeymap() => OpenKeymapSection?.Invoke();

    /// <summary>Copies <c>"id": "binding"</c> for pasting into the keymap section.</summary>
    [RelayCommand]
    private void CopyEntry(KeymapRow? row)
    {
        if (row is null) return;
        var binding = row.IsUnbound ? "null" : "\"" + row.Bindings.Split("   ")[0] + "\"";
        try { Clipboard.SetText($"\"{row.Id}\": {binding},"); }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    public override void OnClosed(TabCloseReason reason)
    {
        if (AppServices.IsInitialized) AppServices.KeymapChanged -= OnKeymapChanged;
    }
}
