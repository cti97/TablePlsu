using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TablePlsu.App.Infrastructure;
using TablePlsu.App.Shell;
using TablePlsu.Core.Connections;
using TablePlsu.Core.Search;

namespace TablePlsu.App.Features.Welcome;

/// <summary>A row in the welcome connection list.</summary>
public sealed class ConnectionItemViewModel
{
    public ConnectionItemViewModel(ConnectionProfile profile)
    {
        Profile = profile;
        ColorBrush = ThemeApplier.ParseBrush(profile.Color);
        TagBrush = ResolveTagBrush(profile.Tag);
    }

    public ConnectionProfile Profile { get; }

    public string Name => Profile.DisplayName;

    public string Detail =>
        $"{Profile.User}@{Profile.Host}:{Profile.Port}/{Profile.Database}{(Profile.SshEnabled ? $"  ·  SSH {Profile.SshHost}" : "")}";

    public Brush? ColorBrush { get; }

    public string? Tag => string.IsNullOrWhiteSpace(Profile.Tag) ? null : Profile.Tag;

    public Brush? TagBrush { get; }

    private static Brush? ResolveTagBrush(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || !AppServices.IsInitialized) return null;
        return AppServices.Current.Tags.TryGetValue(tag, out var hex) ? ThemeApplier.ParseBrush(hex) : Brushes.Gray;
    }
}

/// <summary>
/// The welcome screen (spec §1): saved connections with search, Create Connection, Import from URL, and the connection
/// form shown in place of the list while creating or editing.
/// </summary>
public sealed partial class WelcomeViewModel : ObservableObject
{
    private readonly MainViewModel? _shell;
    private List<ConnectionItemViewModel> _all = [];

    public WelcomeViewModel(MainViewModel? shell)
    {
        _shell = shell;
        Reload();
    }

    public ObservableCollection<ConnectionItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial ConnectionItemViewModel? SelectedItem { get; set; }

    /// <summary>The connection form when creating/editing; null shows the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFormOpen))]
    public partial ConnectionFormViewModel? Form { get; private set; }

    public bool IsFormOpen => Form is not null;

    public bool HasConnections => _all.Count > 0;

    /// <summary>Raised for Ctrl+F (welcome.search); the view focuses its search box.</summary>
    public event EventHandler? FocusSearchRequested;

    public void RequestSearchFocus() => FocusSearchRequested?.Invoke(this, EventArgs.Empty);

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    public void Reload()
    {
        var selectedId = SelectedItem?.Profile.Id;
        _all = AppServices.IsInitialized ? [.. AppServices.Connections.Profiles.Select(p => new ConnectionItemViewModel(p))] : [];
        ApplyFilter();
        SelectedItem = Items.FirstOrDefault(i => i.Profile.Id == selectedId) ?? Items.FirstOrDefault();
        OnPropertyChanged(nameof(HasConnections));
    }

    private void ApplyFilter()
    {
        Items.Clear();
        IEnumerable<ConnectionItemViewModel> list = string.IsNullOrWhiteSpace(SearchText)
            ? _all
            : FuzzyMatcher.Filter(_all, SearchText.Trim(), i => i.Name).Select(r => r.Item);
        foreach (var item in list) Items.Add(item);
        if (SelectedItem is null || !Items.Contains(SelectedItem)) SelectedItem = Items.FirstOrDefault();
    }

    [RelayCommand]
    private Task ConnectAsync(ConnectionItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is null || _shell is null) return Task.CompletedTask;
        return _shell.ConnectAsync(item.Profile);
    }

    [RelayCommand]
    private void NewConnection() => OpenForm(null);

    [RelayCommand]
    private void Edit(ConnectionItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is not null) OpenForm(item.Profile);
    }

    [RelayCommand]
    private void Duplicate(ConnectionItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is null) return;
        var copy = AppServices.Connections.Duplicate(item.Profile.Id);
        Reload();
        SelectedItem = Items.FirstOrDefault(i => i.Profile.Id == copy.Id);
    }

    [RelayCommand]
    private void Delete(ConnectionItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is null) return;
        if (!Dialogs.Confirm($"Delete the connection \"{item.Name}\"? Its saved passwords are removed too.", "Delete Connection", "Delete", danger: true))
            return;
        AppServices.Connections.Delete(item.Profile.Id);
        Reload();
    }

    [RelayCommand]
    private void ImportFromUrl()
    {
        var url = Dialogs.PromptText("Import Connection from URL",
            "Paste a postgresql://user:pass@host:port/db?sslmode=… URL (postgres:// and postgresql+ssh:// work too):");
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!ConnectionUrl.TryParse(url.Trim(), out var parsed, out var error))
        {
            Dialogs.Error(error, "Invalid URL");
            return;
        }
        OpenForm(null);
        Form!.ApplyParsedUrl(parsed);
    }

    private void OpenForm(ConnectionProfile? profile)
    {
        var form = new ConnectionFormViewModel(profile, allowConnect: true);
        form.Saved += p =>
        {
            CloseForm();
            SelectedItem = Items.FirstOrDefault(i => i.Profile.Id == p.Id);
        };
        form.ConnectRequested += (p, secrets) =>
        {
            CloseForm();
            _shell?.OpenWorkspace(p, secrets, null, newWindow: false);
        };
        form.Cancelled += CloseForm;
        Form = form;
    }

    private void CloseForm()
    {
        Form = null;
        Reload();
    }
}
