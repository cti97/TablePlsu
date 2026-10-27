using System.Text;
using TablePlsu.Core.Db;

namespace TablePlsu.App.Features.Results;

/// <summary>Builds the TSV text for grid copy (pure, unit-tested).</summary>
public static class GridCopy
{
    /// <param name="columns">All result columns.</param>
    /// <param name="columnOrder">ResultColumn indexes to output, in output order.</param>
    /// <param name="rows">Rows in output order.</param>
    /// <param name="include">Optional filter: false leaves that cell empty (Ctrl+Shift+C with a ragged selection).</param>
    /// <param name="withHeader">Prepend a row of column names.</param>
    /// <param name="nullText">Text for NULL values.</param>
    public static string Build(
        IReadOnlyList<ResultColumn> columns,
        IReadOnlyList<int> columnOrder,
        IEnumerable<object?[]> rows,
        Func<object?[], int, bool>? include,
        bool withHeader,
        CellFormatter formatter,
        string nullText = "")
    {
        var sb = new StringBuilder();
        var fields = new string[columnOrder.Count];
        var first = true;
        if (withHeader)
        {
            for (var i = 0; i < columnOrder.Count; i++) fields[i] = columnOrder[i] < columns.Count ? columns[columnOrder[i]].Name : "";
            TsvFormatter.AppendRow(sb, fields);
            first = false;
        }
        foreach (var row in rows)
        {
            for (var i = 0; i < columnOrder.Count; i++)
            {
                var c = columnOrder[i];
                if (c >= row.Length || (include is not null && !include(row, c))) { fields[i] = ""; continue; }
                fields[i] = formatter.Full(row[c], nullText, c < columns.Count ? columns[c].DataTypeName : null);
            }
            if (!first) sb.Append("\r\n");
            first = false;
            TsvFormatter.AppendRow(sb, fields);
        }
        return sb.ToString();
    }
}
