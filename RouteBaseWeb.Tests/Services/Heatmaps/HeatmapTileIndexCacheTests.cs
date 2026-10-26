using RouteBaseWeb.Services.Heatmaps;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Heatmaps;

/// <summary>
/// The parsed-index cache is keyed by (heatmapId, generatedAtTicks): a hit avoids re-reading the file,
/// and a timestamp bump (regenerate) forces a fresh parse so a stale index is never served.
/// </summary>
public sealed class HeatmapTileIndexCacheTests : IDisposable
{
    private const string Layer = HeatmapGenerationService.CountLogLayer;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hmidx_{Guid.NewGuid():N}");
    private readonly IMemoryCache _memory = new MemoryCache(new MemoryCacheOptions());

    private HeatmapTilePackStore NewStore(HeatmapTileIndexCache cache) =>
        new(new HeatmapStorageOptions { Root = _root }, cache, TimeProvider.System);

    public void Dispose()
    {
        _memory.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task SameKey_ServesCachedIndex_EvenAfterFileDeleted()
    {
        var cache = new HeatmapTileIndexCache(_memory);
        var store = NewStore(cache);
        await using (var w = store.BeginWrite(1))
        {
            await w.AppendAsync(Layer, new[] { new HeatmapTileData(16, 1, 1, new byte[] { 7 }) });
            await w.CommitAsync();
        }

        var first = await cache.GetOrLoadAsync(1, generatedAtTicks: 100, store.GetPhysicalPath(1), default);
        Assert.NotNull(first);

        // Delete the file; the same (id, ticks) key must still resolve from the cache (proves it was cached).
        store.Delete(1);
        var cached = await cache.GetOrLoadAsync(1, generatedAtTicks: 100, store.GetPhysicalPath(1), default);
        Assert.Same(first, cached);
    }

    [Fact]
    public async Task TimestampBump_InvalidatesAndReloads()
    {
        var cache = new HeatmapTileIndexCache(_memory);
        var store = NewStore(cache);

        await using (var w = store.BeginWrite(2))
        {
            await w.AppendAsync(Layer, new[] { new HeatmapTileData(16, 1, 1, new byte[] { 1 }) });
            await w.CommitAsync();
        }
        var v100 = await cache.GetOrLoadAsync(2, generatedAtTicks: 100, store.GetPhysicalPath(2), default);
        Assert.Single(v100!);

        // Regenerate with two tiles; a new timestamp key must reflect the new file, not the cached one.
        await using (var w = store.BeginWrite(2))
        {
            await w.AppendAsync(Layer, new[]
            {
                new HeatmapTileData(16, 1, 1, new byte[] { 1 }),
                new HeatmapTileData(16, 2, 2, new byte[] { 2 }),
            });
            await w.CommitAsync();
        }
        var v200 = await cache.GetOrLoadAsync(2, generatedAtTicks: 200, store.GetPhysicalPath(2), default);
        Assert.Equal(2, v200!.Count);
    }

    [Fact]
    public async Task MissingFile_ReturnsNull_AndIsNotCached()
    {
        var cache = new HeatmapTileIndexCache(_memory);
        var store = NewStore(cache);

        Assert.Null(await cache.GetOrLoadAsync(3, 100, store.GetPhysicalPath(3), default));

        // Now the file lands under the same key — must load rather than serve a cached null.
        await using (var w = store.BeginWrite(3))
        {
            await w.AppendAsync(Layer, new[] { new HeatmapTileData(16, 1, 1, new byte[] { 5 }) });
            await w.CommitAsync();
        }
        var loaded = await cache.GetOrLoadAsync(3, 100, store.GetPhysicalPath(3), default);
        Assert.NotNull(loaded);
    }
}
