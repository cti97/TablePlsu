using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Metadata;
using TablePlsu.Core.Search;
using TablePlsu.Core.Settings;
using TablePlsu.Core.Sidebar;

namespace TablePlsu.App.Features.Sidebar;

public enum SidebarNodeKind { Schema, Section, Item, Placeholder }

public enum SidebarSection { Tables, Functions, Sequences, Types }

/// <summary>
/// One row of the flattened Items tree (the ListBox shows rows with indentation; expansion is tracked by
/// <see cref="Key"/> in the view-model, so rebuilding from a new metadata snapshot keeps expansion and selection).
/// </summary>
public sealed class SidebarNode
{
    public required SidebarNodeKind Kind { get; init; }
    /// <summary>Stable path: "public", "public/Tables", "public/Tables/orders", "public/Functions/f(x integer)".</summary>
    public required string Key { get; init; }
    public required string Text { get; init; }
    public string Schema { get; init; } = "";
    public int Level { get; init; }
    public string? Detail { get; init; }
    public string Icon { get; init; } = "";
    public string? ToolTip { get; init; }
    public bool IsExpandable { get; init; }
    public bool IsExpanded { get; init; }
    public SidebarSection? Section { get; init; }
    /// <summary>One of <see cref="ObjectKinds"/> for items and schemas.</summary>
    public string? ObjectKind { get; init; }
    public TableInfo? Table { get; init; }
    public FunctionInfo? Function { get; init; }
    public SequenceInfo? Sequence { get; init; }
    public UserTypeInfo? Type { get; init; }

    /// <summary>Left indent in pixels.</summary>
    public double Indent => Level * 14;

    public string Chevron => !IsExpandable ? "" : IsExpanded ? "" : "";

    public bool IsItem => Kind == SidebarNodeKind.Item;

    public bool IsHeader => Kind is SidebarNodeKind.Schema or SidebarNodeKind.Section;

    /// <summary>Tables, views and materialized views open as table/view tabs.</summary>
    public bool IsRelation => Table is not null;

    public SidebarItemRef? ToRef() => Kind switch
    {
        SidebarNodeKind.Schema => new SidebarItemRef(Schema, Schema, ObjectKinds.Schema),
        SidebarNodeKind.Item when ObjectKind is not null => new SidebarItemRef(Schema, Function?.Name ?? Text, ObjectKind) { Signature = Function?.Arguments },
        _ => null,
    };

    public override string ToString() => Key;

    // Segoe Fluent / MDL2 glyphs.
    public const string IconSchema = "";
    public const string IconTable = "";
    public const string IconView = "";
    public const string IconMatView = "";
    public const string IconForeign = "";
    public const string IconFunction = "";
    public const string IconProcedure = "";
    public const string IconSequence = "";
    public const string IconType = "";
}

/// <summary>Inputs for <see cref="SidebarTree.Build"/>.</summary>
public sealed record SidebarTreeOptions
{
    /// <summary>The search box text; empty shows everything.</summary>
    public string Filter { get; init; } = "";
    public SidebarMatching Matching { get; init; } = SidebarMatching.Fuzzy;
    /// <summary>Schemas to show; null = all.</summary>
    public IReadOnlySet<string>? VisibleSchemas { get; init; }
    /// <summary>Expanded node keys (schemas and sections).</summary>
    public IReadOnlySet<string> Expanded { get; init; } = new HashSet<string>();
    /// <summary>Text for an expanded schema whose contents are not loaded yet.</summary>
    public string NotLoadedText { get; init; } = "Loading…";
}

/// <summary>Builds the flattened Items tree from a metadata snapshot (pure; unit-tested).</summary>
public static class SidebarTree
{
    public static string SectionKey(string schema, SidebarSection section) => schema + "/" + section;

    public static string SectionTitle(SidebarSection s) => s switch
    {
        SidebarSection.Tables => "Tables",
        SidebarSection.Functions => "Functions",
        SidebarSection.Sequences => "Sequences",
        _ => "Types",
    };

