using System.Globalization;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using TablePlsu.Core.Sql;

namespace TablePlsu.Core.Data;

/// <summary>
/// A generated statement: <see cref="Sql"/> uses positional placeholders (<c>$1</c>, <c>$2</c>…) bound to
/// <see cref="Parameters"/>; <see cref="LiteralSql"/> is the same statement with the values rendered as SQL literals
/// (for previews, the filter "SQL" button and history). Values are never concatenated into <see cref="Sql"/>.
/// </summary>
public sealed record SqlCommandSpec(string Sql, IReadOnlyList<object?> Parameters, string LiteralSql)
{
    /// <summary>For UPDATE/DELETE by primary key: the number of rows the statement must affect (null = any).</summary>
    public int? ExpectedRows { get; init; }

    /// <summary>For a table page SELECT: how many trailing result columns are the key columns' text forms (not shown).</summary>
    public int KeyTextColumns { get; init; }

    public override string ToString() => LiteralSql;

    /// <summary>
    /// Creates the Npgsql command. Every parameter is sent as text with the "unknown" type so the server infers the
    /// column type (an integer column compares with '42', a jsonb column parses '{"a":1}'…), exactly like a literal.
    /// </summary>
    public NpgsqlCommand CreateCommand(NpgsqlConnection connection, NpgsqlTransaction? transaction = null)
    {
        var cmd = new NpgsqlCommand(Sql, connection, transaction);
        foreach (var p in Parameters)
        {
            cmd.Parameters.Add(new NpgsqlParameter
            {
                Value = p is null ? DBNull.Value : PgText.Format(p),
                NpgsqlDbType = NpgsqlDbType.Unknown,
            });
        }
        return cmd;
    }
}

/// <summary>Marker value: the column's DEFAULT (shown as "DEFAULT" in the grid, emitted as the DEFAULT keyword).</summary>
public sealed class SqlDefault
{
    public static readonly SqlDefault Instance = new();

    private SqlDefault()
    {
    }

    public override string ToString() => "DEFAULT";
}

/// <summary>A raw SQL expression value such as <c>now()</c> (emitted verbatim, never as a parameter).</summary>
public sealed record SqlExpression(string Text)
{
    public static SqlExpression Now { get; } = new("now()");

    public override string ToString() => Text;
}

/// <summary>Builds the parameterized SQL and its literal twin in one pass.</summary>
public sealed class SqlBuilder
{
    private readonly StringBuilder _sql = new();
    private readonly StringBuilder _literal = new();
    private readonly List<object?> _params = [];

    public SqlBuilder Append(string text)
    {
        _sql.Append(text);
        _literal.Append(text);
        return this;
    }

    public SqlBuilder AppendIdent(string identifier) => Append(SqlIdent.QuoteIfNeeded(identifier));

    /// <summary>Appends a value: a placeholder in the SQL, a literal in the preview. DEFAULT / expressions are inlined.</summary>
    public SqlBuilder AppendValue(object? value)
    {
        switch (value)
        {
            case SqlDefault:
                return Append("DEFAULT");
            case SqlExpression e:
                return Append(e.Text);
            case null:
                return Append("NULL");
        }
        _params.Add(value);
        _sql.Append('$').Append(_params.Count.ToString(CultureInfo.InvariantCulture));
        _literal.Append(SqlLiteral.Render(value));
        return this;
    }

    public int Length => _sql.Length;

    public SqlCommandSpec Build() => new(_sql.ToString(), _params.ToArray(), _literal.ToString());
}

/// <summary>Renders values as PostgreSQL literals for previews (never used for execution).</summary>
public static class SqlLiteral
{
    public static string Render(object? value) => value switch
    {
        null or DBNull => "NULL",
        SqlDefault => "DEFAULT",
        SqlExpression e => e.Text,
        bool b => b ? "TRUE" : "FALSE",
        sbyte or byte or short or ushort or int or uint or long or ulong or decimal => PgText.Format(value),
        double d when double.IsFinite(d) => PgText.Format(value),
        float f when float.IsFinite(f) => PgText.Format(value),
        _ => SqlIdent.Literal(PgText.Format(value)),
    };
}

/// <summary>The PostgreSQL text input form of a CLR value (what a parameter is sent as).</summary>
public static class PgText
{
    public static string Format(object value) => value switch
    {
        string s => s,
        bool b => b ? "true" : "false",
        DateTime dt => dt.Kind == DateTimeKind.Utc
            ? dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture) + "+00"
            : dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFzzz", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture),
        TimeSpan ts => FormatTimeSpan(ts),
        byte[] bytes => "\\x" + Convert.ToHexStringLower(bytes),
        double d when double.IsPositiveInfinity(d) => "Infinity",
        double d when double.IsNegativeInfinity(d) => "-Infinity",
        double d when double.IsNaN(d) => "NaN",
        float f when float.IsPositiveInfinity(f) => "Infinity",
        float f when float.IsNegativeInfinity(f) => "-Infinity",
        float f when float.IsNaN(f) => "NaN",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// <summary>Valid for both <c>time</c> (0 ≤ value &lt; 1 day) and <c>interval</c>.</summary>
    private static string FormatTimeSpan(TimeSpan ts)
    {
        if (ts >= TimeSpan.Zero && ts < TimeSpan.FromDays(1))
            return ts.ToString(@"hh\:mm\:ss\.FFFFFF", CultureInfo.InvariantCulture).TrimEnd('.');
        return (ts.Ticks / 10).ToString(CultureInfo.InvariantCulture) + " microseconds";
    }
}
