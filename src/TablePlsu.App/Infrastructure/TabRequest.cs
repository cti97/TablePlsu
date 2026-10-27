namespace TablePlsu.App.Infrastructure;

/// <summary>Well-known tab kinds. Features may add their own strings.</summary>
public static class TabKinds
{
    public const string SqlEditor = "sqlEditor";
    public const string Table = "table";
    public const string View = "view";
    public const string MaterializedView = "matview";
    public const string Function = "function";
    public const string Structure = "structure";
    public const string CreateTable = "createTable";
    public const string Settings = "settings";
    public const string Empty = "empty";

    /// <summary>Object tabs take part in preview-tab reuse (a single click in the sidebar replaces a clean one).</summary>
    public static bool IsObjectKind(string kind) => kind is Table or View or MaterializedView;
}

/// <summary>Database object kinds as used by the sidebar, palette and <see cref="TabRequest.ObjectKind"/>.</summary>
public static class ObjectKinds
{
    public const string Schema = "schema";
    public const string Table = "table";
    public const string View = "view";
    public const string MaterializedView = "matview";
    public const string Function = "function";
    public const string Procedure = "procedure";
    public const string Sequence = "sequence";
    public const string Type = "type";
    public const string Database = "database";
}

/// <summary>
/// A request to open a tab (architecture §Extension points). <see cref="Pinned"/> = double-click (not a preview tab);
/// <see cref="InNewTab"/> = "Open in new tab" (never reuse). <see cref="Sql"/> pre-fills an editor;
/// <see cref="Parameter"/> carries anything kind-specific (for example a filter to apply).
/// </summary>
public sealed record TabRequest
{
    public required string Kind { get; init; }
    public string? Schema { get; init; }
    public string? Name { get; init; }
    public string? ObjectKind { get; init; }
    public string? Sql { get; init; }
    public bool Pinned { get; init; }
    public bool InNewTab { get; init; }
    public string? Title { get; init; }
    public object? Parameter { get; init; }

    public static TabRequest SqlEditor(string? sql = null, string? title = null) =>
        new() { Kind = TabKinds.SqlEditor, Sql = sql, Title = title, Pinned = true, InNewTab = true };

    public static TabRequest ForObject(string schema, string name, string objectKind, bool pinned = false, bool inNewTab = false) =>
        new()
        {
            Kind = objectKind switch
            {
                ObjectKinds.View => TabKinds.View,
                ObjectKinds.MaterializedView => TabKinds.MaterializedView,
                ObjectKinds.Function or ObjectKinds.Procedure => TabKinds.Function,
                _ => TabKinds.Table,
            },
            Schema = schema,
            Name = name,
            ObjectKind = objectKind,
            Pinned = pinned,
            InNewTab = inNewTab,
        };
}
