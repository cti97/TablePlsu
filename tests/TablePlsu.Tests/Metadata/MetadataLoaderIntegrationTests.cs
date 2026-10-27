using System.Diagnostics;
using Npgsql;
using TablePlsu.Core.Db;
using TablePlsu.Core.Metadata;
using TablePlsu.Tests.Infrastructure;
using Xunit.Abstractions;

namespace TablePlsu.Tests.Metadata;

public class MetadataLoaderIntegrationTests(ITestOutputHelper output)
{
    private static async Task<DbSession> OpenAsync()
    {
        var (profile, secrets) = TestPg.Profile();
        var session = new DbSession(profile, secrets);
        await session.ConnectAsync();
        return session;
    }

    [Fact]
    public async Task Whole_db_load_reads_seed_objects()
    {
        if (!TestPg.Available(output)) return;
        await using var s = await OpenAsync();
        var sw = Stopwatch.StartNew();
        var md = await s.WithMetadataConnectionAsync((c, ct) => MetadataLoader.LoadAsync(c, null, ct));
        output.WriteLine($"whole-db load {sw.ElapsedMilliseconds} ms, {md.Schemas.Count} schemas, {md.AllTables().Count()} tables");

        Assert.Equal("tableplsu_test", md.Database);
        Assert.Contains("public", md.EffectiveSearchPath);
        Assert.DoesNotContain(md.Schemas, x => x.Name is "pg_catalog" or "information_schema" || x.Name.StartsWith("pg_toast"));
        Assert.All(md.Schemas, x => Assert.True(x.IsLoaded));

        var customers = md.FindTable(null, "customers");
        Assert.NotNull(customers);
        Assert.Equal("public", customers.Schema);
        Assert.Equal(RelationKind.Table, customers.Kind);
        Assert.Equal(["id"], customers.PrimaryKey);
        Assert.Equal(["id", "name", "email", "created_at", "meta", "active"], customers.Columns.Select(c => c.Name));
        var name = customers.FindColumn("name")!;
        Assert.False(name.IsNullable);
        Assert.Equal("text", name.DataType);
        Assert.StartsWith("nextval(", customers.FindColumn("id")!.Default);
        Assert.Equal("timestamp with time zone", customers.FindColumn("created_at")!.DataType);
        Assert.True(customers.TotalBytes > 0);
        Assert.Contains(customers.Indexes, i => i.IsPrimary && i.Columns.SequenceEqual(["id"]) && i.Method == "btree");
        var emailIdx = Assert.Single(customers.Indexes, i => !i.IsPrimary);
        Assert.True(emailIdx.IsUnique);
        Assert.Equal(["email"], emailIdx.Columns);
        Assert.StartsWith("CREATE UNIQUE INDEX", emailIdx.Definition);

        var orders = md.FindTable("sales", "orders")!;
        Assert.Equal("numeric(12,2)", orders.FindColumn("total")!.DataType);
        var fk = Assert.Single(orders.ForeignKeys);
        Assert.Equal(["customer_id"], fk.Columns);
        Assert.Equal("public", fk.RefSchema);
        Assert.Equal("customers", fk.RefTable);
        Assert.Equal(["id"], fk.RefColumns);
        Assert.Equal("NO ACTION", fk.OnDelete);
        Assert.Same(fk, orders.ForeignKeyFor("customer_id"));
        Assert.True(orders.EstimatedRows is null or > 0);

        Assert.Empty(md.FindTable("public", "no_pk")!.PrimaryKey);
        Assert.Equal(RelationKind.View, md.FindTable(null, "active_customers")!.Kind);
        Assert.Equal(RelationKind.MaterializedView, md.FindTable("sales", "order_totals")!.Kind);
        Assert.Null(md.FindTable(null, "order_totals_nope"));
        // Not on the search path, but unique across schemas.
        Assert.Equal("sales", md.FindTable(null, "orders")!.Schema);
        // Case-insensitive fallback.
        Assert.Equal("customers", md.FindTable("PUBLIC", "Customers")!.Name);

        var addOne = Assert.Single(md.FindFunctions(null, "add_one"));
        Assert.Equal("x integer", addOne.Arguments);
        Assert.Equal("integer", addOne.ReturnType);
        Assert.Equal(RoutineKind.Function, addOne.Kind);
        Assert.Equal("add_one(x integer)", addOne.Signature);

        var seq = Assert.Single(md.FindSchema("public")!.Sequences, q => q.Name == "customers_id_seq");
        Assert.Equal("customers.id", seq.OwnedBy);
        Assert.Equal("integer", seq.DataType);
    }

