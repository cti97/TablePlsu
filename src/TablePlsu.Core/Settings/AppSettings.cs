using System.Text.Json.Serialization;
using TablePlsu.Core.Connections;

namespace TablePlsu.Core.Settings;

/// <summary>
/// Typed view of settings.json (spec §13). Property names map to camelCase JSON keys, e.g. <c>Editor.Autocomplete.Tables</c>
/// is <c>editor.autocomplete.tables</c>. Initial values mirror the embedded defaults.json so a bare instance is usable.
/// </summary>
public sealed record AppSettings
{
    public GeneralSettings General { get; init; } = new();
    public EditorSettings Editor { get; init; } = new();
    public GridSettings Grid { get; init; } = new();
    public SidebarSettings Sidebar { get; init; } = new();
    public TableSettings Table { get; init; } = new();
    public ConnectionsSettings Connections { get; init; } = new();
    public SecuritySettings Security { get; init; } = new();
    public HistorySettings History { get; init; } = new();
    public ThemeSettings Theme { get; init; } = new();

    /// <summary>Command id → bindings. A JSON string or array of strings; <c>null</c> unbinds the command.</summary>
    [JsonConverter(typeof(KeymapJsonConverter))]
    public Dictionary<string, string[]?> Keymap { get; init; } = [];

    /// <summary>Tag name → colour (#RRGGBB).</summary>
    public Dictionary<string, string> Tags { get; init; } = [];

    public ToolbarSettings Toolbar { get; init; } = new();

    /// <summary>Clamps numeric values into their documented ranges.</summary>
    public AppSettings Normalize() => this with
    {
        Editor = Editor with
        {
            FontSize = Math.Clamp(Editor.FontSize, 9, 100),
            LineHeight = Math.Clamp(Editor.LineHeight, 1.0, 5.0),
            TabSize = Math.Clamp(Editor.TabSize, 1, 16),
            AutoSaveDebounceMs = Math.Clamp(Editor.AutoSaveDebounceMs, 50, 10_000),
            DefaultRowLimit = Math.Max(0, Editor.DefaultRowLimit),
        },
        Grid = Grid with { FontSize = Math.Clamp(Grid.FontSize, 6, 100), RowPadding = Math.Clamp(Grid.RowPadding, 0, 40) },
        Sidebar = Sidebar with { FontSize = Math.Clamp(Sidebar.FontSize, 6, 100), RowPadding = Math.Clamp(Sidebar.RowPadding, 0, 40) },
        Table = Table with { PageSize = Math.Clamp(Table.PageSize, 1, 1_000_000), EstimateAboveRows = Math.Max(0, Table.EstimateAboveRows) },
        Connections = Connections with { CommandTimeoutSeconds = Math.Max(0, Connections.CommandTimeoutSeconds) },
        History = History with { MaxEntries = Math.Max(1, History.MaxEntries) },
    };
}

public sealed record GeneralSettings
{
    public bool ReopenWorkspacesOnStartup { get; init; } = true;
    public NewTabOpens NewTabOpens { get; init; } = NewTabOpens.SqlEditor;
}

public sealed record EditorSettings
{
    public string FontFamily { get; init; } = "Cascadia Mono";
    public double FontSize { get; init; } = 12;
    public double LineHeight { get; init; } = 1.2;
    public int TabSize { get; init; } = 4;
    public bool InsertSpaces { get; init; } = true;
    public bool WordWrap { get; init; } = true;
    public bool ShowInvisibles { get; init; }
    public bool HighlightCurrentQuery { get; init; } = true;
    public CompleteKey CompleteKey { get; init; } = CompleteKey.Both;
    public AutocompleteSettings Autocomplete { get; init; } = new();
    public AutoSaveTabs AutoSaveTabs { get; init; } = AutoSaveTabs.All;
    public int AutoSaveDebounceMs { get; init; } = 500;
    public bool ReturnOnError { get; init; }
    public ResultViewMode ResultViewMode { get; init; } = ResultViewMode.Split;
    /// <summary>0 = no limit.</summary>
    public int DefaultRowLimit { get; init; } = 1000;
}

public sealed record AutocompleteSettings
{
    public bool Tables { get; init; } = true;
    public bool Functions { get; init; } = true;
    public bool Keywords { get; init; } = true;
    public bool Disabled { get; init; }
    public bool AddSpace { get; init; }
    public bool PrefixSchema { get; init; } = true;
    public bool UppercaseKeywords { get; init; } = true;
    public bool AutoClosePairs { get; init; } = true;
}

public sealed record GridSettings
{
    public string FontFamily { get; init; } = "Segoe UI";
    public double FontSize { get; init; } = 12;
    public double RowPadding { get; init; } = 2;
    public string NullDisplay { get; init; } = "NULL";
    /// <summary>.NET format string for timestamps.</summary>
    public string DateFormat { get; init; } = "yyyy-MM-dd HH:mm:ss";
}

public sealed record SidebarSettings
{
    public string FontFamily { get; init; } = "Segoe UI";
    public double FontSize { get; init; } = 12;
    public double RowPadding { get; init; } = 2;
    public bool LazyLoadSchemas { get; init; } = true;
    public bool LoadSystemSchemas { get; init; }
    public SidebarMatching Matching { get; init; } = SidebarMatching.Fuzzy;
}

public sealed record TableSettings
{
    public int PageSize { get; init; } = 300;
    public long EstimateAboveRows { get; init; } = 500_000;
}

public sealed record ConnectionsSettings
{
    public bool KeepAlive { get; init; } = true;
    /// <summary>0 = no timeout.</summary>
    public int CommandTimeoutSeconds { get; init; }
}

public sealed record SecuritySettings
{
    public SafeModeLevel DefaultSafeMode { get; init; } = SafeModeLevel.Silent;
}

public sealed record HistorySettings
{
    public bool Enabled { get; init; } = true;
    public int MaxEntries { get; init; } = 10_000;
}

public sealed record ThemeSettings
{
    public ThemeMode Mode { get; init; } = ThemeMode.Auto;
    public string Light { get; init; } = "Light";
    public string Dark { get; init; } = "Dark";
    /// <summary>Colour key (e.g. "editor.keyword") → colour, applied on top of the active theme.</summary>
    public Dictionary<string, string> Overrides { get; init; } = [];
}

public sealed record ToolbarSettings
{
    public IReadOnlyList<string> Items { get; init; } = [];
}

[JsonConverter(typeof(CamelCaseEnumConverter<NewTabOpens>))]
public enum NewTabOpens { SqlEditor, Empty }

[JsonConverter(typeof(CamelCaseEnumConverter<CompleteKey>))]
public enum CompleteKey { Both, Enter, Tab }

[JsonConverter(typeof(CamelCaseEnumConverter<AutoSaveTabs>))]
public enum AutoSaveTabs { All, First, None }

[JsonConverter(typeof(CamelCaseEnumConverter<ResultViewMode>))]
public enum ResultViewMode { Split, Append }

[JsonConverter(typeof(CamelCaseEnumConverter<SidebarMatching>))]
public enum SidebarMatching { Fuzzy, Contains }

[JsonConverter(typeof(CamelCaseEnumConverter<ThemeMode>))]
public enum ThemeMode { Auto, Light, Dark }
