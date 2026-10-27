using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using TablePlsu.App.Features.Workspace;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Themes;

namespace TablePlsu.App.Features.Settings;

/// <summary>
/// Opens the settings documents. Inside a workspace they are tabs (TabKinds.Settings, "settingsDefaults",
/// "keymapReference"); from the welcome screen (no workspace) they open in stand-alone themed windows, one per kind.
/// </summary>
public static class SettingsActions
{
    private static readonly Dictionary<string, (Window Window, TabViewModelBase Vm)> Windows = new(StringComparer.Ordinal);

    public static TabViewModelBase? Open(string kind, IWorkspace? workspace, Window? owner = null)
    {
        if (workspace is not null)
        {
            var tab = workspace.OpenTab(new TabRequest { Kind = kind, Pinned = true });
            if (tab is not null) Wire(tab, workspace, owner);
            return tab;
        }
        return OpenWindow(kind, owner);
    }

    /// <summary>settings.json with the "keymap" section revealed, plus the keymap reference.</summary>
    public static void OpenKeymap(IWorkspace? workspace, Window? owner = null)
    {
        Open(KeymapReferenceViewModel.TabKind, workspace, owner);
        if (Open(Infrastructure.TabKinds.Settings, workspace, owner) is SettingsEditorViewModel editor)
        {
            // Let the view attach (it handles SelectRequested) before revealing.
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) editor.RevealSection("keymap");
            else dispatcher.BeginInvoke(() => editor.RevealSection("keymap"), DispatcherPriority.Loaded);
        }
    }

    /// <summary>Opens the themes folder in Explorer (creating it, with a short README, when needed).</summary>
    public static void OpenThemesFolder()
    {
        if (!AppServices.IsInitialized) return;
        var dir = AppServices.Paths.ThemesDir;
        try
        {
            Directory.CreateDirectory(dir);
            var readme = Path.Combine(dir, "README.txt");
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) File.WriteAllText(readme, ThemesReadme);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Dialogs.Error(ex.Message, "Could not open the themes folder");
        }
    }

    internal static TabViewModelBase? Create(string kind) => kind switch
    {
        Infrastructure.TabKinds.Settings => new SettingsEditorViewModel(SettingsDocumentKind.User),
        SettingsEditorViewModel.DefaultsKind => new SettingsEditorViewModel(SettingsDocumentKind.Defaults),
        KeymapReferenceViewModel.TabKind => new KeymapReferenceViewModel(),
        _ => null,
    };

    internal static void Wire(TabViewModelBase tab, IWorkspace? workspace, Window? owner)
    {
        switch (tab)
        {
            case SettingsEditorViewModel editor:
                editor.OpenRelated = kind => Open(kind, workspace, owner);
                break;
            case KeymapReferenceViewModel keymap:
                keymap.OpenKeymapSection = () =>
                {
                    if (Open(Infrastructure.TabKinds.Settings, workspace, owner) is SettingsEditorViewModel e) e.RevealSection("keymap");
                };
                break;
        }
    }

    private static TabViewModelBase? OpenWindow(string kind, Window? owner)
    {
        if (Windows.TryGetValue(kind, out var existing))
        {
            if (existing.Window.WindowState == WindowState.Minimized) existing.Window.WindowState = WindowState.Normal;
            existing.Window.Activate();
            return existing.Vm;
        }
        if (Create(kind) is not { } vm) return null;
        Wire(vm, null, owner);
        var view = vm.CreateView();
        var window = Dialogs.CreateHost(view, vm.Title + " — TablePlsu", 920, 720, owner);
        window.ShowInTaskbar = true;
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.Owner = null; // an independent window, so it can stay open while the main window is used
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TabViewModelBase.Title)) window.Title = vm.Title + " — TablePlsu";
        };
        window.Closing += (_, e) =>
        {
            if (!vm.CanCloseNow(TabCloseReason.User)) e.Cancel = true;
        };
        window.Closed += (_, _) =>
        {
            Windows.Remove(kind);
            vm.OnClosed(TabCloseReason.User);
        };
        window.SourceInitialized += (_, _) => AppServices.Theme.ApplyTitleBar(window);
        Windows[kind] = (window, vm);
        window.Show();
        return vm;
    }

    private const string ThemesReadme =
        """
        TablePlsu themes
        ================
        Drop a *.json theme file here; it becomes available by its "name" (re-read when settings.json changes).
        Select it with settings.json → "theme": { "light": "<name>" } or "dark": "<name>".

        {
          "name": "My Theme",
          "base": "dark",                 // missing colours come from the built-in Light/Dark theme of this base
          "colors": {
            "app":    { "accent": "#FF8800" },
            "editor": { "keyword": "#C678DD" },
            "grid":   { "nullForeground": "#777777" }
          }
        }

        Built-in themes: Light, Dark, Nord, Solarized Light.
        For single colours on top of any theme use settings.json → "theme": { "overrides": { "editor.keyword": "#FF0000" } }.
        """;

    /// <summary>All theme names (built-in and user), for the reference UI.</summary>
    public static IReadOnlyList<string> ThemeNames => AppServices.IsInitialized ? AppServices.Themes.AvailableThemes : ThemeService.BuiltInNames;
}
