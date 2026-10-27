using TablePlsu.Core.Sql;

namespace TablePlsu.Core.Db;

/// <summary>A result-set column. <see cref="DataTypeName"/> is the PostgreSQL type name ("bigint", "text", "jsonb"…).</summary>
public sealed record ResultColumn(int Ordinal, string Name, string DataTypeName, uint TypeOid);

/// <summary>A RAISE NOTICE / WARNING / INFO message received while a statement ran.</summary>
public sealed record QueryNotice(string Severity, string Message, string? Detail, string? Hint);

/// <summary>A server (or client) error for one statement. <see cref="Position"/> is a 0-based offset into the full SQL text
/// passed to ExecuteAsync and <see cref="Line"/>/<see cref="Column"/> are 1-based, so the editor can underline it.</summary>
public sealed record QueryError(string Message, string? SqlState, string? Detail, string? Hint, int? Position, int? Line, int? Column)
{
    public override string ToString() => SqlState is null ? Message : $"{SqlState}: {Message}";
}

/// <summary>Outcome of one statement. Rows are the same arrays that were streamed in <see cref="ResultBatch"/>es.</summary>
public sealed class StatementResult
{
    public required int Index { get; init; }
    public required SqlStatement Statement { get; init; }
    /// <summary>Title from a <c>-- name:</c> directive above the statement, or null.</summary>
    public string? Title { get; init; }
    public IReadOnlyList<ResultColumn> Columns { get; internal set; } = [];
    public List<object?[]> Rows { get; } = [];
    public bool HasResultSet { get; internal set; }
    /// <summary>Rows inserted/updated/deleted (or selected) as reported by the server; null when not applicable.</summary>
    public long? AffectedRows { get; internal set; }
    /// <summary>"SELECT", "INSERT", "UPDATE", "DELETE", "CREATE", … (first word of the statement for DDL).</summary>
    public string? CommandTag { get; internal set; }
    public List<QueryNotice> Notices { get; } = [];
    public QueryError? Error { get; internal set; }
    public TimeSpan Elapsed { get; internal set; }
    /// <summary>Fetching stopped at the row limit; more rows existed.</summary>
    public bool Truncated { get; internal set; }
    public bool Cancelled { get; internal set; }
}

/// <summary>
/// Streaming progress for one statement: a batch of up to 200 rows, or the final notification
/// (<see cref="IsFinal"/> with <see cref="Result"/>) once the statement is done. <see cref="Columns"/> is set on every batch.
/// Batches are immutable once reported.
/// </summary>
public sealed record ResultBatch(
    int StatementIndex,
    SqlStatement Statement,
    IReadOnlyList<ResultColumn> Columns,
    IReadOnlyList<object?[]> Rows,
    int TotalRows,
    bool IsFinal,
    StatementResult? Result);

/// <summary>All statements of one ExecuteAsync call.</summary>
public sealed class QueryResult
{
    public required IReadOnlyList<StatementResult> Statements { get; init; }
    public TimeSpan Elapsed { get; init; }
    public bool Cancelled { get; init; }
    public bool HasErrors => Statements.Any(s => s.Error is not null);
}

/// <summary>Options for <see cref="DbSession.ExecuteAsync"/>.</summary>
public sealed record ExecuteOptions
{
    /// <summary>Stop fetching after this many rows per statement (null or 0 = no limit). The SQL is not rewritten.</summary>
    public int? RowLimit { get; init; } = 1000;
    /// <summary>Stop at the first failing statement (settings editor.returnOnError).</summary>
    public bool ReturnOnError { get; init; }
    /// <summary>Npgsql CommandTimeout; 0 = none.</summary>
    public int CommandTimeoutSeconds { get; init; }
    public int BatchSize { get; init; } = 200;
}
