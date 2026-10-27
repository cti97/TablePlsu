# TablePlsu — Architecture

The feature list is in [spec.md](spec.md). This document fixes the structure, so features can be built in parallel without stepping on each other.

## Stack
- .NET 10, WPF (`net10.0-windows`), C# latest, nullable enabled, warnings as errors in Core.
- Npgsql 10 (DB), SSH.NET (tunnel), AvalonEdit (editor), CommunityToolkit.Mvvm (MVVM source generators).
- Tests: xUnit. Integration tests read the `TABLEPLSU_TEST_PG` env var (an Npgsql connection string) and **skip** when it is unset. The local fixture is `Host=localhost;Port=54329;Username=postgres;Database=tableplsu_test`, seeded by `tests/seed.sql` (start it with `scripts/start-testdb.sh`).

## Speed rules (non-negotiable)
1. Startup does no DB or network work, does not touch Npgsql types, and does no reflection scanning. `App.OnStartup` loads settings, theme and connections (small JSON files) and shows the window. Everything else is lazy.
2. Publish with `ReadyToRun`, `TieredPGO`, `TieredCompilation`, `InvariantGlobalization=false`, `SatelliteResourceLanguages=en`. Startup is measured by `--measure-startup`, which logs ms to first `ContentRendered` and exits.
3. All DB I/O is async and runs off the UI thread. Results stream in batches of 200 rows, marshalled to the UI with `Dispatcher.InvokeAsync(…, DispatcherPriority.Background)`.
4. Grids are virtualized (`EnableRowVirtualization`, `EnableColumnVirtualization`, `VirtualizingPanel.VirtualizationMode=Recycling`). Rows are `object?[]`, and columns bind via `Binding("[i]")` with a shared null/format converter.

## Projects
```
src/TablePlsu.Core     (net10.0, no WPF)  — all logic that can be unit-tested
src/TablePlsu.App      (net10.0-windows WPF) — views, view-models, WPF services
tests/TablePlsu.Tests  — xUnit, references Core
```

