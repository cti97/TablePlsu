using System.Text;
using TablePlsu.Core.Sql;

namespace TablePlsu.Core.Data;

/// <summary>The table staged changes apply to: column names in grid order and the primary-key column indexes.</summary>
public sealed record ChangeTarget(string Schema, string Table, IReadOnlyList<string> Columns, IReadOnlyList<int> KeyColumns);

/// <summary>
/// Turns <see cref="PendingChanges"/> into statements (spec §8): UPDATE … SET … WHERE pk = …, DELETE … WHERE pk = …,
/// INSERT … VALUES (…), in that order. Values are parameters (<see cref="SqlCommandSpec.Sql"/>) with a literal-rendered
/// twin for the Preview window (<see cref="SqlCommandSpec.LiteralSql"/>). UPDATE and DELETE expect exactly one row.
/// With <c>returnRows</c>, UPDATE and INSERT end with <c>RETURNING</c> the target's columns, so the caller gets the rows
/// exactly as the transaction wrote them (defaults, triggers, server-side casts) instead of the text that was typed.
/// </summary>
public static class ChangeSqlGenerator
{
    public static IReadOnlyList<SqlCommandSpec> Generate(ChangeTarget target, PendingChanges changes, bool returnRows = false)
    {
        var result = new List<SqlCommandSpec>();
        var table = SqlIdent.Qualified(target.Schema, target.Table);
        var returning = returnRows ? " RETURNING " + string.Join(", ", target.Columns.Select(SqlIdent.QuoteIfNeeded)) : null;

        foreach (var u in changes.Updates)
        {
            var b = new SqlBuilder().Append("UPDATE ").Append(table).Append(" SET ");
            var first = true;
            foreach (var (col, value) in u.Cells)
            {
                if (!first) b.Append(", ");
                first = false;
                b.AppendIdent(target.Columns[col]).Append(" = ").AppendValue(value);
            }
            AppendKeyWhere(b, target, u.Key);
            if (returning is not null) b.Append(returning);
            result.Add(b.Build() with { ExpectedRows = 1, ReturnsRows = returning is not null });
        }

        foreach (var d in changes.Deletes)
        {
            var b = new SqlBuilder().Append("DELETE FROM ").Append(table);
            AppendKeyWhere(b, target, d.Key);
            result.Add(b.Build() with { ExpectedRows = 1 });
        }

        foreach (var i in changes.Inserts)
        {
            var b = new SqlBuilder().Append("INSERT INTO ").Append(table);
            var cols = i.SetColumns.Order().ToList();
            if (cols.Count == 0)
            {
                b.Append(" DEFAULT VALUES");
            }
            else
            {
                b.Append(" (").Append(string.Join(", ", cols.Select(c => SqlIdent.QuoteIfNeeded(target.Columns[c])))).Append(") VALUES (");
                for (var k = 0; k < cols.Count; k++)
                {
                    if (k > 0) b.Append(", ");
                    b.AppendValue(i.Row[cols[k]]);
                }
                b.Append(")");
            }
            if (returning is not null) b.Append(returning);
            result.Add(b.Build() with { ReturnsRows = returning is not null });
        }
        return result;
    }

    private static void AppendKeyWhere(SqlBuilder b, ChangeTarget target, RowKey key)
    {
        b.Append(" WHERE ");
        for (var k = 0; k < target.KeyColumns.Count; k++)
        {
            if (k > 0) b.Append(" AND ");
            b.AppendIdent(target.Columns[target.KeyColumns[k]]);
            var v = key.Values[k];
            if (v is null) b.Append(" IS NULL");
            else b.Append(" = ").AppendValue(v);
        }
    }

    /// <summary>The literal statements joined as a script (one per line, each ending with ';').</summary>
    public static string ToScript(IEnumerable<SqlCommandSpec> statements)
    {
        var sb = new StringBuilder();
        foreach (var s in statements) sb.Append(s.LiteralSql).Append(";\n");
        return sb.ToString();
    }
}