    public static bool Matches(string filter, string text, SidebarMatching matching) =>
        filter.Length == 0 || (matching == SidebarMatching.Contains ? FuzzyMatcher.MatchContains(filter, text) : FuzzyMatcher.Match(filter, text)) is not null;

    public static List<SidebarNode> Build(DbMetadata metadata, SidebarTreeOptions options)
    {
        var result = new List<SidebarNode>();
        var filter = options.Filter.Trim();
        var filtering = filter.Length > 0;
        foreach (var schema in metadata.Schemas)
        {
            if (options.VisibleSchemas is { } visible && !visible.Contains(schema.Name)) continue;

            var tables = schema.Tables.Where(t => Matches(filter, t.Name, options.Matching)).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var functions = schema.Functions.Where(f => Matches(filter, f.Name, options.Matching))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Arguments, StringComparer.Ordinal).ToList();
            var sequences = schema.Sequences.Where(s => Matches(filter, s.Name, options.Matching)).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var types = schema.Types.Where(t => Matches(filter, t.Name, options.Matching)).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var any = tables.Count + functions.Count + sequences.Count + types.Count > 0;
            if (filtering && !any) continue;

            var schemaExpanded = filtering || options.Expanded.Contains(schema.Name);
            result.Add(new SidebarNode
            {
                Kind = SidebarNodeKind.Schema,
                Key = schema.Name,
                Text = schema.Name,
                Schema = schema.Name,
                Icon = SidebarNode.IconSchema,
                IsExpandable = true,
                IsExpanded = schemaExpanded,
                ObjectKind = ObjectKinds.Schema,
                ToolTip = schema.Comment,
            });
            if (!schemaExpanded) continue;

            if (!schema.IsLoaded)
            {
                result.Add(new SidebarNode
                {
                    Kind = SidebarNodeKind.Placeholder, Key = schema.Name + "/…", Text = options.NotLoadedText, Schema = schema.Name, Level = 1,
                });
                continue;
            }

            AddSection(result, schema.Name, SidebarSection.Tables, tables, filtering, options, alwaysShow: !filtering, t => TableNode(t));
            AddSection(result, schema.Name, SidebarSection.Functions, functions, filtering, options, alwaysShow: false, f => FunctionNode(f));
            AddSection(result, schema.Name, SidebarSection.Sequences, sequences, filtering, options, alwaysShow: false, s => new SidebarNode
            {
                Kind = SidebarNodeKind.Item, Key = SectionKey(s.Schema, SidebarSection.Sequences) + "/" + s.Name, Text = s.Name, Schema = s.Schema,
                Level = 2, Icon = SidebarNode.IconSequence, ObjectKind = ObjectKinds.Sequence, Sequence = s, Detail = s.DataType,
                ToolTip = s.OwnedBy is null ? null : "Owned by " + s.OwnedBy,
            });
            AddSection(result, schema.Name, SidebarSection.Types, types, filtering, options, alwaysShow: false, t => new SidebarNode
            {
                Kind = SidebarNodeKind.Item, Key = SectionKey(t.Schema, SidebarSection.Types) + "/" + t.Name, Text = t.Name, Schema = t.Schema,
                Level = 2, Icon = SidebarNode.IconType, ObjectKind = ObjectKinds.Type, Type = t, Detail = t.Kind.ToString().ToLowerInvariant(),
                ToolTip = t.Comment,
            });
        }
        return result;
    }

    private static void AddSection<T>(List<SidebarNode> result, string schema, SidebarSection section, List<T> items, bool filtering,
        SidebarTreeOptions options, bool alwaysShow, Func<T, SidebarNode> create)
    {
        if (items.Count == 0 && !alwaysShow) return;
        var key = SectionKey(schema, section);
        var expanded = filtering || options.Expanded.Contains(key);
        result.Add(new SidebarNode
        {
            Kind = SidebarNodeKind.Section,
            Key = key,
            Text = SectionTitle(section),
            Detail = items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Schema = schema,
            Level = 1,
            IsExpandable = true,
            IsExpanded = expanded,
            Section = section,
        });
        if (!expanded) return;
        foreach (var item in items) result.Add(create(item));
    }

    public static SidebarNode TableNode(TableInfo t, int level = 2) => new()
    {
        Kind = SidebarNodeKind.Item,
        Key = SectionKey(t.Schema, SidebarSection.Tables) + "/" + t.Name,
        Text = t.Name,
        Schema = t.Schema,
        Level = level,
        Icon = t.Kind switch
        {
            RelationKind.View => SidebarNode.IconView,
            RelationKind.MaterializedView => SidebarNode.IconMatView,
            RelationKind.ForeignTable => SidebarNode.IconForeign,
            _ => SidebarNode.IconTable,
        },
        ObjectKind = ObjectKindOf(t.Kind),
        Table = t,
        ToolTip = KindText(t.Kind) + (string.IsNullOrEmpty(t.Comment) ? "" : " — " + t.Comment),
    };

    public static SidebarNode FunctionNode(FunctionInfo f, int level = 2) => new()
    {
        Kind = SidebarNodeKind.Item,
        Key = SectionKey(f.Schema, SidebarSection.Functions) + "/" + f.Signature,
        Text = f.Name,
        Detail = "(" + f.Arguments + ")",
        Schema = f.Schema,
        Level = level,
        Icon = f.Kind == RoutineKind.Procedure ? SidebarNode.IconProcedure : SidebarNode.IconFunction,
        ObjectKind = f.Kind == RoutineKind.Procedure ? ObjectKinds.Procedure : ObjectKinds.Function,
        Function = f,
        ToolTip = f.Signature + (f.ReturnType is null ? "" : " → " + f.ReturnType) + (string.IsNullOrEmpty(f.Comment) ? "" : "\n" + f.Comment),
    };

    public static string ObjectKindOf(RelationKind kind) => kind switch
    {
        RelationKind.View => ObjectKinds.View,
        RelationKind.MaterializedView => ObjectKinds.MaterializedView,
        _ => ObjectKinds.Table,
    };

    public static string KindText(RelationKind kind) => kind switch
    {
        RelationKind.View => "View",
        RelationKind.MaterializedView => "Materialized view",
        RelationKind.ForeignTable => "Foreign table",
        RelationKind.PartitionedTable => "Partitioned table",
        _ => "Table",
    };

    /// <summary>Maps a sidebar ref to a <see cref="ScriptObject"/>, resolving relation/routine kinds from metadata.</summary>
    public static ScriptObject? ToScriptObject(DbMetadata md, SidebarItemRef r)
    {
        switch (r.ObjectKind)
        {
            case ObjectKinds.Schema:
                return new ScriptObject(ScriptObjectKind.Schema, r.Schema, r.Name);
            case ObjectKinds.Table or ObjectKinds.View or ObjectKinds.MaterializedView:
                if (md.FindSchema(r.Schema)?.FindTable(r.Name) is { } t) return ScriptObject.For(t);
                return new ScriptObject(r.ObjectKind switch
                {
                    ObjectKinds.View => ScriptObjectKind.View,
                    ObjectKinds.MaterializedView => ScriptObjectKind.MaterializedView,
                    _ => ScriptObjectKind.Table,
                }, r.Schema, r.Name);
            case ObjectKinds.Function or ObjectKinds.Procedure:
                if (FindFunction(md, r) is { } f) return ScriptObject.For(f);
                return new ScriptObject(r.ObjectKind == ObjectKinds.Procedure ? ScriptObjectKind.Procedure : ScriptObjectKind.Function, r.Schema, r.Name, r.Signature);
            case ObjectKinds.Sequence:
                return new ScriptObject(ScriptObjectKind.Sequence, r.Schema, r.Name);
            case ObjectKinds.Type:
                return new ScriptObject(ScriptObjectKind.Type, r.Schema, r.Name);
            default:
                return null;
        }
    }

    public static FunctionInfo? FindFunction(DbMetadata md, SidebarItemRef r) =>
        md.FindSchema(r.Schema)?.FindFunctions(r.Name).FirstOrDefault(f => r.Signature is null || f.Arguments == r.Signature);
}
