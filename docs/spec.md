# TablePlsu — Feature Spec

A personal PostgreSQL client in the style of TablePlus, for Windows only.
**Stack:** .NET 10 WPF, Npgsql, AvalonEdit, SSH.NET (SSH tunnel). Single user, no cloud, no license, no telemetry.

**Top priorities, in order:** (1) opens fast and returns query results fast, (2) everything can be customised through an editable JSON settings file, (3) SQL editor text is autosaved to disk all the time and restored on the next launch.

**Tiers:** **P0** is the MVP and ships first. **P1** comes next. **P2** comes later.

---

## 0. Non-functional requirements (P0, they apply to everything)

| Requirement | Concrete behavior |
|---|---|
| Cold start | The welcome window is interactive in under 400 ms on a warm disk. Build with ReadyToRun and TieredPGO. No splash screen and no network work before first paint. Settings, theme and connection list load synchronously from small JSON files. Everything else loads after first render. |
| Startup restore | If `reopenWorkspacesOnStartup` is true, the last workspaces and their SQL tabs come back. Editor text appears straight from disk before the DB connection finishes. Connections open in the background, and the status bar shows "connecting…". |
| Connection speed | Use Npgsql pooling (one data source per connection profile). A dedicated metadata connection runs separately from the query connection, so the sidebar never blocks queries. Bootstrap commands run once per physical connection. |
| Metadata cache | The schema tree, columns and functions are cached per connection+database in `%LOCALAPPDATA%\TablePlsu\cache\`. The sidebar and autocomplete draw from the cache right away, then refresh from the server in the background. Ctrl+R forces a refresh. |
| Non-blocking UI | All DB I/O is async and runs off the UI thread. The window never freezes. You can scroll, type and switch tabs while a query runs. |
| Streaming results | Rows render as soon as the first batch arrives (batches of about 200, marshalled to the UI). The row count updates live. Fetching stops at the row limit. |
| Grid performance | The grid uses row and column virtualization with container recycling. Values are formatted lazily, only for visible cells. Target: 100k rows × 30 cols scrolls smoothly, and the first 300 rows appear in under 100 ms after the server responds. |
| Timing | The result bar shows the row count and wall-clock time from send to last row. |
| Crash safety | Autosave and settings writes go to a temp file followed by an atomic `File.Replace`. A crash at any moment loses at most the last 500 ms of typing. |

**On-disk layout**

- `%APPDATA%\TablePlsu\settings.json`: all customisation (see §13), hot-reloaded on save.
- `%APPDATA%\TablePlsu\connections.json`: connection profiles. Holds no secrets.
- `%APPDATA%\TablePlsu\themes\*.json`: custom themes.
- `%APPDATA%\TablePlsu\queries\`: saved queries as plain `.sql` files plus `.meta.json` (P1).
- `%LOCALAPPDATA%\TablePlsu\editors\{connectionId}\{tabId}.sql` plus `session.json`: autosaved editor tabs.
- `%LOCALAPPDATA%\TablePlsu\history\{connectionId}.jsonl`: append-only query history.
- Secrets (DB password, SSH password, key passphrase) are stored with DPAPI (`ProtectedData`, CurrentUser scope) and keyed by connection id.

**Windows key mapping rule:** Cmd becomes Ctrl, Opt becomes Alt, Return becomes Enter. Where macOS uses Cmd+Ctrl, the binding is remapped below so nothing collides. Every binding marked *(km)* can be changed in `settings.json → keymap`.

---

## 1. Connections and the welcome window

| Feature | Behavior | Shortcut | Tier |
|---|---|---|---|
| Welcome window | Shown at launch. Right side: saved connections, each with name, host/db, tag pill and status-colour stripe. Left side: Create Connection, Backup Database, Restore Database (the last two from P1). Double-click or Enter connects. Search box at the top filters by name as you type. | Ctrl+N (reopen), Ctrl+F (search) | P0 |
| Create connection form | PostgreSQL only, so there is no driver picker. Fields: Name, Status Color, Tag, Safe Mode, Host (default 127.0.0.1), Port (5432), User (postgres), Password plus storage mode, Database, Bootstrap commands. Buttons: Test (shows success with server version, or the error), Connect (save and open), Save. | — | P0 |
| Password storage modes | Dropdown on the password field. **Store** (DPAPI, default), **Ask every time** (prompt on connect, never persisted), **No password**, **Command line** (run a shell command on connect and use the trimmed stdout, for example `az account get-access-token` or AWS RDS IAM tokens). | — | P0 (Store / Ask / None), P1 (Command line) |
| SSL | SSL mode dropdown: Disable, Allow, Prefer (default), Require, Verify-CA, Verify-Full, mapped to Npgsql `SslMode`. Three file pickers: Client Key…, Client Cert…, CA Cert…. | — | P0 |
| SSH tunnel | The "Over SSH" toggle shows SSH host, port (22), user, password (with the same storage modes), "Use SSH key" plus "Import private key…" (not `.pub`), and key passphrase. A blank key path with Use SSH key ticked falls back to `~/.ssh/config`, then the default keys and Pageant/OpenSSH agent. Built-in SSH.NET local port forward to an ephemeral port. Keep-alive on the tunnel. Automatic re-tunnel on reconnect. | — | P0 |
| SSH debug log | Help menu toggle. Writes SSH handshake output and command-line password script output to the console log. | — | P1 |
| Bootstrap commands | Multi-line SQL (for example `SET search_path…`, `SET statement_timeout…`). Runs on every physical (re)connect. | — | P0 |
| Import from URL | Right-click the list, then New > Import Connection > From URL… (or the button on the form). Paste `postgresql://user:pass@host:port/db?sslmode=…` or `postgres://…`, including the `postgresql+ssh://ssh_user:ssh_pass@ssh_host:ssh_port/user:pass@host:port/db` variant. Parses and pre-fills the form for review. | — | P0 |
| Command-line / URL launch | `TablePlsu.exe "postgresql://…"` connects directly. Recognised query options: `name, statusColor, env, sslmode, safeModeLevel, lazyload`, plus `schema, table, column, operation, value, condition` to open a table with a filter applied. Optional `tablepl su://` URL-scheme registration. | — | P1 |
| Connection context menu | Connect, Edit…, Duplicate (copies every setting), Copy as URL (URL-encoded, password optional behind a confirm), Sort By (Tag / Name), Delete (confirms, and removes the DPAPI secrets too). | — | P0 (Connect / Edit / Duplicate / Delete), P1 (Copy as URL / Sort) |
| Tags | Each connection has one tag. Defaults: local, testing, development, staging, production, or NO TAG. The tag shows as a pill in the list and in the toolbar. Tag colours, plus any custom tags, are edited in `settings.json → tags`. | — | P0 |
| Status color | A colour picker in the form. Tints the workspace toolbar status bar so you can tell prod from dev at a glance. | — | P0 |
| Connection groups | New > Group… creates a group (name and optional icon). Drag connections in and out. Groups can be nested. Deleting a group asks for confirmation and deletes its connections. | — | P1 |
| Export / import connections | Export all, a group, or one connection to an encrypted `.tplsu` file (AES with a user-supplied password). Optional checkboxes to include DB and SSH passwords. Key files are never included. Import From File… reverses this. | — | P2 |
| Keep connection alive | `connections.keepAlive` (default true) runs `SELECT 1` every 30 s on idle connections. Detects a dropped connection, and the status bar offers to reconnect. | — | P0 |
| Query timeout | `connections.commandTimeoutSeconds` (default 0 = none) sets Npgsql `CommandTimeout`. | — | P0 |

