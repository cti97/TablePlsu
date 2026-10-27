using TablePlsu.Core.Metadata;
using TablePlsu.Tests.Infrastructure;

namespace TablePlsu.Tests.Metadata;

public class MetadataCacheTests
{
    private static DbMetadata Sample() => new()
    {
        Database = "db",
        SearchPath = ["app", "public"],
        LoadedAt = DateTimeOffset.UtcNow,
        Schemas =
        [
            new SchemaInfo
            {
                Name = "app", IsLoaded = true,
                Tables = [new TableInfo { Schema = "app", Name = "users", Kind = RelationKind.Table, PrimaryKey = ["id"], Columns = [new ColumnInfo { Name = "id", Ordinal = 1, DataType = "integer", IsNullable = false }] }],
            },
            new SchemaInfo
            {
                Name = "public", IsLoaded = true,
                Tables =
                [
                    new TableInfo { Schema = "public", Name = "users", Kind = RelationKind.View },
                    new TableInfo { Schema = "public", Name = "Mixed", Kind = RelationKind.Table },
                ],
                Functions = [new FunctionInfo { Schema = "public", Name = "f", Arguments = "a integer" }, new FunctionInfo { Schema = "public", Name = "f", Arguments = "" }],
            },
            new SchemaInfo { Name = "other", IsLoaded = true, Tables = [new TableInfo { Schema = "other", Name = "solo" }] },
        ],
    };

    [Fact]
    public void FindTable_follows_search_path_then_unique_fallback()
    {
        var md = Sample();
        Assert.Equal("app", md.FindTable(null, "users")!.Schema);
        Assert.Equal(RelationKind.View, md.FindTable("public", "users")!.Kind);
        Assert.Equal("other", md.FindTable(null, "solo")!.Schema);
        Assert.Equal("Mixed", md.FindTable(null, "mixed")!.Name);
        Assert.Null(md.FindTable(null, "nope"));
        Assert.Null(md.FindTable("nope", "users"));
        Assert.Equal(2, md.FindFunctions(null, "f").Count);
    }

    [Fact]
    public void Empty_search_path_defaults_to_public()
    {
        var md = new DbMetadata { Schemas = Sample().Schemas };
        Assert.Equal(["public"], md.EffectiveSearchPath);
        Assert.Equal("public", md.FindTable(null, "users")!.Schema);
    }

    [Fact]
    public async Task Save_and_LoadCached_round_trip()
    {
        using var tmp = new TempDir();
        using var cache = new MetadataCache("conn1", "my/db?", null, tmp.Paths);
        Assert.Contains("db-", Path.GetFileName(cache.CacheFile));
        cache.Save(Sample());

        using var again = new MetadataCache("conn1", "my/db?", null, tmp.Paths);
        MetadataChangedEventArgs? seen = null;
        again.Changed += (_, e) => seen = e;
        Assert.True(await again.LoadCachedAsync());
        Assert.Equal(MetadataChangeReason.LoadedFromDisk, seen!.Reason);
        Assert.Equal(["id"], again.FindTable("app", "users")!.PrimaryKey);
        Assert.Equal(RelationKind.View, again.FindTable("public", "users")!.Kind);
        Assert.False(again.FindTable("app", "users")!.Columns[0].IsNullable);

        again.Clear();
        Assert.False(again.HasData);
        Assert.False(File.Exists(again.CacheFile));
    }

    [Fact]
    public async Task Corrupt_cache_is_ignored()
    {
        using var tmp = new TempDir();
        using var cache = new MetadataCache("conn1", "db", null, tmp.Paths);
        Directory.CreateDirectory(Path.GetDirectoryName(cache.CacheFile)!);
        File.WriteAllText(cache.CacheFile, "{ not json");
        Assert.False(await cache.LoadCachedAsync());
        Assert.False(cache.HasData);
    }

    [Fact]
    public void Refresh_without_session_throws()
    {
        using var tmp = new TempDir();
        using var cache = new MetadataCache("conn1", "db", null, tmp.Paths);
        Assert.Throws<InvalidOperationException>(() => { _ = cache.RefreshAsync(); });
    }
}

