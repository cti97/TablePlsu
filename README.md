# TablePlsu

A fast, keyboard-driven PostgreSQL client for Windows, modelled on [TablePlus](https://tableplus.com).
Built with .NET 10 WPF, [Npgsql](https://www.npgsql.org), [AvalonEdit](https://github.com/icsharpcode/AvalonEdit) and [SSH.NET](https://github.com/sshnet/SSH.NET).

TablePlsu is an independent personal project. It is not affiliated with or endorsed by TablePlus.

## Priorities

1. **Opens fast and returns results fast.** The first window appears in about 0.5 s. Results stream into a virtualized grid as they arrive.
2. **Everything is customisable.** One `settings.json` covers fonts, themes, colours, keymap, tags and toolbar, and changes apply as soon as you save.
3. **You never lose a query.** Every SQL tab is autosaved to disk within 500 ms and restored on the next launch, even if the app crashes or the database is down.

## Features

**Connections**
- Host, port, user, password and database, with the database picked from a dropdown filled by the server.
- SSL modes and client certificates.
- SSH tunnel with a password or private key.
- Startup ("bootstrap") SQL that runs on every connect.
- Import from a `postgresql://` URL.
- Tags and a status colour per connection, so production is easy to spot.
- Passwords are encrypted with Windows DPAPI for your user account.

**Workspace**
- Tabs, with a reusable preview tab when you open objects from the sidebar.
- **Open Anything** (Ctrl+P), **Open Database** (Ctrl+K) and **Open Connection** (Ctrl+Shift+K).
- Console log of every query the app sends.
- Optional tray mode for near-instant reopening.

**Sidebar**
- Schemas, tables, views, materialized views, functions, sequences and types.
- Fuzzy search.
- Context menus with Copy Script As, Truncate and Drop.
- Query history.
- A hint when the database you connected to is empty, listing the server's other databases.

**SQL editor**
- PostgreSQL syntax highlighting and schema-aware autocomplete that understands aliases and CTEs.
- Run the current statement (Ctrl+Enter) or all statements (Ctrl+Shift+Enter), with a row limit and cancel.
- One result tab per statement.
- Errors jump to the failing line.
- `RAISE NOTICE` output is shown.
- Find and replace, comment toggling, font zoom, and opening and saving `.sql` files.

**Table data**
- Server-side paging, sorting and filter bar, with every TablePlus operator.
- Inline editing, insert, duplicate and delete rows, and paste from Excel.
- Edits are staged, can be previewed as SQL (Ctrl+Shift+P), and are committed in one transaction (Ctrl+S).

**Structure**
- Columns, indexes, and the DDL for tables and views.
- Function editor.
- Create-table editor.

**Safe Mode** (per connection)
- Silent, confirm every query, confirm writes only, or require the password before running.

The full feature list, including what is planned next (import and export, backup and restore with `pg_dump`/`pg_restore`, process list, saved queries, ER diagram), is in [docs/spec.md](docs/spec.md).

## Requirements

- Windows 10 or 11 (x64).
- To build: the [.NET 10 SDK](https://dotnet.microsoft.com/download).
- The default self-contained build needs no installed runtime. The small framework-dependent build needs the .NET 10 Desktop Runtime.

## Build and run

```powershell
git clone https://github.com/cti97/TablePlsu.git
cd TablePlsu
dotnet build
dotnet run --project src/TablePlsu.App
```

For a fast, optimised build:

```powershell
& .\scripts\publish.ps1            # self-contained, composite ReadyToRun (~205 MB), fastest startup
& .\scripts\publish.ps1 -FrameworkDependent   # ~23 MB, needs the .NET 10 Desktop Runtime
```

Then run `artifacts\publish\TablePlsu.App.exe`. You can also pass a connection URL: `TablePlsu.App.exe "postgresql://user@host:5432/db"`.

## Customisation

Press **Ctrl+,** to open `settings.json` inside the app. It is checked against a schema as you type, and saving applies the changes immediately.

```jsonc
{
  "editor": { "fontFamily": "Cascadia Mono", "fontSize": 13, "defaultRowLimit": 1000 },
  "theme": { "mode": "auto", "dark": "Nord", "overrides": { "editor.keyword": "#C678DD" } },
  "keymap": { "query.runCurrent": "Ctrl+Enter", "tab.next": ["Ctrl+Tab", "Ctrl+]"] },
  "general": { "keepRunningInTray": true }
}
```

- **Themes:** Light, Dark, Nord and Solarized Light are built in. Add your own by dropping a JSON theme into the `themes` folder.
- **Keymap:** every command can be rebound, and `null` unbinds it. Run **Keymap Reference** from the Ctrl+P palette to see every command and its current binding.
- **Tray mode:** `general.keepRunningInTray` keeps the app running in the tray after you close the window, so reopening takes about 170 ms. `general.startWithWindows` starts it hidden when you sign in.

### Where data lives

| What | Location |
|---|---|
| Settings, connections, encrypted secrets, themes | `%APPDATA%\TablePlsu\` |
| Autosaved editor tabs, history, metadata cache | `%LOCALAPPDATA%\TablePlsu\` |

Set the environment variable `TABLEPLSU_ROOT` to keep all app data in another folder instead.

## Default shortcuts (selection)

| Action | Shortcut |
|---|---|
| New SQL tab / close / reopen closed | Ctrl+T / Ctrl+W / Ctrl+Shift+T |
| Run current / run all / cancel | Ctrl+Enter / Ctrl+Shift+Enter / Ctrl+Shift+. |
| Open Anything / database / connection | Ctrl+P / Ctrl+K / Ctrl+Shift+K |
| Commit / preview / discard staged changes | Ctrl+S / Ctrl+Shift+P / Ctrl+Shift+Delete |
| Filter bar / next page / previous page | Ctrl+F / Ctrl+Right / Ctrl+Left |
| Data / structure / indexes | Ctrl+Alt+1 / 2 / 3 |
| Toggle sidebar / console / results | Ctrl+0 / Ctrl+Alt+C / Ctrl+` |
| Settings | Ctrl+, |

## Development

```
src/TablePlsu.Core        logic with no UI: settings, connections, DB session, SQL splitter, metadata, completion, data editing
src/TablePlsu.App         WPF views, view-models and features
tests/TablePlsu.Tests     Core unit and integration tests
tests/TablePlsu.App.Tests WPF/view-model tests
tests/TablePlsu.UiFlows   end-to-end flows driven with real keystrokes
```

The design and extension points are described in [docs/architecture.md](docs/architecture.md).

**Tests.** Integration tests need a PostgreSQL server and are skipped when `TABLEPLSU_TEST_PG` is not set. To start a disposable trust-auth server on port 54329 seeded with `tests/seed.sql`, using the PostgreSQL 18 binaries:

```bash
scripts/start-testdb.sh /path/to/empty/data-dir
```

Then, in PowerShell:

```powershell
$env:TABLEPLSU_TEST_PG = "Host=localhost;Port=54329;Username=postgres;Database=tableplsu_test"
dotnet test
& .\scripts\ui-flows.ps1 -Publish   # end-to-end UI flows; needs an unlocked desktop, hands off the keyboard
```

**Performance scripts.** `scripts/measure-startup.ps1`, `scripts/measure-activation.ps1` and `scripts/perf-*.ps1`.

## License

[MIT](LICENSE) © 2026 Catalin Istratoiu