## 2. Workspace, tabs and navigation

| Feature | Behavior | Shortcut | Tier |
|---|---|---|---|
| Model | Window → Workspace (one connection plus one database) → Tabs (table, view, function, SQL editor, structure). P0 runs one workspace per window, and Ctrl+N opens another window. | — | P0 |
| Multiple workspaces per window | A database stack strip on the far left holds one icon per open DB. Click an icon to switch. Its gear toggles DB/connection labels. Right-click: Close, Close others, Move to New Window. | Ctrl+Shift+] / Ctrl+Shift+[ *(km)*, Ctrl+Shift+\ (overview) | P1 |
| Open a database | Lists the server's databases with search. In P0, Enter or double-click opens the database in the current window (replacing the workspace) and Shift+Enter opens it in a new window. In P1 it opens a new workspace in the same window. Context menu: Open in new tab / in current tab, Copy name. | Ctrl+K | P0 |
| Create / rename / drop database | In the database list: New… (name, encoding, collation), Rename… (not allowed for the open DB), Drop… (requires typing the DB name, not allowed for the open DB). Subject to Safe Mode. | — | P1 |
| Open a connection | A popup of saved connections. Enter opens the connection in a new window. | Ctrl+Shift+K | P0 |
| New tab | Opens a new SQL editor by default. `general.newTabOpens` can be `"sqlEditor"` or `"empty"`. | Ctrl+T | P0 |
| Close tab | The ×, middle-click or Ctrl+W. Closing a SQL tab keeps its file in "recently closed" (last 20). Reopen it with Ctrl+Shift+T. | Ctrl+W / Ctrl+Shift+T | P0 |
| Tab reuse (preview tab) | A single click in the sidebar replaces the current tab only if it is a table/view tab with no pending changes. Otherwise it reuses another clean object tab, or opens a new one. SQL editors and dirty tabs are never replaced. Double-click pins the tab. Context menu "Open in new tab" always creates a new tab. | — | P0 |
| Tab context menu | New Tab, Close, Close Others, Close to the Right, Close All, Rename Tab (custom label, saved with the autosave session). | — | P0 |
| Tab navigation | Next or previous tab, and jump to tab 1–9. | Ctrl+Tab / Ctrl+Shift+Tab, Ctrl+] / Ctrl+[ *(km)*, Ctrl+1..9 | P0 |
| Back / forward | History of viewed objects within the workspace, like a browser. | Alt+Left / Alt+Right *(km)* | P1 |
| Reload / reconnect / disconnect | Reload workspace (data plus schema cache), reload current tab, reconnect, disconnect. | Ctrl+R / Ctrl+Alt+R / Ctrl+Shift+R / Ctrl+Alt+. | P0 |
| Full screen | Toggles full screen. | F11 | P1 |

## 3. Toolbar and status

| Feature | Behavior | Shortcut | Tier |
|---|---|---|---|
| Default toolbar | Left to right: Toggle sidebar · Connection · **Discard \| Preview \| Commit** (disabled when nothing is pending) · Safe Mode button · Database · New SQL · status bar · Notifications · Reload · Command (Open Anything) · Panels (console / right sidebar). Tooltips show the current shortcut. | — | P0 |
| Status bar | Shows connection name / db, tag pill, and a progress bar while a query runs. Background uses the status colour. Click for a popover: server version, host, db, user, TLS cipher, SSH yes/no, plus Edit, Reconnect and Disconnect. When the connection has dropped, a click reconnects. | — | P0 |
| Notifications | Lists import/export/backup jobs with live progress, Stop, success or failure, and "Open folder" (Explorer). Also lists `RAISE NOTICE` toasts. | — | P1 |
| Customize toolbar | The item order and visibility live in `settings.json → toolbar.items`. No drag UI. | — | P1 |

## 4. Open Anything (command palette)

| Feature | Behavior | Shortcut | Tier |
|---|---|---|---|
| Palette | Fuzzy-matches databases, schemas, tables, views, materialized views, functions/procedures, saved queries, recent SQL tabs and app commands. Enter opens the item. Ctrl+Enter opens a table in a new tab. Up/Down (or Ctrl+N / Ctrl+P) move the selection. Tab completes to the highlighted name. Esc clears the text, a second Esc closes. Results come from the in-memory metadata cache, under 16 ms per keystroke. | Ctrl+P | P0 |
| Anchored matching | `^ord` matches a prefix, `_items$` a suffix, `^orders$` an exact name. Anything else matches fuzzily. | — | P0 |
| App commands | With empty input, lists commands with their shortcuts: Open Connection, Open Database, Settings, Open Keymap, Cancel Running Queries, New SQL Editor, Reload, Toggle Console, and later Backup, Restore, Metrics Board, User Management, Process List, Diagram as they ship. | — | P0 (core commands), P1 / P2 (as features land) |

## 5. Left sidebar (object tree)

| Feature | Behavior | Shortcut | Tier |
|---|---|---|---|
| Sidebar | Toggled with Ctrl+0. Tabs: **Items**, **Queries** (P1), **History**. Each tab has a search box. Ctrl+Shift+F focuses the active tab's search. | Ctrl+0, Ctrl+Shift+F | P0 |
| Items tree | Sections per schema: Tables (with views and materialized views marked by icon), Functions/Procedures (overloads shown with their signature), Sequences, Types. A schema selector at the bottom chooses which schemas show. | — | P0 |
| Lazy load | `sidebar.lazyLoadSchemas` (default true) loads a schema's items only when it is expanded. One catalog query per schema, driven by `pg_class` / `pg_proc`. | — | P0 |
| Sidebar display options | Gear menu, mirrored in settings: show recent section, show functions, sequences and types, show table columns (A–Z or ordinal), load system schemas (`pg_catalog`, `information_schema`), matching strategy (Fuzzy / Contains). | — | P0 (system schemas, matching), P1 (the rest) |
| Keyboard | Space or Enter opens the item. Delete stages a drop. Alt+Delete stages a truncate. Ctrl- and Shift-click multi-select. | Space / Delete / Alt+Delete | P0 |
| Item context menu | Open in new tab, Open structure, Copy name, Copy Script As (CREATE / DROP / TRUNCATE / SELECT … LIMIT 100), Truncate… (Cascade, Restart identity), Delete… (Cascade). Both of the last two are staged until commit. | — | P0 |
| More context menu items | Clone… (`<name>_clone`, "Duplicate content" ticked by default), Export…, Import from CSV…, Pin to top / Unpin, Item overview. | — | P1 |
| Item overview | Clicking the "Tables" header shows a grid of all tables with schema, kind, owner, estimated rows and total size. | — | P1 |
| "+" menu | New Table, plus (P1) New View, New Materialized View, New Function/Procedure, New Schema. | — | P0 (Table), P1 (others) |

## 6. Table data view

| Feature | Behavior | Shortcut | Tier |
|---|---|---|---|
| Data / Structure / Index tabs | Bottom tabs on every table, plus a DDL tab for views. | Ctrl+Alt+1 / Ctrl+Alt+2 / Ctrl+Alt+3 *(km)* | P0 |
| Paging | Server-side `LIMIT/OFFSET`, page size from `table.pageSize` (default 300). Prev/next buttons, plus a page popover where you set Limit and Offset and press Go. | Ctrl+Left / Ctrl+Right (page), Ctrl+Up / Ctrl+Down (first / last row) | P0 |
| Row count | Uses the `pg_class.reltuples` estimate when the table is over `table.estimateAboveRows` (default 500k). Click the estimate to run an exact `count(*)`. | — | P0 |
| Sort | Clicking a header cycles DESC, then ASC, then none (server-side `ORDER BY`). Header menu: Sort Asc, Sort Desc, Remove sort, and Add to multi-column sort. | — | P0 |
| Filter bar | Toggled with Ctrl+F. Each row has a checkbox, column (including **Any column** and **Raw SQL**), operator and value. Enter applies that row. "+" or Ctrl+I adds a row (copied from the current one). "−" or Ctrl+Shift+I removes it. Ctrl+B toggles the checkbox. Ctrl+Enter applies all checked rows with AND, and a dropdown offers OR. Clear resets. The SQL button shows the generated `WHERE`. Esc hides the bar. Filters are remembered per table for the session. | Ctrl+F, Ctrl+Enter, Ctrl+I, Ctrl+Shift+I, Ctrl+B | P0 |
| Filter operators | `= <> < > <= >=`, IN, NOT IN, IS NULL, IS NOT NULL, BETWEEN, NOT BETWEEN, LIKE, ILIKE, Contains, Not contains, Has prefix, Has suffix, plus case-insensitive (ILIKE) variants of those four. Values are always sent as parameters, never concatenated (except Raw SQL). | — | P0 |
| Filter defaults | Settings `filter.defaultColumn` (pk / any / raw), `filter.defaultOperator`, `filter.columnSort`, `filter.defaultState` (remember / show / hide), `table.defaultSort` (none / pkAsc / pkDesc). | — | P1 |
| Quick filter | Header menu "Filter with column". Cell menu Quick Filter: = value, Contains, Has prefix, IN, IS NULL. | — | P1 |
| Column chooser | Token field of column names, then Apply, which narrows the `SELECT` list. Header menu also has "Hide this column" and "Show all columns". | Ctrl+Alt+F | P1 |
| Follow foreign key | Cells in FK columns show an → icon. Clicking it opens the referenced table filtered to that row. | — | P1 |
| Column widths / positions | Remembered per table in the cache folder. | — | P1 |
| Inline edit | Double-click, Enter or F2 edits a cell. Tab moves to the next cell. Each edit is staged and highlighted. The type-aware editor handles bool, date/time, json and arrays as text. | Enter / F2 / Tab | P0 |
| Set Value menu | Alt+Enter or right-click > Set Value: NULL, DEFAULT, EMPTY, NOW() (date/time columns). Add a file… (bytea) is P1. | Alt+Enter | P0 |
| Row operations | Insert row (or double-click below the last row). Duplicate selected rows. Delete rows (staged, shown struck through). | Ctrl+I / Ctrl+D / Delete | P0 |
| Copy / paste | Ctrl+C copies rows as TSV (pastes cleanly into Excel). Ctrl+Shift+C copies only the selected cells. Ctrl+V pastes as new rows. Ctrl+Shift+V pastes into the selected cells. Range selection works by drag or Shift+arrows. | Ctrl+C / Ctrl+Shift+C / Ctrl+V / Ctrl+Shift+V | P0 |
| Copy As | Submenu: Plain text, CSV, CSV with header, JSON, Markdown table, HTML, INSERT statements. Also Copy All Column Values. | — | P1 |
| Row detail sidebar | Space toggles a right sidebar showing one field per line, with a field search box. Editing a field there with several rows selected changes every selected row. Each field has a quick menu: NULL, DEFAULT, EMPTY, NOW(), PRETTY / MINIFY (JSON). | Space | P1 |
| Quick Look | Ctrl+Enter or middle-click a cell opens a larger viewer/editor. JSON values get a Raw / Tree toggle plus PRETTY and MINIFY. bytea values show image preview, Save as…, Add a file… and hex. Pop out to its own window. Esc closes. | Ctrl+Enter / middle-click | P1 |
| Spatial preview | PostGIS geometry drawn on a map. | — | P2 |
| View using encoding | Changes `client_encoding` for display. | — | P2 |

## 7. Structure, indexes and objects

| Feature | Behavior | Shortcut | Tier |
|---|---|---|---|
| Structure view (read) | A grid with one row per column: name, data_type, is_nullable, default, foreign_key, comment. Also shows the Primary key token field, a search box (Ctrl+F) and a DDL button (generated CREATE TABLE in a popover, with Copy). | Ctrl+Alt+2 | P0 |
| Index view (read) | Shows index_name, is_unique, columns, method (btree / gin / gist / hash / brin), partial condition and comment. | Ctrl+Alt+3 | P0 |
| Create table | The "+" menu opens a new-table structure editor: Name, Primary key, column grid. Add columns with "+ Column" or Ctrl+I. Ctrl+S generates CREATE TABLE and runs it through the review/commit flow. | Ctrl+I, Ctrl+S | P0 |
| Edit columns | Staged: add, drop, rename, change type (`USING` cast prompt on failure), nullability, default (dropdown: NULL, EMPTY, CURRENT_TIMESTAMP, now(), gen_random_uuid(), nextval(…)), comment. Rename the table through the Name field. | Ctrl+I / Delete / Space (sidebar form) | P1 |
| Primary key / FK editor | PK token field with column autocomplete. FK popover: referenced table and columns, ON UPDATE / ON DELETE. Lists existing FKs to edit or delete. | — | P1 |
| Index editing | Add (name, columns, unique, method, WHERE), edit (as drop plus create), delete. All staged. | Ctrl+I / Delete | P1 |
| Triggers | A "Triggers" button opens a list (name, timing, event) and a statement editor. Add, edit and drop are staged. | Ctrl+I / Delete | P1 |
| Views / materialized views | Data, Structure and DDL tabs. DDL is editable for views (CREATE OR REPLACE, rename through Name) and read-only for materialized views. Materialized views get a "Refresh Materialized View" action. | Ctrl+S | P0 (open and read DDL), P1 (create / edit) |
| Functions / procedures | Clicking opens `pg_get_functiondef` in an editable SQL tab, and Ctrl+S runs it. New… opens a template. Delete stages `DROP FUNCTION name(signature)`. | Ctrl+S | P0 (view / edit / run), P1 (create template, staged drop) |
| Copy column definitions | Copy As from the structure grid. | — | P1 |

## 8. Pending changes, code review and Safe Mode

| Feature | Behavior | Shortcut | Tier |
|---|---|---|---|
| Pending changes model | Every GUI edit (cells, rows, structure, indexes, drops, truncates) is staged locally and highlighted (new, modified, deleted colours come from the theme). SQL editor queries are never staged. | — | P0 |
| Commit | Sends all staged statements in **one transaction**. Any error rolls the whole thing back, keeps the changes staged, and highlights the failing statement. Writes to history. If nothing is staged and a SQL tab has focus, Ctrl+S flushes autosave or saves the linked `.sql` file. | Ctrl+S | P0 |
| Preview (code review) | A window lists the generated SQL (UPDATE … WHERE pk = …, INSERT, DELETE, ALTER…) with Commit, Discard, and "Open in window" (non-modal, live-updating). Tables without a PK: editing is disabled, with a banner that explains why. | Ctrl+Shift+P | P0 |
| Discard | Drops all staged changes, reloads the tab, and clears undo. | Ctrl+Shift+Delete | P0 |
| Undo / redo pending edits | Steps through the staged-change stack. | Ctrl+Z / Ctrl+Y (Ctrl+Shift+Z) | P0 |
| Safe Mode levels | Per connection: **Silent** (default), **Alert 1** (confirm every query), **Alert 2** (confirm except SELECT / EXPLAIN / SHOW / WITH-select), **Safe 1** (DB password for every query), **Safe 2** (password except reads). The dialog lists the exact statements, then Send or Cancel. Applies to commits, editor runs, kill, backup / restore and DB drop. The toolbar button shows the level. Lowering the level from Safe 1/2 requires the password. `security.defaultSafeMode` sets the level for new connections. | — | P0 |
| Log original values | `history.logOriginalValues` (default false). When on, UPDATE and DELETE commits store the affected rows' prior values as CSV in the history entry. | — | P1 |
| App passcode lock | Master passcode at startup and after an idle timeout. | — | P2 |

## 9. SQL query editor (AvalonEdit)

| Feature | Behavior | Shortcut | Tier |
|---|---|---|---|
| Open editor | Editor on top, results below with a resizable splitter. Ctrl+Enter while focus is not in an editor or grid (for example the sidebar) opens a new editor. | Ctrl+E / Ctrl+T | P0 |
| **Autosave (core)** | Every SQL tab is written to `editors\{connId}\{tabId}.sql` 500 ms after the last keystroke (debounced, atomic write). It is also written immediately on run, format, tab close, window deactivate and app exit. `session.json` stores tab order, titles, caret/selection, scroll offset, folds and the active tab. On launch or reconnect, **all** SQL tabs are restored per connection. Unlike TablePlus, the default is `"all"`. `editor.autoSaveTabs`: `"all"` \| `"first"` \| `"none"`. Text restores even when the DB is unreachable. Closed tabs go to "recently closed" instead of being deleted. Saves never block the UI. | — | P0 |
| Run current | Runs the selection, or the statement under the caret. A Postgres-aware splitter skips semicolons inside quotes, `$$` dollar-quotes, comments and E'' strings. | Ctrl+Enter *(km)* | P0 |
| Run all | Runs every statement in sequence. | Ctrl+Shift+Enter *(km)* | P0 |
| Run button menu | Run Current, Run All, Default ▸ (which action the button runs), Save SQL as…, Add to Queries (P1), Change shortcuts… (opens the keymap). | — | P0 |
| Highlight current query | Background tint on the statement Run Current would execute. Updates as the caret moves. `editor.highlightCurrentQuery` (default true). | — | P0 |
| Row limit | Dropdown beside Run: No limit, 100, 300, 500, 1000, 5000, 10000, 50000, 100000, 500000. Implemented by stopping the fetch after N rows and cancelling the rest, so the SQL is not rewritten. Default 1000 for speed. Remembered per connection. | — | P0 |
| Cancel running query | While a query runs, Run becomes Cancel. Uses Npgsql `NpgsqlCommand.Cancel()` (a protocol cancel request on a separate socket). Rows already loaded stay visible. Esc also cancels when the results pane has focus. | Ctrl+Shift+. / Cancel button | P0 |
| Return on error | `editor.returnOnError` (default false). With multiple statements, when it is off, the error is recorded and the run continues. | — | P0 |
| Autocomplete | Pops up as you type. Suggests schemas, tables, views, columns (alias-aware, including CTE names and `FROM x AS a … a.`), functions, keywords, and later saved-query keywords. Ranked by prefix, then fuzzy. Fed from the metadata cache. Esc toggles the popup. Up/Down or Ctrl+N / Ctrl+P move. The accept key is `editor.completeKey`: `"both"` \| `"enter"` \| `"tab"`. Options: suggest tables / functions / keywords, disable all, add trailing space, prefix schema (non-`public` only), uppercase keywords, auto-close brackets and quotes. | Esc, Enter / Tab | P0 |
| Syntax highlighting | PostgreSQL grammar: keywords, types, strings, `$$` bodies, comments, numbers, `::` casts, params. Colours come from the theme. | — | P0 |
| Find / replace | AvalonEdit search panel with regex, match case and whole word. | Ctrl+F / Ctrl+H / F3 / Shift+F3 | P0 |
| Comments | Toggle line comment (`-- `) and block comment (`/* */`). | Ctrl+/ / Ctrl+Shift+/ *(km)* | P0 |
| Font size zoom | Changes the editor font size, persisted to settings. Ctrl+mouse wheel also works. | Ctrl+= / Ctrl+- / Ctrl+0 is not used (it toggles the sidebar) *(km)* | P0 |
| Line wrap, invisibles | `editor.wordWrap` (default true), `editor.showInvisibles` (default false). Both also in the gear menu. | — | P0 |
| Code folding | Folds per statement and by indentation. Fold state is saved in the autosave session. | Ctrl+Shift+[ / Ctrl+Shift+] | P1 |
| Multiple carets | Ctrl+Click adds or removes a caret. Alt+Drag makes a column selection. Ctrl+Alt+Up/Down adds a caret on the line above or below. Ctrl+D selects the next occurrence. Ctrl+Shift+L selects all occurrences. Esc returns to a single caret. AvalonEdit only does rectangular selection natively, so full multi-caret is a custom extension. | Ctrl+D / Ctrl+Click / Alt+Drag / Ctrl+Shift+L | P1 (rectangular selection is P0) |
| Format SQL | Beautify the selection or the current statement, honouring the uppercase-keywords setting. Uglify collapses it to one line. Indent options: tabs or spaces, width 2/4/8, style standard / tabular-left / tabular-right. Case transforms: Upper, Lower, Capitalize. | Ctrl+Shift+F is taken; use **Ctrl+Alt+L** (beautify) / Ctrl+Alt+Shift+L (uglify) *(km)* | P1 |
| Save / open files | Save as `.sql`. Open a `.sql` file into a new tab that stays linked to it (and autosaves to it). Open Recent. | Ctrl+Shift+S / Ctrl+O | P0 (save as / open), P1 (recent) |
| Toggle results pane | Shows or hides the results area under the editor. | Ctrl+` | P0 |
| Key binding mode | `editor.keyBindingMode`: `"default"` \| `"vim"`. | — | P2 |
| Split panes | Split Down (stacked editors in one tab, each with its own results). Split Right (a new pane with its own tab strip). Select next / previous pane. Close pane. | Ctrl+Shift+D, Ctrl+Alt+] / Ctrl+Alt+[ *(km)* | P1 |
| Query parameters | Off by default (`editor.queryParams.enabled`). Placeholder format (`:name` by default; also `${name}`, `{{name}}`, `%name%`). The parser skips `::` casts, quoted strings (unless `searchInQuotes`), `$n` and jsonb `?` operators. On run, a Variables dialog lists one row per parameter, pre-filled with the last values, and values are substituted as raw text. Ctrl+Enter applies. Cleanup removes stale stored values. | Ctrl+Enter (apply) | P1 |
| AI assistant | See §15. | — | P2 |

**Shortcut notes.** TablePlus uses Ctrl+I to beautify. Here Ctrl+I keeps its grid meaning (insert row), and the editor uses Ctrl+Alt+L, so Ctrl+I always means "insert" across views. Ctrl+. is the process list (§11), and Ctrl+Shift+. is the direct cancel.

## 10. Query results

| Feature | Behavior | Shortcut | Tier |
|---|---|---|---|
| One result tab per statement | Tabs are labelled "Query 1…N". Statements that return no rows still get a tab, showing the Message view. With more than 100 statements, everything collapses into one tab. `editor.resultViewMode`: `"split"` \| `"append"`. | — | P0 |
| Name a tab by comment | `-- name: X`, `-- label: X` or `-- tab: X` directly above a statement sets its tab title. The key is case-insensitive. | — | P0 |
| Pin a result tab | A pin toggle keeps the tab across re-runs. Unpinned tabs are replaced on each run. | — | P1 |
| Data / Message views | The Message view shows affected rows, errors (with position, so the editor jumps to and underlines the error line), and `RAISE NOTICE` output. | — | P0 |
| Result bar | Shows row count (live while streaming), elapsed time, Export…, and Search. | — | P0 |
| Copy | Same Ctrl+C / Ctrl+Shift+C behavior as the table grid. Copy As formats arrive in P1. | Ctrl+C / Ctrl+Shift+C | P0 |
| Edit results inline | Allowed only when the result comes from a single table that has a PK in the result set. Otherwise the grid is read-only and says why. Uses the same staged commit / preview flow. | Ctrl+S | P1 |
| Search in results | Ctrl+F while the grid has focus. Filters loaded rows client-side: column or Any, Contains / Not contains / prefix / suffix, Match Case. Replace and Replace All produce staged edits. | Ctrl+F | P1 |
| Export result | CSV, JSON, SQL INSERT (Excel in P2), with field selection. Choose this result or all tabs. The Message view exports as .txt. | — | P1 |
| Chart view | Line, bar or pie, with x/y (or label/value) pickers. `-- barchart x: a, y: b` comment directives. Export as PNG. | — | P2 |

## 11. History, saved queries and process list

| Feature | Behavior | Shortcut | Tier |
|---|---|---|---|
| Query history | Every statement sent is appended to the per-connection JSONL file: editor runs, GUI commits, user management. Each entry holds timestamp, DB, duration, rows and error flag. The History tab shows newest first with search. Double-click inserts into a new SQL tab. Menu: Run, Copy, Save As…, Delete, Clear all History, Show in Explorer. `history.enabled`, `history.maxEntries` (default 10,000 per connection). | Delete | P0 |
| Console log | A bottom panel logging every query, including metadata and background queries, with timestamps. Optional syntax highlighting. Clear button. Keeps the last 1,000 entries, and very large statements are truncated. | Ctrl+Alt+C *(km)* | P0 |
| Saved queries (Queries tab) | A folder tree of `.sql` files with `.meta.json` holding name and keywords. "+" or New > File… creates one. "Add to Queries" works from the editor (selection, current statement or whole editor) and from history. Double-click opens the query in a linked tab, or focuses it if already open. Menu: Run, Copy, Edit / Rename, Insert into SQL Editor, Show in Explorer, Delete. Folders support drag-move and rename. Sort by Name, Created or Modified. Edits autosave back to the file (`queries.autoSave`, default true). | — | P1 |
| Keyword binding | A saved query's keywords become autocomplete items. Accepting one replaces the typed keyword with the full query text. | Enter / Tab | P1 |
| Shared query folder | Mount an external folder of `.sql` files into the Queries tab. | — | P2 |
| Process list | A modal sheet that uses its own separate connection. Shows `pg_stat_activity` (non-idle sessions) with pid, datname, usename, application_name, client_addr, query_start, state and query, newest first, with sortable columns and Refresh. Multi-select, then **Cancel** (`pg_cancel_backend`) or **Terminate** (`pg_terminate_backend`), behind Safe Mode. Context menu: Copy As, Quick Look. | Ctrl+. | P1 |

## 12. Import, export, backup, restore and search

| Feature | Behavior | Shortcut | Tier |
|---|---|---|---|
| Export tables | Select one or more tables/views, then a dialog with an item checklist (across schemas) and format tabs. **CSV**: delimiter, quote, line break (LF/CRLF), decimal separator, NULL→empty, line break→space, header row. **JSON**: array of objects. **SQL**: per-item Structure / Drop / Data checkboxes, optional .gz. A file-name pattern can use `{connection} {database} {table} {date:yyyyMMdd}`. Produces one file per table, or one .sql file. Runs in the background, reports to Notifications, and uses `COPY TO STDOUT` for speed. | Ctrl+Shift+E | P1 |
| Export with column selection | Single table with field checkboxes. For SQL: structure / content / DROP IF EXISTS. | — | P1 |
| Export current page | Grid context menu. Exports only the visible page. | — | P1 |
| Excel export | `.xlsx`, one file per table. | — | P2 |
| Import CSV wizard | Previews the first 100 rows. Options: delimiter, quote, encoding, header row, target table or create new (auto-detect types or all text), matching (by name+order / name / order, case-insensitive variants), per-column mapping or "Do not import", on conflict STOP or `ON CONFLICT DO UPDATE`. Uses binary `COPY` when there is no conflict handling. Runs in the background. | — | P1 |
| Import JSON wizard | Same flow as CSV, minus delimiter, quote and header. | — | P1 |
| Import SQL dump | Previews the first 100k characters, then executes statements one by one with progress and stop-on-error. | — | P1 |
| Backup | A standalone window (also reachable from the welcome screen). Pick connection and database. Locates `pg_dump` (newest installed `C:\Program Files\PostgreSQL\*\bin`, or `backup.pgBinPath` in settings) and warns when its major version is older than the server's. Options field with an "Add option…" menu (`--format=custom` by default, `--data-only`, `--schema-only`, `--clean`, `--create`, `--no-owner`). File-name pattern, destination folder, live stdout/stderr log, Cancel (kills the process). The password goes in through the `PGPASSWORD` env var, never on the command line. Works over the SSH tunnel. Last options are remembered per connection. Windows toast on completion if the app is unfocused. | — | P1 |
| Restore | Pick connection and target DB (or "New database"). `pg_restore` options (`--single-transaction` by default, `--clean`, `--create`, `--exit-on-error`, `--no-owner`, `--data-only`, `--schema-only`). Picking a plain `.sql` file redirects to Import SQL Dump. Safe Mode confirmation, live log, Cancel. | — | P1 |
| Search in database | Replaces the sidebar with a panel: value, operator (Contains, Not contains, =, Has prefix, Has suffix), scope (all tables or selected tables), Start. Runs one query per table (`row::text ILIKE` or per-column casts) and streams results grouped by schema with match counts. Pause / Resume. Clicking a result opens that table filtered on Any column. Done closes the panel. | — | P1 |

## 13. Settings, themes and keymap (customisation)

| Feature | Behavior | Shortcut | Tier |
|---|---|---|---|
| **settings.json** | One editable file is the source of truth. Ctrl+, opens it in an in-app AvalonEdit tab with JSON highlighting, schema-driven autocomplete and validation (bundled `settings.schema.json`, with errors listed inline). Saving hot-reloads instantly with no restart. External edits are picked up by a FileSystemWatcher. Bad JSON keeps the last good config and shows an error banner. "Open defaults" shows the read-only default file, and user settings override it key by key. | Ctrl+, | P0 |
| Settings keys (P0) | `general.reopenWorkspacesOnStartup`, `general.newTabOpens`, `general.keepRunningInTray` (false: closing the last window hides to the tray), `general.startWithWindows` (false: start hidden in the tray at sign-in). `editor.fontFamily` (default "Cascadia Mono"), `editor.fontSize` (12, range 9–100), `editor.lineHeight` (1.0–5.0), `editor.tabSize`, `editor.insertSpaces`, `editor.wordWrap`, `editor.showInvisibles`, `editor.highlightCurrentQuery`, `editor.completeKey`, `editor.autocomplete.{tables,functions,keywords,disabled,addSpace,prefixSchema,uppercaseKeywords,autoClosePairs}`, `editor.autoSaveTabs`, `editor.autoSaveDebounceMs` (500), `editor.returnOnError`, `editor.resultViewMode`, `editor.defaultRowLimit`. `grid.fontFamily`, `grid.fontSize`, `grid.rowPadding`, `grid.nullDisplay` ("NULL"), `grid.dateFormat`. `sidebar.fontFamily`, `sidebar.fontSize`, `sidebar.rowPadding`, `sidebar.lazyLoadSchemas`, `sidebar.loadSystemSchemas`, `sidebar.matching`. `table.pageSize`, `table.estimateAboveRows`. `connections.keepAlive`, `connections.commandTimeoutSeconds`. `security.defaultSafeMode`. `history.enabled`, `history.maxEntries`. `theme.mode` (`"auto"\|"light"\|"dark"`), `theme.light`, `theme.dark`, `theme.overrides`. `keymap`, `tags`, `toolbar.items`. | — | P0 |
| Themes | Built-in **Light** and **Dark**, plus **Auto** (follows Windows app light/dark mode and switches live). Each theme is a JSON file with `base` (light/dark) and colour groups: **app** (window, sidebar, tabs, toolbar, accent), **editor** (background, foreground, keyword, string, number, comment, type, function, operator, selection, current-query background, error underline, caret, line numbers), and **grid** (background, alt row, grid lines, header, selection, cell-selection border/background, pending new / modified / deleted row backgrounds, NULL text). Drop a file in `themes\` and it is available. `theme.overrides` tweaks single colours on top of the active theme. Switching applies instantly through WPF `DynamicResource` with no restart. | — | P0 |
| Extra built-in themes | Additional dark and light variants, plus "Export current theme" and "Duplicate as custom". | — | P1 |
| Import theme from Git | Install themes from a repository URL. | — | P2 |
| Keymap | `keymap` in settings.json maps command id to binding (for example `"query.runCurrent": "Ctrl+Enter"`, `"tab.next": "Ctrl+]"`, `"table.showStructure": "Ctrl+Alt+2"`, `"editor.toggleLineComment": "Ctrl+/"`). **Every** command is rebindable, which goes further than TablePlus. `null` unbinds. Conflicts are reported as validation warnings, and the first binding wins. Bindings must include Ctrl or Alt unless they are F-keys or the existing single-key grid/sidebar keys (Space, Delete, Enter). Menu items, tooltips and the palette always show the current binding. "Open Keymap" in the palette jumps to the section. | — | P0 |
| Settings GUI | A searchable form over the same JSON (Fonts & Themes with live preview, Editor, Table, Keymap recorder with "Type shortcut…" and Reset per row). Optional, because the JSON is the primary interface. | — | P2 |
| Data locations | `paths.queriesHistory` and `paths.other` override the default folders (useful for OneDrive sync). | — | P1 |

## 14. Database administration

| Feature | Behavior | Shortcut | Tier |
|---|---|---|---|
| User management | Tools > User Management. Roles listed on the left. **General** tab: name, password, can login, valid until. **Role attributes**: CREATEDB, SUPERUSER, REPLICATION, BYPASSRLS, CREATEROLE. **Privileges** tab: a schema → table → column tree with Available ⇄ Granted lists (SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER). New user, Delete. Apply shows a preview of the generated `CREATE ROLE / ALTER ROLE / GRANT / REVOKE` and logs it to history. Safe Mode applies. | — | P1 |
| Metrics board | A per-connection dashboard canvas. Boards listed in the left sidebar (New, Rename, Duplicate, Delete, Import/Export as JSON). Objects: data table, scoreboard (score plus detail), bar / line / pie chart, input field, dropdown (fixed or query-backed), button. Each object has label, SQL and refresh rate (1 s–30 min, on event, or manual). `$var` text substitution from linked inputs. Drag to link objects. Lock / Grid toggles. Start/stop refresh for the whole board. Uses a dedicated connection. Exporter, Importer and Connection objects are P2. | — | P1 |

## 15. Later (P2)

| Feature | Behavior | Shortcut | Tier |
|---|---|---|---|
| ER diagram | Tools > Show Diagram, the sidebar item menu, or the palette. One box per table (columns and types), FK lines, connected components share a colour. Schema dropdown. Pan, drag, zoom (in / out / reset). Sort by name or column count. Rearrange (auto layout). Search (Ctrl+F, Up/Down, Enter). Click a table for a popover (rename, colour, columns, indexes, delete). Add table (`untitled_table_N`). Drag between tables to create an FK (column-count check). Right-click a line to delete the FK. All staged, with Undo/Redo, then Ctrl+S. | Ctrl+F, Ctrl+S | P2 |
| AI assistant (LLM) | A right-sidebar Assistant tab (`ai.enabled`). Providers: Anthropic, OpenAI, OpenRouter, Ollama (default `http://localhost:11434`), or a custom OpenAI-compatible endpoint (host, sub path, key). Keys stored with DPAPI. "@ Add context" adds table DDL. SQL blocks get Copy and Run. The agent may run read-only queries directly (still under Safe Mode). Write queries need an "Approve SQL" review. New chat, Clear, History. Enter sends, Shift+Enter adds a newline, Stop cancels. The privacy note states exactly what is sent. | Enter / Shift+Enter | P2 |
| MCP server | `mcp.enabled`, port 51003 (allowed 32000–61000), localhost only. Bearer access tokens (Read Only / Read & Write / Full, all or a subset of connections) stored with DPAPI and shown once. Tools: `list_connections`, `list_open_connections`, `open_connection`, `disconnect`, `execute_read_query`, `execute_query` (300 s max). Resources: saved queries, history. "Approve AI Agent Query" dialog for unsafe queries on Silent connections. Activity log window with filters, CSV export and clear. Config snippet for Claude Code (`claude mcp add`). Remote TLS access is out of scope. | Ctrl+P > MCP Server | P2 |
| Plugins | A plugin folder of C# scripts or JS (Jint). Commands placed in the Tools menu, the cell context menu and the sidebar item menu, with their own shortcuts. A trust warning on install. | — | P2 |
| Connection sources | Extra folders (OneDrive / Dropbox) whose `connections.json` shows as its own section on the welcome window. Secrets always stay in local DPAPI. | — | P2 |

---

## P0 build order (suggested)

1. Settings, theme and keymap infrastructure with hot reload.
2. Connection store and form (SSL, SSH, DPAPI).
3. Workspace shell, toolbar and status bar.
4. Metadata cache and sidebar tree.
5. SQL editor: autosave and restore, splitter, run current / all, streaming grid, cancel, row limit, result tabs, history, console.
6. Autocomplete.
7. Table data view: paging, sort, filters.
8. Inline edit, staged changes, preview and commit (one transaction), Safe Mode.
9. Structure / Index / DDL views and create table.
10. Open Anything.

**Dropped (not relevant to PostgreSQL, Windows or personal use):** SQLite/DuckDB file connections and file-based DB lists; MySQL/MariaDB/MSSQL/Mongo/Redis/Cassandra/BigQuery/Snowflake specifics (SSL VERIFY-IDENTITY, Domain field, MySQL privilege and resource tabs, MySQL processlist, REPLACE insert mode, Mongo syntax menu and suggestions); "Load more" paged drivers; column drag-reorder (PostgreSQL can't do it); Touch ID / Windows Hello unlock; macOS-only menu items (Hide, Services, Speech, Bring All to Front, Keychain, `open -a`); license, activation, update and beta channels; crash reporting; app language selection; admin/MDM history policy; GitHub Copilot and Codex CLI providers and the ACP custom agent; MCP remote TLS access and one-click setup for third-party agents.