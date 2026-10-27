namespace TablePlsu.Core.Commands;

/// <summary>
/// Where a binding is active. The App's KeyBindingRouter resolves the focused scope first (Editor, Grid, Sidebar,
/// FilterBar), then Workspace, then Global. The same chord may be bound in different scopes; within one scope it is a conflict.
/// </summary>
public enum CommandScope
{
    Global,
    Workspace,
    Editor,
    Grid,
    Sidebar,
    FilterBar,
    Welcome,
}

/// <summary>A command id with its human title, default bindings (first is the primary shown in menus) and scope.</summary>
public sealed record CommandDefinition(string Id, string Title, CommandScope Scope, IReadOnlyList<string> DefaultBindings)
{
    public CommandDefinition(string id, string title, CommandScope scope, params string[] bindings)
        : this(id, title, scope, (IReadOnlyList<string>)bindings) { }
}

/// <summary>
/// Every command id plus its default bindings. Features register handlers for these ids in the App's CommandRegistry;
/// users rebind them in <c>settings.json → keymap</c>. Order matters: on a conflict the earlier command wins.
/// </summary>
public static class CommandIds
{
    // Window / app (Global)
    public const string WindowNew = "window.new";
    public const string SettingsOpen = "settings.open";
    public const string KeymapOpen = "keymap.open";
    public const string PaletteOpenConnection = "palette.openConnection";
    public const string AppFullScreen = "app.fullScreen";

    // Workspace
    public const string PaletteOpenAnything = "palette.openAnything";
    public const string PaletteOpenDatabase = "palette.openDatabase";
    public const string TabNew = "tab.new";
    public const string TabClose = "tab.close";
    public const string TabReopenClosed = "tab.reopenClosed";
    public const string TabNext = "tab.next";
    public const string TabPrev = "tab.prev";
    public const string TabGotoPrefix = "tab.goto"; // tab.goto1 … tab.goto9
    public const string TabGoto1 = "tab.goto1";
    public const string TabGoto2 = "tab.goto2";
    public const string TabGoto3 = "tab.goto3";
    public const string TabGoto4 = "tab.goto4";
    public const string TabGoto5 = "tab.goto5";
    public const string TabGoto6 = "tab.goto6";
    public const string TabGoto7 = "tab.goto7";
    public const string TabGoto8 = "tab.goto8";
    public const string TabGoto9 = "tab.goto9";
    public const string TabReload = "tab.reload";
    public const string EditorNew = "editor.new";
    public const string WorkspaceReload = "workspace.reload";
    public const string WorkspaceReconnect = "workspace.reconnect";
    public const string WorkspaceDisconnect = "workspace.disconnect";
    public const string SidebarToggle = "sidebar.toggle";
    public const string SidebarSearch = "sidebar.search";
    public const string ConsoleToggle = "console.toggle";
    public const string ChangesCommit = "changes.commit";
    public const string ChangesPreview = "changes.preview";
    public const string ChangesDiscard = "changes.discard";
    public const string EditUndo = "edit.undo";
    public const string EditRedo = "edit.redo";
    public const string QueryCancel = "query.cancel";
    public const string TableShowData = "table.showData";
    public const string TableShowStructure = "table.showStructure";
    public const string TableShowIndexes = "table.showIndexes";
    public const string FileOpen = "file.open";

    // Editor
    public const string QueryRunCurrent = "query.runCurrent";
    public const string QueryRunAll = "query.runAll";
    public const string EditorToggleLineComment = "editor.toggleLineComment";
    public const string EditorToggleBlockComment = "editor.toggleBlockComment";
    public const string EditorFontBigger = "editor.fontBigger";
    public const string EditorFontSmaller = "editor.fontSmaller";
    public const string EditorToggleResults = "editor.toggleResults";
    public const string EditorFind = "editor.find";
    public const string EditorReplace = "editor.replace";
    public const string EditorFindNext = "editor.findNext";
    public const string EditorFindPrev = "editor.findPrev";
    public const string FileSaveAs = "file.saveAs";

    // Grid (table data view and result grids)
    public const string TableFilterToggle = "table.filterToggle";
    public const string GridInsertRow = "grid.insertRow";
    public const string GridDuplicateRow = "grid.duplicateRow";
    public const string GridDeleteRow = "grid.deleteRow";
    public const string GridSetValueMenu = "grid.setValueMenu";
    public const string GridEditCell = "grid.editCell";
    public const string GridCopy = "grid.copy";
    public const string GridCopyCells = "grid.copyCells";
    public const string GridPaste = "grid.paste";
    public const string GridPasteCells = "grid.pasteCells";
    public const string PageNext = "page.next";
    public const string PagePrev = "page.prev";
    public const string GridFirstRow = "grid.firstRow";
    public const string GridLastRow = "grid.lastRow";

