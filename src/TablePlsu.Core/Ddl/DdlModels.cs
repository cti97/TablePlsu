namespace TablePlsu.Core.Ddl;

/// <summary>
/// A table constraint as stored in <c>pg_constraint</c>. <see cref="Type"/> is <c>contype</c>: 'p' primary key,
/// 'u' unique, 'c' check, 'f' foreign key, 'x' exclusion. <see cref="Definition"/> is <c>pg_get_constraintdef</c>.
/// </summary>
public sealed record ConstraintDef(string Name, char Type, string Definition);

/// <summary>Catalog details the metadata snapshot does not carry, loaded on demand by <see cref="DdlLoader"/>.</summary>
public sealed class TableDdlExtras
{
    public IReadOnlyList<ConstraintDef> Constraints { get; init; } = [];

    /// <summary>Index name → comment (only indexes that have one).</summary>
    public IReadOnlyDictionary<string, string> IndexComments { get; init; } = new Dictionary<string, string>();

    public static TableDdlExtras Empty { get; } = new();
}

/// <summary>A view or materialized view definition (<c>pg_get_viewdef</c>).</summary>
public sealed record ViewDefinition(string Schema, string Name, string Definition, bool IsMaterialized);

/// <summary>The new-table editor model (spec §7 "Create table").</summary>
public sealed class NewTableModel
{
    public string Schema { get; set; } = "public";
    public string Name { get; set; } = "";
    public List<NewColumn> Columns { get; init; } = [];

    /// <summary>Problems that stop SQL generation (empty when valid). Messages are user-facing.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name)) errors.Add("Table name is required.");
        if (string.IsNullOrWhiteSpace(Schema)) errors.Add("Schema is required.");
        if (Columns.Count == 0) errors.Add("Add at least one column.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Columns.Count; i++)
        {
            var c = Columns[i];
            var label = string.IsNullOrWhiteSpace(c.Name) ? $"Column {i + 1}" : $"Column \"{c.Name}\"";
            if (string.IsNullOrWhiteSpace(c.Name)) errors.Add($"Column {i + 1} has no name.");
            else if (!seen.Add(c.Name.Trim())) errors.Add($"Duplicate column name \"{c.Name}\".");
            if (string.IsNullOrWhiteSpace(c.DataType)) errors.Add($"{label} has no data type.");
        }
        return errors;
    }
}

public sealed class NewColumn
{
    public string Name { get; set; } = "";
    public string DataType { get; set; } = "";
    public bool IsNullable { get; set; } = true;

    /// <summary>A <see cref="DefaultChoices"/> entry or a free SQL expression; null/empty/"NULL" = no default.</summary>
    public string? Default { get; set; }

    public string? Comment { get; set; }
    public bool IsPrimaryKey { get; set; }
}

/// <summary>The default-value dropdown (spec §7) and the common type list for the type dropdown.</summary>
public static class DefaultChoices
{
    public const string Null = "NULL";
    public const string Empty = "EMPTY";
    public const string CurrentTimestamp = "CURRENT_TIMESTAMP";
    public const string Now = "now()";
    public const string GenRandomUuid = "gen_random_uuid()";
    public const string NextVal = "nextval(...)";

    public static IReadOnlyList<string> All { get; } = [Null, Empty, CurrentTimestamp, Now, GenRandomUuid, NextVal];

    public static IReadOnlyList<string> CommonTypes { get; } =
    [
        "integer", "bigint", "smallint", "serial", "bigserial", "numeric", "numeric(12,2)", "real", "double precision",
        "text", "varchar(255)", "char(1)", "boolean", "date", "time", "timestamp", "timestamptz", "interval", "uuid",
        "json", "jsonb", "bytea", "inet", "cidr", "text[]", "integer[]",
    ];

    /// <summary>The "nextval(...)" placeholder: the generator creates an owned sequence named <c>{table}_{column}_seq</c>.</summary>
    public static bool IsNextVal(string? choice) =>
        choice is not null && choice.Trim() is "nextval(...)" or "nextval(…)" or "nextval()";

    /// <summary>The DEFAULT expression for a dropdown choice or free text; null when there is no default.</summary>
    public static string? ToExpression(string? choice)
    {
        if (string.IsNullOrWhiteSpace(choice)) return null;
        var t = choice.Trim();
        if (string.Equals(t, Null, StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(t, Empty, StringComparison.OrdinalIgnoreCase)) return "''";
        return t;
    }
}
