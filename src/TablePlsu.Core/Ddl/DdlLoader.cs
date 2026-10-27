using Npgsql;

namespace TablePlsu.Core.Ddl;

/// <summary>On-demand catalog reads for DDL views (constraint and view/function definitions). All methods are async.</summary>
public static class DdlLoader
{
    /// <summary>Constraints (p/u/c/f/x, via <c>pg_get_constraintdef</c>) and index comments of one table.</summary>
    public static async Task<TableDdlExtras> LoadTableExtrasAsync(NpgsqlConnection conn, string schema, string table, CancellationToken ct = default)
    {
        await using var batch = new NpgsqlBatch(conn)
        {
            BatchCommands =
            {
                new NpgsqlBatchCommand("""
                    select c.conname, c.contype::text, pg_get_constraintdef(c.oid, true)
                    from pg_constraint c
                    join pg_class r on r.oid = c.conrelid
                    join pg_namespace n on n.oid = r.relnamespace
                    where n.nspname = $1 and r.relname = $2 and c.contype in ('p','u','c','f','x')
                    order by c.contype, c.conname
                    """) { Parameters = { new() { Value = schema }, new() { Value = table } } },
                new NpgsqlBatchCommand("""
                    select i.relname, d.description
                    from pg_index x
                    join pg_class i on i.oid = x.indexrelid
                    join pg_class r on r.oid = x.indrelid
                    join pg_namespace n on n.oid = r.relnamespace
                    join pg_description d on d.objoid = i.oid and d.classoid = 'pg_class'::regclass and d.objsubid = 0
                    where n.nspname = $1 and r.relname = $2
                    """) { Parameters = { new() { Value = schema }, new() { Value = table } } },
            },
        };
        await using var reader = await batch.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var constraints = new List<ConstraintDef>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            constraints.Add(new ConstraintDef(reader.GetString(0), reader.GetString(1)[0], reader.GetString(2)));
        await reader.NextResultAsync(ct).ConfigureAwait(false);
        var comments = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            comments[reader.GetString(0)] = reader.GetString(1);
        return new TableDdlExtras { Constraints = constraints, IndexComments = comments };
    }

    /// <summary><c>pg_get_viewdef</c> of a view or materialized view; null when no such relation exists.</summary>
    public static async Task<ViewDefinition?> LoadViewDefinitionAsync(NpgsqlConnection conn, string schema, string name, CancellationToken ct = default)
    {
        await using var cmd = new NpgsqlCommand("""
            select pg_get_viewdef(c.oid, true), c.relkind = 'm'
            from pg_class c join pg_namespace n on n.oid = c.relnamespace
            where n.nspname = $1 and c.relname = $2 and c.relkind in ('v','m')
            """, conn) { Parameters = { new() { Value = schema }, new() { Value = name } } };
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        return new ViewDefinition(schema, name, reader.IsDBNull(0) ? "" : reader.GetString(0), reader.GetBoolean(1));
    }

    /// <summary>
    /// <c>pg_get_functiondef</c> of a routine by oid. Null when the oid does not exist or is an aggregate (which has no
    /// function definition).
    /// </summary>
    public static async Task<string?> LoadFunctionDefinitionAsync(NpgsqlConnection conn, uint oid, CancellationToken ct = default)
    {
        await using var cmd = new NpgsqlCommand(
            "select case when p.prokind = 'a' then null else pg_get_functiondef(p.oid) end from pg_proc p where p.oid = $1", conn)
        {
            Parameters = { new() { Value = oid, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Oid } },
        };
        var v = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return v as string;
    }

    /// <summary>
    /// Resolves a routine oid from "schema", "name" and optional identity arguments ("x integer"); with no arguments the
    /// routine must not be overloaded. Null when not found or ambiguous.
    /// </summary>
    public static async Task<uint?> FindFunctionOidAsync(NpgsqlConnection conn, string schema, string name, string? arguments, CancellationToken ct = default)
    {
        await using var cmd = new NpgsqlCommand("""
            select p.oid from pg_proc p join pg_namespace n on n.oid = p.pronamespace
            where n.nspname = $1 and p.proname = $2 and ($3::text is null or pg_get_function_identity_arguments(p.oid) = $3)
            limit 2
            """, conn)
        {
            Parameters = { new() { Value = schema }, new() { Value = name }, new() { Value = (object?)arguments ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text } },
        };
        var oids = new List<uint>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) oids.Add(reader.GetFieldValue<uint>(0));
        return oids.Count == 1 ? oids[0] : null;
    }
}

/// <summary>Thrown by <see cref="DdlExecutor"/> when a statement fails; the transaction has been rolled back.</summary>
public sealed class DdlExecutionException(int index, string statement, Exception inner)
    : Exception($"Statement {index + 1} failed: {inner.Message}", inner)
{
    public int StatementIndex { get; } = index;
    public string Statement { get; } = statement;
    public PostgresException? PostgresError => InnerException as PostgresException;
}

/// <summary>Runs DDL statements in one transaction on a dedicated connection (never the editor's query connection).</summary>
public static class DdlExecutor
{
    /// <summary>
    /// Executes each statement as one command (no splitting, so function bodies are safe). With
    /// <paramref name="transaction"/> they all commit or all roll back. Throws <see cref="DdlExecutionException"/>.
    /// </summary>
    public static async Task ExecuteAsync(NpgsqlConnection conn, IReadOnlyList<string> statements, bool transaction = true, CancellationToken ct = default)
    {
        await using var tx = transaction ? await conn.BeginTransactionAsync(ct).ConfigureAwait(false) : null;
        for (var i = 0; i < statements.Count; i++)
        {
            try
            {
                await using var cmd = new NpgsqlCommand(statements[i], conn, tx);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
            {
                if (tx is not null)
                {
                    try { await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
                    catch (NpgsqlException) { /* the connection may be broken; the server rolls back anyway */ }
                }
                throw new DdlExecutionException(i, statements[i], ex);
            }
        }
        if (tx is not null) await tx.CommitAsync(ct).ConfigureAwait(false);
    }
}
