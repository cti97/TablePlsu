using System.IO;
using System.Windows;
using System.Windows.Threading;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Commands;

namespace TablePlsu.App.Features.Settings;

/// <summary>
/// Settings &amp; customisation (spec §13):
/// <list type="bullet">
/// <item><c>settings.open</c> (Ctrl+,): settings.json in an AvalonEdit tab (a window on the welcome screen).</item>
/// <item><c>keymap.open</c>: settings.json with the "keymap" section revealed + the keymap reference tab.</item>
/// <item><c>settings.openDefaults</c>, <c>keymap.reference</c>, <c>themes.openFolder</c>: extra (unbindable) commands,
/// also offered in the palette.</item>
/// <item>Editor/sidebar font resources (<see cref="SettingsResources"/>) and a themes-folder watcher, so fonts, tag
/// colours and dropped-in theme files apply live.</item>
/// </list>
/// </summary>
public sealed class SettingsFeature : IFeature
{
    public const string OpenDefaultsCommand = "settings.openDefaults";
    public const string KeymapReferenceCommand = "keymap.reference";
    public const string OpenThemesFolderCommand = "themes.openFolder";

    private static FileSystemWatcher? _themesWatcher;
    private static DispatcherTimer? _themesDebounce;

    public void Register(FeatureContext ctx)
    {
        var c = ctx.Commands;
        c.Register(CommandIds.SettingsOpen, x => SettingsActions.Open(TabKinds.Settings, x.Workspace, x.Window));
        c.Register(CommandIds.KeymapOpen, x => SettingsActions.OpenKeymap(x.Workspace, x.Window));
        c.Register(OpenDefaultsCommand, x => SettingsActions.Open(SettingsEditorViewModel.DefaultsKind, x.Workspace, x.Window), scope: CommandScope.Global);
        c.Register(KeymapReferenceCommand, x => SettingsActions.Open(KeymapReferenceViewModel.TabKind, x.Workspace, x.Window), scope: CommandScope.Global);
        c.Register(OpenThemesFolderCommand, _ => SettingsActions.OpenThemesFolder(), scope: CommandScope.Global);

        foreach (var kind in new[] { TabKinds.Settings, SettingsEditorViewModel.DefaultsKind, KeymapReferenceViewModel.TabKind })
        {
            var k = kind;
            ctx.Tabs.Register(k, (_, ws) =>
            {
                var vm = SettingsActions.Create(k)!;
                SettingsActions.Wire(vm, ws, null);
                return vm;
            });
        }

        ctx.Palette.Register(new SettingsPaletteItems());

        // After first render (font resolution and the folder watcher are not needed for the welcome screen). Queued before
        // the shell's own idle work (workspace restore), so the resources exist before any workspace view loads.
        ctx.OnWindowCreated(w => w.Dispatcher.BeginInvoke(() =>
        {
            SettingsResources.EnsureInitialized();
            StartThemesWatcherWhenIdle();
        }, DispatcherPriority.ApplicationIdle));
    }

    /// <summary>Re-reads the themes folder (debounced) when a theme file is added, changed or removed.</summary>
    private static void StartThemesWatcherWhenIdle()
    {
        if (_themesWatcher is not null || !AppServices.IsInitialized || Application.Current is null) return;
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_themesWatcher is not null) return;
            try
            {
                Directory.CreateDirectory(AppServices.Paths.ThemesDir);
                _themesDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _themesDebounce.Tick += (_, _) =>
                {
                    _themesDebounce.Stop();
                    AppServices.Themes.Reload();
                    AppServices.Theme.Apply();
                };
                _themesWatcher = new FileSystemWatcher(AppServices.Paths.ThemesDir, "*.json")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                FileSystemEventHandler onChange = (_, _) => UiDispatch.Run(() =>
                {
                    _themesDebounce.Stop();
                    _themesDebounce.Start();
                });
                _themesWatcher.Changed += onChange;
                _themesWatcher.Created += onChange;
                _themesWatcher.Deleted += onChange;
                _themesWatcher.Renamed += (s, e) => onChange(s, e);
                _themesWatcher.EnableRaisingEvents = true;
                Application.Current.Exit += (_, _) => _themesWatcher?.Dispose();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _themesWatcher = null;
            }
        }, DispatcherPriority.ApplicationIdle);
    }

    private sealed class SettingsPaletteItems : IPaletteItemProvider
    {
        // settings.open and keymap.open are CommandIds, listed by the palette's own command list.
        public IEnumerable<PaletteItem> GetItems(PaletteQuery query)
        {
            if (query.Mode != PaletteMode.Anything) yield break;
            yield return Item("Open Default Settings", "Read-only defaults.json", OpenDefaultsCommand, query);
            yield return Item("Keymap Reference", "Every command id with its scope and current bindings", KeymapReferenceCommand, query);
            yield return Item("Open Themes Folder", "Drop theme .json files here", OpenThemesFolderCommand, query);
        }

        private static PaletteItem Item(string title, string detail, string id, PaletteQuery query) =>
            new(title, "command", _ => AppServices.Commands.TryExecute(id, new CommandContext
            {
                CommandId = id,
                Scope = CommandScope.Global,
                Window = query.Window,
                Workspace = query.Workspace,
            }))
            {
                Detail = detail,
                SearchText = title + " " + id,
            };
    }
}