    [Fact]
    public async Task Lazy_load_lists_names_and_loads_selected_schemas()
    {
        if (!TestPg.Available(output)) return;
        await using var s = await OpenAsync();
        var md = await s.WithMetadataConnectionAsync((c, ct) =>
            MetadataLoader.LoadAsync(c, new MetadataLoadOptions { Schemas = ["public"] }, ct));
        Assert.True(md.FindSchema("public")!.IsLoaded);
        var sales = md.FindSchema("sales")!;
        Assert.False(sales.IsLoaded);
        Assert.Empty(sales.Tables);

        var loaded = await s.WithMetadataConnectionAsync((c, ct) => MetadataLoader.LoadSchemasAsync(c, ["sales", "missing"], ct));
        var only = Assert.Single(loaded);
        Assert.True(only.IsLoaded);
        Assert.NotNull(only.FindTable("orders"));

        var merged = md.WithSchema(only);
        Assert.NotNull(merged.FindTable("sales", "orders"));
        Assert.NotNull(merged.FindTable("public", "customers"));

        var system = await s.WithMetadataConnectionAsync((c, ct) =>
            MetadataLoader.LoadAsync(c, new MetadataLoadOptions { IncludeSystemSchemas = true, Schemas = [] }, ct));
        Assert.True(system.FindSchema("pg_catalog")!.IsSystem);
    }

