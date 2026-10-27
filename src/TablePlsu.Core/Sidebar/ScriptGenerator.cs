using System.Text;
using TablePlsu.Core.Metadata;
using TablePlsu.Core.Sql;

namespace TablePlsu.Core.Sidebar;

/// <summary>Kinds of database objects the sidebar can script.</summary>
public enum ScriptObjectKind { Table, View, MaterializedView, ForeignTable, Function, Procedure, Aggregate, Sequence, Type, Schema }

/// <summary>One object to script. <see cref="Arguments"/> are the identity arguments of a routine (for DROP FUNCTION).</summary>
public sealed record ScriptObject(ScriptObjectKind Kind, string Schema, string Name, string? Arguments = null)
{
    public string QualifiedName => Kind == ScriptObjectKind.Schema ? SqlIdent.QuoteIfNeeded(Name) : SqlIdent.Qualified(Schema, Name);

    /// <summary>"schema.name(args)" for routines, the qualified name otherwise.</summary>
    public string Target => Kind is ScriptObjectKind.Function or ScriptObjectKind.Procedure or ScriptObjectKind.Aggregate
        ? $"{QualifiedName}({Arguments ?? ""})"
        : QualifiedName;

    public static ScriptObject For(TableInfo t) => new(KindOf(t.Kind), t.Schema, t.Name);

    public static ScriptObject For(FunctionInfo f) => new(
        f.Kind switch { RoutineKind.Procedure => ScriptObjectKind.Procedure, RoutineKind.Aggregate => ScriptObjectKind.Aggregate, _ => ScriptObjectKind.Function },
        f.Schema, f.Name, f.Arguments);

    public static ScriptObject For(SequenceInfo s) => new(ScriptObjectKind.Sequence, s.Schema, s.Name);

    public static ScriptObject For(UserTypeInfo t) => new(ScriptObjectKind.Type, t.Schema, t.Name);

    public static ScriptObjectKind KindOf(RelationKind kind) => kind switch
    {
        RelationKind.View => ScriptObjectKind.View,
        RelationKind.MaterializedView => ScriptObjectKind.MaterializedView,
        RelationKind.ForeignTable => ScriptObjectKind.ForeignTable,
        _ => ScriptObjectKind.Table,
    };
}

/// <summary>
/// SQL text for the sidebar's "Copy Script As" (CREATE / DROP / TRUNCATE / SELECT … LIMIT 100) and for its Truncate… /
/// Delete… actions. Table DDL is generated from the metadata snapshot; views, materialized views and routines need
/// their definition from the server, so <see cref="DefinitionQuery"/> returns a one-row query that yields the DDL text.
/// </summary>
public static class ScriptGenerator
{
    public const int DefaultSelectLimit = 100;

    // ---- SELECT ---------------------------------------------------------------------------------------------------

    public static string Select(string schema, string name, int limit = DefaultSelectLimit) =>
        $"SELECT * FROM {SqlIdent.Qualified(schema, name)} LIMIT {limit};";

    // ---- TRUNCATE -------------------------------------------------------------------------------------------------

    /// <summary>One TRUNCATE for every table (views and other kinds are skipped). Empty string when nothing qualifies.</summary>
    public static string Truncate(IEnumerable<ScriptObject> objects, bool cascade = false, bool restartIdentity = false)
    {
        var targets = objects.Where(o => o.Kind is ScriptObjectKind.Table or ScriptObjectKind.ForeignTable)
            .Select(o => o.QualifiedName).Distinct().ToList();
        if (targets.Count == 0) return "";
        var sb = new StringBuilder("TRUNCATE TABLE ").Append(string.Join(", ", targets));
        if (restartIdentity) sb.Append(" RESTART IDENTITY");
        if (cascade) sb.Append(" CASCADE");
        return sb.Append(';').ToString();
    }