## Core layout (namespace = folder)
| Folder | Owns |
|---|---|
| `Paths/AppPaths.cs` | `%APPDATA%\TablePlsu`, `%LOCALAPPDATA%\TablePlsu`, overridable root for tests. |
| `IO/AtomicFile.cs` | Write to temp, then `File.Replace`/`Move`. Used for every write. |
| `Settings/` | `AppSettings` (typed record tree mirroring spec §13 keys), `SettingsService` (embedded `defaults.json` deep-merged with the user `settings.json`, `FileSystemWatcher` hot reload, `Changed` event, keeps the last good config on parse errors and exposes `LastError`). |
| `Themes/` | `ThemeDefinition` (base + flat `Dictionary<string,string>` colours keyed like `editor.keyword`), `ThemeService` (built-in light/dark embedded JSON, user `themes\*.json`, overrides). |
| `Keymap/` | `KeyChord` parse/format ("Ctrl+Shift+Enter"), `KeymapService` (defaults from `CommandIds` + user overrides, conflict detection). |
| `Commands/CommandIds.cs` | String constants for every command id plus their default bindings. |
| `Connections/` | `ConnectionProfile` (id, name, color, tag, safeMode, host, port, user, db, passwordMode, ssl*, ssh*, bootstrap, rowLimit), `ConnectionStore` (connections.json), `SecretStore` (DPAPI, file-backed), `ConnectionUrl` (parse/format postgresql:// and postgresql+ssh://). |
| `Db/` | `DbSession` (one per open workspace: owns the SSH tunnel, the `NpgsqlDataSource`, and separate query and metadata connections; Connect/Reconnect/Disconnect/KeepAlive; `ExecuteAsync(sql, rowLimit, IProgress<ResultBatch>, ct)` streams results per statement; cancel via `NpgsqlCommand.Cancel`), `QueryResult` / `ResultColumn` / `ResultBatch` / `StatementResult` (columns, rows `object?[]`, affected, notices, error with position, elapsed), `SshTunnel`. |
| `Sql/` | `StatementSplitter` (PG-aware: quotes, `$tag$`, comments, E'' strings; returns spans with offsets; `StatementAt(caret)`), `SqlIdent` (quoting), `TabNameDirective` (`-- name:`), `SqlFormatter` (P1). |
| `Metadata/` | `DbMetadata` model (schemas → tables/views/matviews/functions/sequences/types, columns, PK, FKs, indexes), `MetadataLoader` (catalog queries), `MetadataCache` (JSON on disk per connection+db, memory, background refresh). |
| `Completion/` | `CompletionEngine`: pure function `(text, caret, DbMetadata, options) → CompletionItem[]`, alias/CTE aware. |
| `Data/` | `TableQueryBuilder` (SELECT with filters, sort, paging, parameterized), `FilterCondition` and operators, `PendingChanges` (staged cell edits, inserts, deletes, DDL ops, undo/redo), `ChangeSqlGenerator` (UPDATE/INSERT/DELETE by PK, parameter-safe literal rendering for preview), `DdlGenerator` (CREATE TABLE, column ops), `SafeModePolicy`. |
| `Editors/` | `EditorSessionStore` (autosave: `editors\{connId}\{tabId}.sql` plus `session.json`, debounced, recently-closed list). |
| `History/` | `HistoryStore` (JSONL per connection, capped). |
| `Search/` | `FuzzyMatcher` (used by the palette and sidebar). |

## App layout
| Folder | Owns |
|---|---|
| `App.xaml(.cs)` | Startup: `AppServices.Init()`, theme, then `MainWindow`. `--measure-startup` flag. |
| `Infrastructure/` | `AppServices` (static composition root holding singletons), `ThemeApplier` (pushes ThemeDefinition colours into `Application.Resources` as `SolidColorBrush` keys `"c.editor.keyword"` and so on; all XAML uses `{DynamicResource c.…}`), `CommandRegistry` (id → `ICommand` handler + `CanExecute`, scoped: global, workspace, editor, grid), `KeyBindingRouter` (one `PreviewKeyDown` handler on each window that resolves the chord via KeymapService and routes to the focused scope first, then global), `Dialogs`, `UiDispatch`. |
| `Shell/` | `MainWindow` (hosts the WelcomeView or WorkspaceView), `MainViewModel`. |
| `Features/Welcome/` | Welcome view, connection list, connection form, URL import. |
| `Features/Workspace/` | `WorkspaceView`/`WorkspaceViewModel` (toolbar, status bar, tab strip, bottom console, layout), `TabViewModelBase` (Title, Kind, IsDirty, IsPreview, CanClose, `Save()`), `TabHost`. |
| `Features/Sidebar/` | Object tree, schema selector, history tab, search. |
| `Features/Palette/` | Open Anything (Ctrl+P), Open Database (Ctrl+K), Open Connection (Ctrl+Shift+K). |
| `Features/SqlEditor/` | `SqlEditorTabViewModel` and view (AvalonEdit, highlighting `.xshd` generated from theme, current-query highlight, run/cancel/row-limit, result tabs, message view, autosave hook, completion adapter). |
| `Features/Results/` | `ResultGrid` user control (virtualized DataGrid over `object?[]` rows, copy TSV, range selection) reused by the editor and table view. |
| `Features/TableData/` | Table tab: Data/Structure/Index bottom tabs, paging, sort, filter bar, inline edit, pending changes, Preview window, commit. |
| `Features/Structure/` | Structure, index and DDL views, create table editor, function editor. |
| `Features/SafeMode/` | Confirm dialog and password prompt. |
| `Features/Settings/` | Opens settings.json in an editor tab, validates on save. |
| `Features/Console/` | Console log panel. |

### Extension points (defined by the foundation and stable after it)
- `IFeature { void Register(FeatureContext ctx); }`, listed explicitly in `Infrastructure/Features.cs` (one line per feature, no reflection). `FeatureContext` exposes `CommandRegistry`, the tab factory registry (`ctx.Tabs.Register(kind, Func<TabRequest, TabViewModelBase>)`), sidebar context menu contributors, palette item providers, and toolbar items.
- `TabRequest { Kind, Schema, Name, ObjectKind, Sql, Pinned, InNewTab }`. The workspace calls `OpenTab(request)`, which applies the preview-tab reuse rules from the spec.
- `IWorkspace` (what features see): `DbSession Session`, `ConnectionProfile Profile`, `string Database`, `MetadataCache Metadata`, `OpenTab`, `ActiveTab`, `Log(string)` to the console, `History`, `SafeMode.ConfirmAsync(statements)`.

#### As built (App shell)
- `FeatureContext` also has `Panels.Register(WorkspacePanel.Sidebar|Console|Right, ws => element)` for the layout slots, `OnWorkspaceOpened(ws => …)` (restore editor tabs here, before the connection finishes), `OnWindowCreated`, and `UseSafeMode(ws => ISafeModeGate)`.
- Command handlers: `ctx.Commands.Register(id, Action<CommandContext>, Func<CommandContext,bool>? canExecute)`. `CommandContext` carries `Workspace`, `Target` (DataContext of the nearest element marked `infra:CommandScopes.Scope="Editor|Grid|Sidebar|FilterBar"`), `IsTextInputFocused`. The newest handler whose `canExecute` passes runs. A scope element's DataContext implementing `ICommandTarget` gets the command first; workspace-scope commands are forwarded to the active tab when it implements `ICommandTarget`.
- Tabs: `TabViewModelBase` (`Kind`, `Schema`, `Name`, `Title`/`CustomTitle`, `IsDirty`, `IsPreview`, `HasPendingChanges`, `IsBusy`, `CreateView()`, `Save/CommitAsync/PreviewChanges/DiscardChanges/Reload/Flush/CanCloseNow/OnClosed`). Factories: `ctx.Tabs.Register(TabKinds.X, request => vm)` (or `(request, workspace) => vm`). Views are created once per tab and cached by `TabHost`.
- `IWorkspace.Metadata` is the workspace's `MetadataCache` (Core `Metadata/`): disk cache loaded on open, server refresh after each (re)connect and on Ctrl+R (`workspace.reload`). `Changed` fires on a background thread; marshal with `UiDispatch`. Read `ws.Metadata.Current` (an immutable `DbMetadata` snapshot) or use `FindTable(schema?, name)` / `FindFunctions` / `EnsureSchemaLoadedAsync(schema)` (lazy mode, `sidebar.lazyLoadSchemas`).
- `Features/Results/ResultGrid` is the shared grid: set `ResultColumns` + `Rows` (a `BulkObservableCollection<object?[]>`, append with `AddRange`). Display cells are `CellText` elements (no per-cell bindings). Editable mode: `IsReadOnly=false`, `CellEditCommitted`, `RowStateProvider`, `RefreshRow`. Header clicks raise `SortRequested` (`ServerSort=true` disables client sorting; show order with `SetSortIndicators`). `grid.copy` / `grid.copyCells` are handled by `ResultsFeature`. Grid font resources: `grid.fontFamily`, `grid.fontSize`, `grid.cellPadding`, `grid.rowHeight` (`GridResources`).
- Settings (`Features/Settings`): `settings.open` (Ctrl+,) opens settings.json as a `TabKinds.Settings` tab (a stand-alone window on the welcome screen); `keymap.open` reveals the `"keymap"` section and opens the `keymapReference` tab; extra unbindable commands `settings.openDefaults`, `keymap.reference`, `themes.openFolder` (also palette items). Validation: Core `SettingsValidator` (embedded schema subset + keymap warnings, line/column diagnostics). Font resources for live settings: `editor.fontFamily` / `editor.fontSize` / `editor.lineHeight` and `sidebar.fontFamily` / `sidebar.fontSize` / `sidebar.rowPadding` (Thickness) / `sidebar.rowHeight` (`SettingsResources`, published after first render) — bind them with `{DynamicResource …}`. Views that cache settings values on construction must subscribe to `AppServices.SettingsChanged` (tags colours are refreshed by the Settings feature). Built-in themes: Light, Dark, Nord, Solarized Light; `themes\*.json` is watched.
- Toolbar item ids are the `settings.toolbar.items` names; built-in templates are `tb.<id>` in `WorkspaceView.xaml`. Colours are `{DynamicResource c.<theme key>}` brushes (plus `color.<key>` Colors); shared styles live in `Shell/Styles.xaml`.

## Data flow: run query
Editor → `StatementSplitter` → `SafeModePolicy` check → `DbSession.ExecuteAsync` streams `ResultBatch` per statement → the result tab VM appends rows to an `ObservableCollection<object?[]>` in batches (or a custom `BulkObservableCollection` that raises one Reset per batch) → the grid renders. History entry and console log are written per statement.

## Persistence (all atomic, all under AppPaths)
`settings.json`, `connections.json`, `secrets.dat` (DPAPI), `themes\`, `editors\`, `history\`, `cache\metadata\`.

## Testing conventions
- Core logic is TDD'd: splitter, URL parsing, settings merge, keymap parse, query builder, change SQL generator, completion engine, autosave store, history store, fuzzy matcher.
- Integration tests (`[SkippableFact]`-style: return early with a skip message when the env var is missing) for DbSession streaming/cancel, metadata loading and commit transactions.
- The App is verified by building plus running `TablePlsu.App.exe --measure-startup`, and by a UI smoke script where practical.