    [Fact]
    public async Task Loads_types_indexes_and_fk_actions_from_scratch_schema()
    {
        if (!TestPg.Available(output)) return;
        var schema = "md_test_" + Guid.NewGuid().ToString("N")[..8];
        await using var s = await OpenAsync();
        await using (var conn = await s.OpenConnectionAsync())
        {
            await using var cmd = new NpgsqlCommand($"""
                create schema {schema};
                create type {schema}.mood as enum ('sad', 'ok', 'happy');
                create domain {schema}.posint as integer check (value > 0);
                create type {schema}.pair as (a int, b text);
                create table {schema}.parent(a int, b int, primary key (a, b));
                create table {schema}.child(id int generated always as identity primary key, pa int, pb int, m {schema}.mood,
                    total int generated always as (pa * 2) stored,
                    foreign key (pa, pb) references {schema}.parent(a, b) on delete cascade on update set null);
                create index child_expr on {schema}.child ((pa + pb), pa) where pa > 0;
                comment on table {schema}.child is 'kids';
                comment on column {schema}.child.pa is 'parent a';
                create procedure {schema}.p(x int) language sql as $$ select 1 $$;
                """, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        try
        {
            var md = await s.WithMetadataConnectionAsync((c, ct) => MetadataLoader.LoadAsync(c, new MetadataLoadOptions { Schemas = [schema] }, ct));
            var sc = md.FindSchema(schema)!;
            var child = sc.FindTable("child")!;
            Assert.Equal("kids", child.Comment);
            Assert.Equal("parent a", child.FindColumn("pa")!.Comment);
            Assert.Equal("a", child.FindColumn("id")!.Identity);
            Assert.True(child.FindColumn("total")!.IsGenerated);
            Assert.Equal($"{schema}.mood", child.FindColumn("m")!.DataType);
            var fk = Assert.Single(child.ForeignKeys);
            Assert.Equal(["pa", "pb"], fk.Columns);
            Assert.Equal(["a", "b"], fk.RefColumns);
            Assert.Equal("CASCADE", fk.OnDelete);
            Assert.Equal("SET NULL", fk.OnUpdate);
            var idx = Assert.Single(child.Indexes, i => i.Name == "child_expr");
            Assert.Equal(2, idx.Columns.Count);
            Assert.Contains("pa + pb", idx.Columns[0]);
            Assert.Equal("pa", idx.Columns[1]);
            Assert.Equal("pa > 0", idx.Predicate);
            Assert.Equal(["a", "b"], sc.FindTable("parent")!.PrimaryKey);

            var mood = Assert.Single(sc.Types, t => t.Name == "mood");
            Assert.Equal(UserTypeKind.Enum, mood.Kind);
            Assert.Equal(["sad", "ok", "happy"], mood.EnumLabels);
            var posint = Assert.Single(sc.Types, t => t.Name == "posint");
            Assert.Equal(UserTypeKind.Domain, posint.Kind);
            Assert.Equal("integer", posint.BaseType);
            var pair = Assert.Single(sc.Types, t => t.Name == "pair");
            Assert.Equal(UserTypeKind.Composite, pair.Kind);
            Assert.Equal(["a", "b"], pair.Attributes.Select(a => a.Name));
            Assert.DoesNotContain(sc.Types, t => t.Name is "child" or "parent");

            var p = Assert.Single(sc.Functions);
            Assert.Equal(RoutineKind.Procedure, p.Kind);
            Assert.Null(p.ReturnType);
        }
        finally
        {
            await using var conn = await s.OpenConnectionAsync();
            await using var drop = new NpgsqlCommand($"drop schema if exists {schema} cascade", conn);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Cache_refreshes_saves_and_reloads_from_disk()
    {
        if (!TestPg.Available(output)) return;
        using var tmp = new TempDir();
        var (profile, secrets) = TestPg.Profile();
        await using var s = new DbSession(profile, secrets);
        await s.ConnectAsync();

        using var cache = new MetadataCache(profile.Id, s.Database, s, tmp.Paths);
        Assert.False(await cache.LoadCachedAsync());
        Assert.False(cache.HasData);

        var events = new List<MetadataChangeReason>();
        cache.Changed += (_, e) => { lock (events) events.Add(e.Reason); };
        var t1 = cache.RefreshAsync();
        var t2 = cache.RefreshAsync();
        Assert.Same(t1, t2);
        var md = await t1;
        Assert.Same(md, cache.Current);
        Assert.NotNull(cache.FindTable(null, "customers"));
        Assert.NotNull(cache.LastRefreshed);
        Assert.True(File.Exists(cache.CacheFile));
        Assert.Contains(MetadataChangeReason.Refreshed, events);

        using var second = new MetadataCache(profile.Id, s.Database, null, tmp.Paths);
        var sw = Stopwatch.StartNew();
        Assert.True(await second.LoadCachedAsync());
        output.WriteLine($"disk cache load {sw.ElapsedMilliseconds} ms ({new FileInfo(cache.CacheFile).Length} bytes)");
        var orders = second.FindTable("sales", "orders")!;
        Assert.Equal(RelationKind.Table, orders.Kind);
        Assert.Equal("customers", orders.ForeignKeys[0].RefTable);
        Assert.Equal(md.Schemas.Count, second.Current.Schemas.Count);
        Assert.Equal(["id"], second.FindTable(null, "customers")!.PrimaryKey);
    }

    [Fact]
    public async Task Cache_lazy_mode_loads_schema_on_demand()
    {
        if (!TestPg.Available(output)) return;
        using var tmp = new TempDir();
        var (profile, secrets) = TestPg.Profile();
        await using var s = new DbSession(profile, secrets);
        await s.ConnectAsync();
        using var cache = new MetadataCache(profile.Id, s.Database, s, tmp.Paths, new MetadataCacheOptions { LazySchemas = true });
        await cache.RefreshAsync();
        Assert.True(cache.FindSchema("public")!.IsLoaded);
        Assert.False(cache.FindSchema("sales")!.IsLoaded);

        string? changedSchema = null;
        cache.Changed += (_, e) => changedSchema = e.Schema;
        var sales = await cache.EnsureSchemaLoadedAsync("sales");
        Assert.NotNull(sales);
        Assert.Equal("sales", changedSchema);
        Assert.NotNull(cache.FindTable("sales", "orders"));
        Assert.Null(await cache.EnsureSchemaLoadedAsync("does_not_exist"));

        // A later refresh keeps already-expanded schemas loaded.
        await cache.RefreshAsync();
        Assert.True(cache.FindSchema("sales")!.IsLoaded);
    }
}