    public static string Truncate(ScriptObject obj, bool cascade = false, bool restartIdentity = false) =>
        Truncate([obj], cascade, restartIdentity);

    // ---- DROP -----------------------------------------------------------------------------------------------------

    public static string DropKeyword(ScriptObjectKind kind) => kind switch
    {
        ScriptObjectKind.Table => "TABLE",
        ScriptObjectKind.View => "VIEW",
        ScriptObjectKind.MaterializedView => "MATERIALIZED VIEW",
        ScriptObjectKind.ForeignTable => "FOREIGN TABLE",
        ScriptObjectKind.Function => "FUNCTION",
        ScriptObjectKind.Procedure => "PROCEDURE",
        ScriptObjectKind.Aggregate => "AGGREGATE",
        ScriptObjectKind.Sequence => "SEQUENCE",
        ScriptObjectKind.Type => "TYPE",
        ScriptObjectKind.Schema => "SCHEMA",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string Drop(ScriptObject obj, bool cascade = false) => Drop([obj], cascade).Single();

    /// <summary>One DROP per object kind (in first-seen order), listing every object of that kind.</summary>
    public static IReadOnlyList<string> Drop(IEnumerable<ScriptObject> objects, bool cascade = false, bool ifExists = false)
    {
        var result = new List<string>();
        foreach (var group in objects.GroupBy(o => o.Kind))
        {
            var targets = group.Select(o => o.Target).Distinct().ToList();
            var sb = new StringBuilder("DROP ").Append(DropKeyword(group.Key)).Append(' ');
            if (ifExists) sb.Append("IF EXISTS ");
            sb.Append(string.Join(", ", targets));
            if (cascade) sb.Append(" CASCADE");
            result.Add(sb.Append(';').ToString());
        }
        return result;
    }

    // ---- CREATE ---------------------------------------------------------------------------------------------------

    /// <summary>True when <see cref="CreateFromMetadata"/> can script the object without asking the server.</summary>
    public static bool CanCreateFromMetadata(ScriptObjectKind kind) =>
        kind is ScriptObjectKind.Table or ScriptObjectKind.ForeignTable or ScriptObjectKind.Sequence or ScriptObjectKind.Type or ScriptObjectKind.Schema;

    /// <summary>
    /// A query returning one text value: the CREATE statement for a view, materialized view or routine (from
    /// <c>pg_get_viewdef</c> / <c>pg_get_functiondef</c>). Null for kinds scripted from metadata (and for aggregates,
    /// which <c>pg_get_functiondef</c> does not support).
    /// </summary>
    public static string? DefinitionQuery(ScriptObject obj, uint oid = 0)
    {
        var regclass = SqlIdent.Literal(SqlIdent.Quote(obj.Schema) + "." + SqlIdent.Quote(obj.Name)) + "::regclass";
        return obj.Kind switch
        {
            ScriptObjectKind.View =>
                $"SELECT 'CREATE OR REPLACE VIEW ' || {SqlIdent.Literal(obj.QualifiedName)} || E' AS\\n' || pg_get_viewdef({regclass}, true)",
            ScriptObjectKind.MaterializedView =>
                $"SELECT 'CREATE MATERIALIZED VIEW ' || {SqlIdent.Literal(obj.QualifiedName)} || E' AS\\n' || pg_get_viewdef({regclass}, true)",
            ScriptObjectKind.Function or ScriptObjectKind.Procedure when oid != 0 =>
                $"SELECT pg_get_functiondef({oid}::oid)",
            ScriptObjectKind.Function or ScriptObjectKind.Procedure =>
                "SELECT pg_get_functiondef(p.oid) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace " +
                $"WHERE n.nspname = {SqlIdent.Literal(obj.Schema)} AND p.proname = {SqlIdent.Literal(obj.Name)} " +
                $"AND pg_get_function_identity_arguments(p.oid) = {SqlIdent.Literal(obj.Arguments ?? "")}",
            _ => null,
        };
    }

    /// <summary>Completes the text returned by <see cref="DefinitionQuery"/> into a statement ending in ';'.</summary>
    public static string FinishDefinition(string definition)
    {
        var t = definition.TrimEnd();
        return t.EndsWith(';') ? t : t + ";";
    }

    /// <summary>CREATE for a table/foreign table/sequence/type/schema found in <paramref name="metadata"/>; null when not found.</summary>
    public static string? CreateFromMetadata(DbMetadata metadata, ScriptObject obj)
    {
        var schema = metadata.FindSchema(obj.Schema);
        switch (obj.Kind)
        {
            case ScriptObjectKind.Schema:
                return $"CREATE SCHEMA {SqlIdent.QuoteIfNeeded(obj.Name)};";
            case ScriptObjectKind.Table or ScriptObjectKind.ForeignTable:
                return schema?.FindTable(obj.Name) is { } t ? CreateTable(t) : null;
            case ScriptObjectKind.Sequence:
                return schema?.Sequences.FirstOrDefault(s => s.Name == obj.Name) is { } s ? CreateSequence(s) : null;
            case ScriptObjectKind.Type:
                return schema?.Types.FirstOrDefault(s => s.Name == obj.Name) is { } ty ? CreateType(ty) : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// CREATE TABLE built from metadata: columns (type, identity/generated, default, NOT NULL), primary key, foreign
    /// keys, then the non-primary indexes and comments as separate statements. Partitioning clauses are not scripted.
    /// </summary>
    public static string CreateTable(TableInfo table)
    {
        var name = SqlIdent.Qualified(table.Schema, table.Name);
        var lines = new List<string>();
        foreach (var c in table.Columns.OrderBy(c => c.Ordinal))
            lines.Add("    " + ColumnDefinition(c));
        if (table.HasPrimaryKey)
        {
            var pk = "PRIMARY KEY (" + string.Join(", ", table.PrimaryKey.Select(SqlIdent.QuoteIfNeeded)) + ")";
            lines.Add("    " + (table.PrimaryKeyName is { Length: > 0 } pkName ? $"CONSTRAINT {SqlIdent.QuoteIfNeeded(pkName)} {pk}" : pk));
        }
        foreach (var fk in table.ForeignKeys)
            lines.Add("    " + ForeignKeyDefinition(fk));

        var sb = new StringBuilder();
        // Serial columns: the owned sequence must exist before the default can reference it.
        foreach (var c in table.Columns.OrderBy(c => c.Ordinal))
            if (SequenceOfDefault(c.Default) is { } seq) sb.Append("CREATE SEQUENCE IF NOT EXISTS ").Append(seq).Append(";\n");
        sb.Append("CREATE TABLE ").Append(name).Append(" (\n");
        sb.Append(string.Join(",\n", lines));
        sb.Append("\n);");

        foreach (var ix in table.Indexes.Where(i => !i.IsPrimary))
        {
            if (string.IsNullOrWhiteSpace(ix.Definition)) continue;
            sb.Append('\n').Append(FinishDefinition(ix.Definition));
        }
        if (!string.IsNullOrEmpty(table.Comment))
            sb.Append('\n').Append($"COMMENT ON TABLE {name} IS {SqlIdent.Literal(table.Comment)};");
        foreach (var c in table.Columns.OrderBy(c => c.Ordinal).Where(c => !string.IsNullOrEmpty(c.Comment)))
            sb.Append('\n').Append($"COMMENT ON COLUMN {name}.{SqlIdent.QuoteIfNeeded(c.Name)} IS {SqlIdent.Literal(c.Comment!)};");
        return sb.ToString();
    }

    private static readonly System.Text.RegularExpressions.Regex NextvalDefault =
        new(@"^nextval\('((?:[^']|'')+)'::regclass\)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The sequence name (as written, possibly qualified/quoted) in a <c>nextval('seq'::regclass)</c> default.</summary>
    public static string? SequenceOfDefault(string? columnDefault)
    {
        if (string.IsNullOrEmpty(columnDefault)) return null;
        var m = NextvalDefault.Match(columnDefault);
        return m.Success ? m.Groups[1].Value.Replace("''", "'") : null;
    }

    public static string ColumnDefinition(ColumnInfo c)
    {
        var sb = new StringBuilder(SqlIdent.QuoteIfNeeded(c.Name)).Append(' ').Append(c.DataType);
        if (c.Identity is "a" or "d")
            sb.Append(c.Identity == "a" ? " GENERATED ALWAYS AS IDENTITY" : " GENERATED BY DEFAULT AS IDENTITY");
        else if (c.IsGenerated && !string.IsNullOrEmpty(c.Default))
            sb.Append(" GENERATED ALWAYS AS (").Append(c.Default).Append(") STORED");
        else if (!string.IsNullOrEmpty(c.Default))
            sb.Append(" DEFAULT ").Append(c.Default);
        if (!c.IsNullable) sb.Append(" NOT NULL");
        return sb.ToString();
    }

    public static string ForeignKeyDefinition(ForeignKeyInfo fk)
    {
        var prefix = string.IsNullOrEmpty(fk.Name) ? "" : $"CONSTRAINT {SqlIdent.QuoteIfNeeded(fk.Name)} ";
        if (!string.IsNullOrWhiteSpace(fk.Definition)) return prefix + fk.Definition;
        var sb = new StringBuilder(prefix)
            .Append("FOREIGN KEY (").Append(string.Join(", ", fk.Columns.Select(SqlIdent.QuoteIfNeeded))).Append(") REFERENCES ")
            .Append(SqlIdent.Qualified(fk.RefSchema, fk.RefTable))
            .Append(" (").Append(string.Join(", ", fk.RefColumns.Select(SqlIdent.QuoteIfNeeded))).Append(')');
        if (fk.OnUpdate != "NO ACTION") sb.Append(" ON UPDATE ").Append(fk.OnUpdate);
        if (fk.OnDelete != "NO ACTION") sb.Append(" ON DELETE ").Append(fk.OnDelete);
        return sb.ToString();
    }

    public static string CreateSequence(SequenceInfo s)
    {
        var sb = new StringBuilder("CREATE SEQUENCE ").Append(SqlIdent.Qualified(s.Schema, s.Name));
        if (!string.IsNullOrEmpty(s.DataType)) sb.Append(" AS ").Append(s.DataType);
        if (!string.IsNullOrEmpty(s.OwnedBy) && s.OwnedBy.Split('.') is [var table, var column])
            sb.Append(" OWNED BY ").Append(SqlIdent.Qualified(s.Schema, table)).Append('.').Append(SqlIdent.QuoteIfNeeded(column));
        return sb.Append(';').ToString();
    }

    public static string CreateType(UserTypeInfo t)
    {
        var name = SqlIdent.Qualified(t.Schema, t.Name);
        var sql = t.Kind switch
        {
            UserTypeKind.Enum => $"CREATE TYPE {name} AS ENUM ({string.Join(", ", t.EnumLabels.Select(SqlIdent.Literal))});",
            UserTypeKind.Domain => $"CREATE DOMAIN {name} AS {t.BaseType ?? "text"};",
            _ => $"CREATE TYPE {name} AS (\n{string.Join(",\n", t.Attributes.OrderBy(a => a.Ordinal).Select(a => "    " + SqlIdent.QuoteIfNeeded(a.Name) + " " + a.DataType))}\n);",
        };
        if (!string.IsNullOrEmpty(t.Comment))
            sql += $"\nCOMMENT ON {(t.Kind == UserTypeKind.Domain ? "DOMAIN" : "TYPE")} {name} IS {SqlIdent.Literal(t.Comment)};";
        return sql;
    }
}
