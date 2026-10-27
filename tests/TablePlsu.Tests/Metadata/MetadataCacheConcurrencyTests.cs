using TablePlsu.Core.Db;
using TablePlsu.Core.Metadata;
using TablePlsu.Tests.Infrastructure;
using Xunit.Abstractions;

namespace TablePlsu.Tests.Metadata;

/// <summary>Shared loads in <see cref="MetadataCache"/>: one caller's cancellation or a concurrent refresh must not hurt another.</summary>
public class MetadataCacheConcurrencyTests(ITestOutputHelper output)
{
    /// <summary>Occupies the session's (serialized) metadata connection until the returned release is completed.</summary>
    private static async Task<TaskCompletionSource> HoldMetadataConnectionAsync(DbSession s)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = s.WithMetadataConnectionAsync(async (_, _) =>
        {
            started.SetResult();
            await release.Task;
            return 0;
        });
        await started.Task;
        return release;
    }

    [Fact]
    public async Task Cancelling_the_first_caller_of_a_shared_schema_load_does_not_cancel_the_second()
    {
        if (!TestPg.Available(output)) return;
        using var tmp = new TempDir();
        var (profile, secrets) = TestPg.Profile();
        await using var s = new DbSession(profile, secrets);
        await s.ConnectAsync();
        using var cache = new MetadataCache(profile.Id, s.Database, s, tmp.Paths, new MetadataCacheOptions { LazySchemas = true });

        var release = await HoldMetadataConnectionAsync(s);
        using var firstCts = new CancellationTokenSource();
        var first = cache.EnsureSchemaLoadedAsync("sales", ct: firstCts.Token);
        var second = cache.EnsureSchemaLoadedAsync("sales");
        firstCts.Cancel();
        release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var sales = await second.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(sales);
        Assert.True(cache.FindSchema("sales")!.IsLoaded);
    }

    [Fact]
    public async Task Cancelling_the_first_caller_of_a_shared_refresh_does_not_cancel_the_second()
    {
        if (!TestPg.Available(output)) return;
        using var tmp = new TempDir();
        var (profile, secrets) = TestPg.Profile();
        await using var s = new DbSession(profile, secrets);
        await s.ConnectAsync();
        using var cache = new MetadataCache(profile.Id, s.Database, s, tmp.Paths);

        var release = await HoldMetadataConnectionAsync(s);
        using var firstCts = new CancellationTokenSource();
        var first = cache.RefreshAsync(firstCts.Token);
        var second = cache.RefreshAsync();
        firstCts.Cancel();
        release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var md = await second.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(md.FindTable("sales", "orders"));
    }

    [Fact]
    public async Task A_refresh_that_overlaps_a_schema_load_keeps_the_schema_loaded()
    {
        if (!TestPg.Available(output)) return;
        using var tmp = new TempDir();
        var (profile, secrets) = TestPg.Profile();
        await using var s = new DbSession(profile, secrets);
        await s.ConnectAsync();
        using var cache = new MetadataCache(profile.Id, s.Database, s, tmp.Paths, new MetadataCacheOptions { LazySchemas = true });
        await cache.RefreshAsync();
        Assert.False(cache.FindSchema("sales")!.IsLoaded);

        // The sidebar expands "sales" while a refresh (Ctrl+R / reconnect) is starting: the schema load gets the
        // metadata connection first, the refresh (which decided its schema list before) publishes last.
        var release = await HoldMetadataConnectionAsync(s);
        var load = cache.EnsureSchemaLoadedAsync("sales");
        await Task.Delay(100);
        var refresh = cache.RefreshAsync();
        await Task.Delay(100);
        release.SetResult();
        await Task.WhenAll(load, refresh).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(cache.FindSchema("sales")!.IsLoaded, "the expanded schema was unloaded by the overlapping refresh");
        Assert.NotNull(cache.FindTable("sales", "orders"));
    }
}
