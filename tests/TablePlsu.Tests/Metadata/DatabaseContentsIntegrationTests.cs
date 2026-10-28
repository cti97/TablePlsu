using Npgsql;
using TablePlsu.Core.Db;
using TablePlsu.Core.Metadata;
using TablePlsu.Tests.Infrastructure;
using Xunit.Abstractions;

namespace TablePlsu.Tests.Metadata;

public class DatabaseContentsIntegrationTests(ITestOutputHelper output)
{
    private static async Task<DbSession> OpenAsync(string? database = null)
    {
        var (profile, secrets) = TestPg.Profile();
        var session = new DbSession(database is null ? profile : profile with { Database = database }, secrets);
        await session.ConnectAsync();
        return session;
    }

    [Fact]
    public async Task Seeded_database_has_user_objects()
    {
        if (!TestPg.Available(output)) return;
        await using var s = await OpenAsync();
        Assert.True(await DatabaseContents.HasUserObjectsAsync(s));
    }

    [Fact]
    public async Task Maintenance_database_has_no_user_objects()
    {
        if (!TestPg.Available(output)) return;
        await using var s = await OpenAsync("postgres");
        Assert.False(await DatabaseContents.HasUserObjectsAsync(s));
    }

    [Theory]
    [InlineData("create table s.t(id int)")]
    [InlineData("create view s.v as select 1 as x")]
    [InlineData("create sequence s.q")]
    [InlineData("create function s.f() returns int language sql as 'select 1'")]
    [InlineData("create type s.e as enum ('a')")]
    [InlineData("create domain s.d as int")]
    [InlineData("create type s.c as (a int)")]
    public async Task Any_kind_of_user_object_in_any_schema_counts(string ddl)
    {
        if (!TestPg.Available(output)) return;
        var db = "tableplsu_dc_" + Guid.NewGuid().ToString("N")[..8];
        await ExecAsync(TestPg.ConnectionString!, $"create database {db}");
        try
        {
            var cs = new NpgsqlConnectionStringBuilder(TestPg.ConnectionString) { Database = db, Pooling = false }.ConnectionString;
            await ExecAsync(cs, "create schema s");
            await using (var s = await OpenAsync(db))
                Assert.False(await DatabaseContents.HasUserObjectsAsync(s), "an empty schema is not content");
            await ExecAsync(cs, ddl);
            await using (var s = await OpenAsync(db))
                Assert.True(await DatabaseContents.HasUserObjectsAsync(s));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecAsync(TestPg.ConnectionString!, $"drop database if exists {db} with (force)");
        }
    }

    [Fact]
    public void Snapshot_proves_content_only_from_loaded_non_system_schemas()
    {
        static DbMetadata Md(params SchemaInfo[] schemas) => new() { Database = "db", Schemas = schemas };
        var table = new TableInfo { Schema = "x", Name = "t", Kind = RelationKind.Table };
        Assert.False(DatabaseContents.SnapshotHasUserObjects(Md()));
        Assert.False(DatabaseContents.SnapshotHasUserObjects(Md(new SchemaInfo { Name = "public", IsLoaded = true })));
        Assert.False(DatabaseContents.SnapshotHasUserObjects(Md(new SchemaInfo { Name = "sales", IsLoaded = false })));
        Assert.False(DatabaseContents.SnapshotHasUserObjects(Md(new SchemaInfo { Name = "pg_catalog", IsLoaded = true, Tables = [table] })));
        Assert.False(DatabaseContents.SnapshotHasUserObjects(Md(new SchemaInfo { Name = "information_schema", IsLoaded = true, Tables = [table] })));
        Assert.True(DatabaseContents.SnapshotHasUserObjects(Md(new SchemaInfo { Name = "public", IsLoaded = true, Tables = [table] })));
        Assert.True(DatabaseContents.SnapshotHasUserObjects(Md(new SchemaInfo
        {
            Name = "s", IsLoaded = true,
            Sequences = [new SequenceInfo { Schema = "s", Name = "q", DataType = "bigint" }],
        })));
    }

    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var c = new NpgsqlConnection(connectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }
}
