using Npgsql;
using TablePlsu.Core.Data;
using TablePlsu.Core.Db;
using TablePlsu.Core.Metadata;
using TablePlsu.Tests.Infrastructure;
using Xunit.Abstractions;

namespace TablePlsu.Tests.Data;

/// <summary>
/// Adversarial review (lens: wrong-row) of spec §10 "Edit results inline": cases where a committed UPDATE lands on a row
/// (or column) other than the one the user edited in the editor result.
/// </summary>
[Trait("Review", "wrong-row")]
public class ResultEditabilityWrongRowTests(ITestOutputHelper output)
{
    private static async Task ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync();
    }

    private static async Task<(StatementResult St, ResultEditInfo Info)> AnalyzeAsync(DbSession session, MetadataCache cache, string sql)
    {
        var result = await session.ExecuteAsync(sql, new ExecuteOptions { RowLimit = 50 });
        var st = Assert.Single(result.Statements);
        Assert.Null(st.Error);
        // The app's pipeline (ResultTabViewModel.AnalyzeEditabilityAsync): provenance → table → duplicate keys → row re-read.
        var info = await ResultEditability.AnalyzeResultAsync(st.CommandTag, st.Columns, st.Rows, cache, session);
        return (st, info);
    }

    /// <summary>
    /// A self-join (or LATERAL / CTE equivalent) where the key comes from one alias and another column from a second alias
    /// of the SAME table: every column has the same table OID and distinct attnums, so the result is judged editable. The
    /// displayed "name" belongs to row id+1, but the UPDATE is keyed by row id, so the edit is written to the wrong row.
    /// </summary>
    [Theory]
    [InlineData("select a.id, b.name from public.{0} a join public.{0} b on b.id = a.id + 1 order by a.id")]
    [InlineData("select a.id, l.name from public.{0} a cross join lateral (select name from public.{0} x where x.id = a.id + 1) l order by a.id")]
    [InlineData("with s as (select a.id, b.name from public.{0} a join public.{0} b on b.id = a.id + 1) select * from s order by id")]
    public async Task Self_join_mixing_aliases_must_not_update_the_key_row(string sqlTemplate)
    {
        if (!TestPg.Available(output)) return;
        var table = "rv_sj_" + Guid.NewGuid().ToString("N")[..10];
        await using var admin = new NpgsqlConnection(TestPg.ConnectionString);
        await admin.OpenAsync();
        await ExecAsync(admin, $"CREATE TABLE public.{table}(id int PRIMARY KEY, name text); INSERT INTO public.{table} VALUES (1, 'a'), (2, 'b')");
        var (profile, secrets) = TestPg.Profile();
        await using var session = new DbSession(profile, secrets);
        await session.ConnectAsync();
        using var tmp = new TempDir();
        using var cache = new MetadataCache(profile.Id, session.Database, session, tmp.Paths, new MetadataCacheOptions { LazySchemas = true });
        try
        {
            var (st, info) = await AnalyzeAsync(session, cache, string.Format(sqlTemplate, table));
            var row = Assert.Single(st.Rows); // (id 1, name 'b' -- the name of row 2)
            Assert.Equal(1, row[0]);
            Assert.Equal("b", row[1]);

            if (info.IsEditable)
            {
                // What the app would commit when the user changes the shown name 'b' to 'B2'.
                var pending = new PendingChanges(info.Target!.Columns.Count, info.Target.KeyColumns);
                pending.AttachRows(st.Rows);
                pending.SetCell(st.Rows[0], 1, "B2");
                var statements = ChangeSqlGenerator.Generate(info.Target, pending, returnRows: true);
                output.WriteLine(statements[0].LiteralSql);
                await using var conn = await session.OpenConnectionAsync();
                var commit = await TableDataAccess.CommitAsync(conn, statements);
                Assert.True(commit.Success, commit.Error);
                // The value the user edited belonged to row 2; row 1 must keep 'a'.
                Assert.Equal("a", await ScalarAsync(admin, $"SELECT name FROM public.{table} WHERE id = 1"));
                Assert.Equal("B2", await ScalarAsync(admin, $"SELECT name FROM public.{table} WHERE id = 2"));
            }
            Assert.False(info.IsEditable, "a self-join mixing columns of two aliases must be read-only");
        }
        finally
        {
            await ExecAsync(admin, $"DROP TABLE IF EXISTS public.{table}");
        }
    }

    /// <summary>
    /// The metadata snapshot is trusted as long as the table OID matches and the attnums exist; column NAMES are never
    /// re-checked. After another session renames columns (here: swaps the names of the key column and another int
    /// column), the cached names are stale: the generated UPDATE uses the old names, so it matches a different row by the
    /// wrong column and writes the wrong column.
    /// </summary>
    [Fact]
    public async Task Stale_metadata_after_column_rename_must_not_update_another_row()
    {
        if (!TestPg.Available(output)) return;
        var table = "rv_st_" + Guid.NewGuid().ToString("N")[..10];
        await using var admin = new NpgsqlConnection(TestPg.ConnectionString);
        await admin.OpenAsync();
        await ExecAsync(admin, $"CREATE TABLE public.{table}(pk int PRIMARY KEY, other int NOT NULL); INSERT INTO public.{table} VALUES (1, 2), (2, 1)");
        var (profile, secrets) = TestPg.Profile();
        await using var session = new DbSession(profile, secrets);
        await session.ConnectAsync();
        using var tmp = new TempDir();
        using var cache = new MetadataCache(profile.Id, session.Database, session, tmp.Paths, new MetadataCacheOptions { LazySchemas = true });
        try
        {
            // First run loads the schema into the metadata snapshot.
            var (_, first) = await AnalyzeAsync(session, cache, $"select * from public.{table}");
            Assert.True(first.IsEditable, first.Reason);

            // Someone else swaps the column names. attnum 1 (still the PK) is now called "other", attnum 2 "pk".
            await ExecAsync(admin, $"ALTER TABLE public.{table} RENAME pk TO tmp; ALTER TABLE public.{table} RENAME other TO pk; ALTER TABLE public.{table} RENAME tmp TO other");

            var (st, info) = await AnalyzeAsync(session, cache, $"select * from public.{table} order by 1");
            Assert.Equal(["other", "pk"], st.Columns.Select(c => c.Name)); // what the grid shows now
            if (info.IsEditable)
            {
                output.WriteLine("target columns: " + string.Join(", ", info.Target!.Columns));
                // The user edits the row whose key (attnum 1, shown as "other") is 1, setting its "pk" cell (attnum 2) to 99.
                var row = st.Rows.Single(r => Equals(r[0], 1));
                var pending = new PendingChanges(info.Target.Columns.Count, info.Target.KeyColumns);
                pending.AttachRows(st.Rows);
                pending.SetCell(row, 1, 99);
                var statements = ChangeSqlGenerator.Generate(info.Target, pending, returnRows: true);
                output.WriteLine(statements[0].LiteralSql);
                await using var conn = await session.OpenConnectionAsync();
                var commit = await TableDataAccess.CommitAsync(conn, statements);
                if (commit.Success)
                {
                    // Intended: the row with key 1 now has pk(attnum 2) = 99, and the row with key 2 is untouched.
                    Assert.Equal(99, await ScalarAsync(admin, $"SELECT pk FROM public.{table} WHERE other = 1"));
                    Assert.Equal(1L, await ScalarAsync(admin, $"SELECT count(*) FROM public.{table} WHERE other = 2 AND pk = 1"));
                }
            }
            // Either way the metadata names don't match the server's columns any more: the result must not be editable.
            Assert.False(info.IsEditable && info.Target!.Columns.SequenceEqual(["pk", "other"]),
                "stale column names (pk, other) were used for a table whose attnums are now named (other, pk)");
        }
        finally
        {
            await ExecAsync(admin, $"DROP TABLE IF EXISTS public.{table}");
        }
    }
}
