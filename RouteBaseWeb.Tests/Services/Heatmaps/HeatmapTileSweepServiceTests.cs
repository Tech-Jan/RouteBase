using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Heatmaps;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Heatmaps;

/// <summary>
/// Orphan-sweep reconcile: the pure core deletes only stored ids with no live row, and the store's
/// on-disk enumeration lists committed tile files (skipping never-written ids and in-flight temp files).
/// </summary>
public sealed class HeatmapTileSweepServiceTests : IDisposable
{
    private const string Layer = HeatmapGenerationService.CountLogLayer;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hmsweep_{Guid.NewGuid():N}");

    private HeatmapTilePackStore NewStore() => new(
        new HeatmapStorageOptions { Root = _root },
        new HeatmapTileIndexCache(new MemoryCache(new MemoryCacheOptions())), TimeProvider.System);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private static async Task WriteTileFileAsync(HeatmapTilePackStore store, int heatmapId)
    {
        await using var w = store.BeginWrite(heatmapId);
        await w.AppendAsync(Layer, new[] { new HeatmapTileData(16, 1, 1, new byte[] { 1 }) });
        await w.CommitAsync();
    }

    [Fact]
    public void SweepOrphans_DeletesOnlyIdsWithNoLiveRow()
    {
        var stored = new[] { 1, 2, 3, 4 };
        var live = new HashSet<int> { 2, 4 };
        var deleted = new List<int>();

        var count = HeatmapTileSweepService.SweepOrphans(stored, live, deleted.Add);

        Assert.Equal(2, count);
        Assert.Equal(new[] { 1, 3 }, deleted);
    }

    [Fact]
    public void SweepOrphans_NoOrphans_DeletesNothing()
    {
        var deleted = new List<int>();
        var count = HeatmapTileSweepService.SweepOrphans(
            new[] { 5, 6 }, new HashSet<int> { 5, 6 }, deleted.Add);

        Assert.Equal(0, count);
        Assert.Empty(deleted);
    }

    [Fact]
    public async Task EnumerateStoredHeatmapIds_ListsCommittedFilesOnly()
    {
        var store = NewStore();
        await WriteTileFileAsync(store, 42);
        await WriteTileFileAsync(store, 7);

        // A leftover .tmp (crash mid-write) must not be counted as a committed tile file.
        var tmp = store.GetPhysicalPath(999) + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);
        await File.WriteAllBytesAsync(tmp, new byte[] { 1, 2, 3 });

        var ids = store.EnumerateStoredHeatmapIds().OrderBy(i => i).ToList();

        Assert.Equal(new[] { 7, 42 }, ids);
    }

    [Fact]
    public void EnumerateStoredHeatmapIds_NoStorageDir_ReturnsEmpty()
    {
        var store = NewStore(); // nothing ever written; the heatmaps/ dir doesn't exist
        Assert.Empty(store.EnumerateStoredHeatmapIds());
    }

    [Fact]
    public async Task EndToEnd_SweepDeletesOrphanFile_KeepsLive()
    {
        var store = NewStore();
        await WriteTileFileAsync(store, 100); // live
        await WriteTileFileAsync(store, 200); // orphan (no row)

        var live = new HashSet<int> { 100 };
        var count = HeatmapTileSweepService.SweepOrphans(store.EnumerateStoredHeatmapIds(), live, store.Delete);

        Assert.Equal(1, count);
        Assert.True(File.Exists(store.GetPhysicalPath(100)));
        Assert.False(File.Exists(store.GetPhysicalPath(200)));
    }

    [Fact]
    public async Task DeleteAbandonedTempFiles_DeletesOldKeepsFreshAndCommitted()
    {
        // HM-13: hard-crash leftovers (.htiles.tmp) are reclaimed by the daily sweep; the age guard
        // protects an in-flight writer's temp file, committed packs are untouched.
        var store = NewStore();
        await WriteTileFileAsync(store, 42); // committed pack

        var oldTmp = store.GetPhysicalPath(100) + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(oldTmp)!);
        await File.WriteAllBytesAsync(oldTmp, new byte[] { 1 });
        File.SetLastWriteTimeUtc(oldTmp, DateTime.UtcNow.AddDays(-2));

        var freshTmp = store.GetPhysicalPath(200) + ".tmp";
        await File.WriteAllBytesAsync(freshTmp, new byte[] { 1 });

        var deleted = store.DeleteAbandonedTempFiles(TimeSpan.FromDays(1));

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(oldTmp));
        Assert.True(File.Exists(freshTmp));
        Assert.True(File.Exists(store.GetPhysicalPath(42)));
    }

    [Fact]
    public void DeleteAbandonedTempFiles_NoStorageDir_ReturnsZero()
    {
        Assert.Equal(0, NewStore().DeleteAbandonedTempFiles(TimeSpan.FromDays(1)));
    }

    [Fact]
    public async Task ReconcileInterrupted_FlipsPendingAndRunningToFailed_LeavesOthers()
    {
        // HM-11: the in-memory queue dies with the process; rows stuck pending/running after a restart
        // must be demoted to failed, or Attach 409s ("pending or running" guard) forever.
        var connectionString = $"Data Source=file:hmreconcile_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(connectionString).Options;
        await using var seedContext = new RouteBaseDbContext(options);
        await seedContext.Database.EnsureCreatedAsync();

        seedContext.Users.Add(new AppUser
        {
            UserId = "test-user", DisplayName = "Test User", Email = "t@t.t", IsAdmin = false,
            CreatedAt = DateTime.UtcNow.ToString("O"), LastLogin = DateTime.UtcNow.ToString("O"),
        });
        var statuses = new[] { "pending", "running", "done", "stale", "failed" };
        foreach (var status in statuses)
        {
            var seg = new Segment { UserId = "test-user", SegmentKind = "shapes", SegmentDistance = 100 };
            seedContext.Segments.Add(seg);
            await seedContext.SaveChangesAsync();
            seedContext.Heatmaps.Add(new Heatmap { SegmentId = seg.SegmentId, Status = status, Error = null });
        }
        await seedContext.SaveChangesAsync();

        var services = new ServiceCollection().AddSingleton(TimeProvider.System);
        services.AddScoped(_ => new RouteBaseDbContext(options));
        await using var provider = services.BuildServiceProvider();

        var sweep = new HeatmapTileSweepService(
            provider.GetRequiredService<IServiceScopeFactory>(), NewStore(),
            NullLogger<HeatmapTileSweepService>.Instance);

        var reconciled = await sweep.ReconcileInterruptedAsync(CancellationToken.None);

        Assert.Equal(2, reconciled);
        seedContext.ChangeTracker.Clear();
        var after = await seedContext.Heatmaps.AsNoTracking().OrderBy(h => h.HeatmapId).ToListAsync();
        Assert.Equal(new[] { "failed", "failed", "done", "stale", "failed" }, after.Select(h => h.Status), StringComparer.Ordinal);
        Assert.All(after.Take(2), h => Assert.Contains("Interrupted by app restart", h.Error, StringComparison.Ordinal));
        Assert.Null(after[2].Error); // untouched rows keep their fields
    }
}