    // Sidebar
    public const string SidebarOpenItem = "sidebar.openItem";
    public const string SidebarDropItem = "sidebar.dropItem";
    public const string SidebarTruncateItem = "sidebar.truncateItem";

    // Filter bar
    public const string FilterAddRow = "filter.addRow";
    public const string FilterRemoveRow = "filter.removeRow";
    public const string FilterToggleRow = "filter.toggleRow";
    public const string FilterApplyAll = "filter.applyAll";

    // Welcome window
    public const string WelcomeSearch = "welcome.search";

    public static IReadOnlyList<CommandDefinition> All { get; } =
    [
        new(WindowNew, "New Window", CommandScope.Global, "Ctrl+N"),
        new(SettingsOpen, "Settings", CommandScope.Global, "Ctrl+,"),
        new(KeymapOpen, "Open Keymap", CommandScope.Global),
        new(PaletteOpenConnection, "Open Connection", CommandScope.Global, "Ctrl+Shift+K"),
        new(AppFullScreen, "Toggle Full Screen", CommandScope.Global, "F11"),

        new(PaletteOpenAnything, "Open Anything", CommandScope.Workspace, "Ctrl+P"),
        new(PaletteOpenDatabase, "Open Database", CommandScope.Workspace, "Ctrl+K"),
        new(TabNew, "New Tab", CommandScope.Workspace, "Ctrl+T"),
        new(TabClose, "Close Tab", CommandScope.Workspace, "Ctrl+W"),
        new(TabReopenClosed, "Reopen Closed Tab", CommandScope.Workspace, "Ctrl+Shift+T"),
        new(TabNext, "Next Tab", CommandScope.Workspace, "Ctrl+Tab", "Ctrl+]"),
        new(TabPrev, "Previous Tab", CommandScope.Workspace, "Ctrl+Shift+Tab", "Ctrl+["),
        new(TabGoto1, "Go to Tab 1", CommandScope.Workspace, "Ctrl+1"),
        new(TabGoto2, "Go to Tab 2", CommandScope.Workspace, "Ctrl+2"),
        new(TabGoto3, "Go to Tab 3", CommandScope.Workspace, "Ctrl+3"),
        new(TabGoto4, "Go to Tab 4", CommandScope.Workspace, "Ctrl+4"),
        new(TabGoto5, "Go to Tab 5", CommandScope.Workspace, "Ctrl+5"),
        new(TabGoto6, "Go to Tab 6", CommandScope.Workspace, "Ctrl+6"),
        new(TabGoto7, "Go to Tab 7", CommandScope.Workspace, "Ctrl+7"),
        new(TabGoto8, "Go to Tab 8", CommandScope.Workspace, "Ctrl+8"),
        new(TabGoto9, "Go to Tab 9", CommandScope.Workspace, "Ctrl+9"),
        new(TabReload, "Reload Tab", CommandScope.Workspace, "Ctrl+Alt+R"),
        new(EditorNew, "New SQL Editor", CommandScope.Workspace, "Ctrl+E"),
        new(WorkspaceReload, "Reload", CommandScope.Workspace, "Ctrl+R"),
        new(WorkspaceReconnect, "Reconnect", CommandScope.Workspace, "Ctrl+Shift+R"),
        new(WorkspaceDisconnect, "Disconnect", CommandScope.Workspace, "Ctrl+Alt+."),
        new(SidebarToggle, "Toggle Sidebar", CommandScope.Workspace, "Ctrl+0"),
        new(SidebarSearch, "Search Sidebar", CommandScope.Workspace, "Ctrl+Shift+F"),
        new(ConsoleToggle, "Toggle Console", CommandScope.Workspace, "Ctrl+Alt+C"),
        new(ChangesCommit, "Commit Changes", CommandScope.Workspace, "Ctrl+S"),
        new(ChangesPreview, "Preview Changes", CommandScope.Workspace, "Ctrl+Shift+P"),
        new(ChangesDiscard, "Discard Changes", CommandScope.Workspace, "Ctrl+Shift+Delete"),
        new(EditUndo, "Undo", CommandScope.Workspace, "Ctrl+Z"),
        new(EditRedo, "Redo", CommandScope.Workspace, "Ctrl+Y", "Ctrl+Shift+Z"),
        new(QueryCancel, "Cancel Running Queries", CommandScope.Workspace, "Ctrl+Shift+."),
        new(TableShowData, "Show Data", CommandScope.Workspace, "Ctrl+Alt+1"),
        new(TableShowStructure, "Show Structure", CommandScope.Workspace, "Ctrl+Alt+2"),
        new(TableShowIndexes, "Show Indexes", CommandScope.Workspace, "Ctrl+Alt+3"),
        new(FileOpen, "Open SQL File…", CommandScope.Workspace, "Ctrl+O"),

        new(QueryRunCurrent, "Run Current", CommandScope.Editor, "Ctrl+Enter"),
        new(QueryRunAll, "Run All", CommandScope.Editor, "Ctrl+Shift+Enter"),
        new(EditorToggleLineComment, "Toggle Line Comment", CommandScope.Editor, "Ctrl+/"),
        new(EditorToggleBlockComment, "Toggle Block Comment", CommandScope.Editor, "Ctrl+Shift+/"),
        new(EditorFontBigger, "Increase Font Size", CommandScope.Editor, "Ctrl+="),
        new(EditorFontSmaller, "Decrease Font Size", CommandScope.Editor, "Ctrl+-"),
        new(EditorToggleResults, "Toggle Results Pane", CommandScope.Editor, "Ctrl+`"),
        new(EditorFind, "Find", CommandScope.Editor, "Ctrl+F"),
        new(EditorReplace, "Replace", CommandScope.Editor, "Ctrl+H"),
        new(EditorFindNext, "Find Next", CommandScope.Editor, "F3"),
        new(EditorFindPrev, "Find Previous", CommandScope.Editor, "Shift+F3"),
        new(FileSaveAs, "Save SQL As…", CommandScope.Editor, "Ctrl+Shift+S"),

        new(TableFilterToggle, "Toggle Filter Bar", CommandScope.Grid, "Ctrl+F"),
        new(GridInsertRow, "Insert Row", CommandScope.Grid, "Ctrl+I"),
        new(GridDuplicateRow, "Duplicate Rows", CommandScope.Grid, "Ctrl+D"),
        new(GridDeleteRow, "Delete Rows", CommandScope.Grid, "Delete"),
        new(GridSetValueMenu, "Set Value…", CommandScope.Grid, "Alt+Enter"),
        new(GridEditCell, "Edit Cell", CommandScope.Grid, "F2"),
        new(GridCopy, "Copy Rows", CommandScope.Grid, "Ctrl+C"),
        new(GridCopyCells, "Copy Selected Cells", CommandScope.Grid, "Ctrl+Shift+C"),
        new(GridPaste, "Paste as New Rows", CommandScope.Grid, "Ctrl+V"),
        new(GridPasteCells, "Paste into Cells", CommandScope.Grid, "Ctrl+Shift+V"),
        new(PageNext, "Next Page", CommandScope.Grid, "Ctrl+Right"),
        new(PagePrev, "Previous Page", CommandScope.Grid, "Ctrl+Left"),
        new(GridFirstRow, "First Row", CommandScope.Grid, "Ctrl+Up"),
        new(GridLastRow, "Last Row", CommandScope.Grid, "Ctrl+Down"),

        new(SidebarOpenItem, "Open Item", CommandScope.Sidebar, "Space", "Enter"),
        new(SidebarDropItem, "Delete Item…", CommandScope.Sidebar, "Delete"),
        new(SidebarTruncateItem, "Truncate Item…", CommandScope.Sidebar, "Alt+Delete"),

        new(FilterAddRow, "Add Filter Row", CommandScope.FilterBar, "Ctrl+I"),
        new(FilterRemoveRow, "Remove Filter Row", CommandScope.FilterBar, "Ctrl+Shift+I"),
        new(FilterToggleRow, "Toggle Filter Row", CommandScope.FilterBar, "Ctrl+B"),
        new(FilterApplyAll, "Apply All Filters", CommandScope.FilterBar, "Ctrl+Enter"),

        new(WelcomeSearch, "Search Connections", CommandScope.Welcome, "Ctrl+F"),
    ];

    private static readonly Dictionary<string, CommandDefinition> ById = All.ToDictionary(d => d.Id, StringComparer.Ordinal);

    public static CommandDefinition? Get(string id) => ById.GetValueOrDefault(id);

    public static bool Exists(string id) => ById.ContainsKey(id);
}
