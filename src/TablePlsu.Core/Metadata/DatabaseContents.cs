using Npgsql;
using TablePlsu.Core.Db;

namespace TablePlsu.Core.Metadata;

/// <summary>
/// A cheap server-side check for "this database has nothing in it": no relations, sequences, standalone composite
/// types, functions, domains, enums or ranges in any non-system schema. Independent of what the (possibly lazy)
/// metadata snapshot has loaded. Used by the sidebar's empty-database hint.
/// </summary>
public static class DatabaseContents
{
    /// <summary>One row, one boolean. System schemas: <c>pg_*</c> (catalog, toast, temp) and <c>information_schema</c>.</summary>
    public const string Query = """
        SELECT EXISTS (
            SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r','p','v','m','f','S','c')
              AND n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema')
        OR EXISTS (
            SELECT 1 FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema')
        OR EXISTS (
            SELECT 1 FROM pg_catalog.pg_type t JOIN pg_catalog.pg_namespace n ON n.oid = t.typnamespace
            WHERE t.typtype IN ('d','e','r')
              AND n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema')
        """;

    /// <summary>True when the connection's database has at least one user object (see <see cref="Query"/>).</summary>
    public static async Task<bool> HasUserObjectsAsync(NpgsqlConnection connection, CancellationToken ct = default)
    {
        await using var cmd = new NpgsqlCommand(Query, connection);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is true;
    }

    /// <summary>Runs the check on the session's metadata connection.</summary>
    public static Task<bool> HasUserObjectsAsync(DbSession session, CancellationToken ct = default) =>
        session.WithMetadataConnectionAsync(HasUserObjectsAsync, ct);

    /// <summary>
    /// What the snapshot already proves: true when a loaded non-system schema contains anything. False means "unknown"
    /// (unloaded schemas may still have objects), so ask the server.
    /// </summary>
    public static bool SnapshotHasUserObjects(DbMetadata metadata)
    {
        foreach (var s in metadata.Schemas)
        {
            if (s.IsSystem || IsSystemSchemaName(s.Name)) continue;
            if (s.Tables.Count > 0 || s.Functions.Count > 0 || s.Sequences.Count > 0 || s.Types.Count > 0) return true;
        }
        return false;
    }

    public static bool IsSystemSchemaName(string name) =>
        name.StartsWith("pg_", StringComparison.Ordinal) || name == "information_schema";
}
