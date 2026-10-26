using RouteBaseWeb.Services.Heatmaps;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Heatmaps;

/// <summary>
/// Round-trips the packed <c>.htiles</c> tile store: write a multi-zoom set → read exact bytes back,
/// missing tiles/files → null, idempotent delete, atomic regen overwrite, and corrupt/short files
/// degrading to "no tiles" (the endpoint serves a transparent tile) rather than throwing.
/// </summary>
public sealed class HeatmapTilePackStoreTests : IDisposable
{
    private const string Layer = HeatmapGenerationService.CountLogLayer;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hmpack_{Guid.NewGuid():N}");

    private HeatmapTilePackStore NewStore() => new(
        new HeatmapStorageOptions { Root = _root },
        new HeatmapTileIndexCache(new MemoryCache(new MemoryCacheOptions())), TimeProvider.System);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // Real 8-byte PNG signature + payload: ReadTileAsync (HM-12) rejects stored bytes without it.
    private static byte[] Png(params byte[] bytes) =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. bytes];

    [Fact]
    public async Task WriteThenRead_MultiZoom_ReturnsExactBytes()
    {
        var store = NewStore();
        await using (var w = store.BeginWrite(42))
        {
            await w.AppendAsync(Layer, new[]
            {
                new HeatmapTileData(16, 100, 200, Png(1, 1, 1)),
                new HeatmapTileData(16, 101, 200, Png(2, 2)),
            });
            await w.AppendAsync(Layer, new[] { new HeatmapTileData(15, 50, 100, Png(3, 3, 3, 3)) });
            await w.CommitAsync();
        }

        Assert.Equal(Png(1, 1, 1), await store.ReadTileAsync(42, 1, Layer, 16, 100, 200));
        Assert.Equal(Png(2, 2), await store.ReadTileAsync(42, 1, Layer, 16, 101, 200));
        Assert.Equal(Png(3, 3, 3, 3), await store.ReadTileAsync(42, 1, Layer, 15, 50, 100));
    }

    [Fact]
    public async Task ReadTile_MissingTileOrFile_ReturnsNull()
    {
        var store = NewStore();
        // No file at all.
        Assert.Null(await store.ReadTileAsync(7, 1, Layer, 16, 0, 0));

        await using (var w = store.BeginWrite(7))
        {
            await w.AppendAsync(Layer, new[] { new HeatmapTileData(16, 1, 1, Png(9)) });
            await w.CommitAsync();
        }
        // File exists, but this {z,x,y} isn't in it.
        Assert.Null(await store.ReadTileAsync(7, 1, Layer, 16, 2, 2));
        // Unknown layer → null (never throws).
        Assert.Null(await store.ReadTileAsync(7, 1, "speed", 16, 1, 1));
    }

    [Fact]
    public async Task Regenerate_OverwritesAtomically_NewBytesWithNewTimestamp()
    {
        var store = NewStore();
        await using (var w = store.BeginWrite(5))
        {
            await w.AppendAsync(Layer, new[] { new HeatmapTileData(16, 1, 1, Png(1)) });
            await w.CommitAsync();
        }
        Assert.Equal(Png(1), await store.ReadTileAsync(5, generatedAtTicks: 100, Layer, 16, 1, 1));

        await using (var w = store.BeginWrite(5))
        {
            await w.AppendAsync(Layer, new[] { new HeatmapTileData(16, 1, 1, Png(2, 2)) });
            await w.CommitAsync();
        }
        // New generatedAtTicks keys a fresh index → new bytes (a regen bumps GeneratedAt in production).
        Assert.Equal(Png(2, 2), await store.ReadTileAsync(5, generatedAtTicks: 200, Layer, 16, 1, 1));
    }

    [Fact]
    public async Task Abort_LeavesNoFile_AndTempIsRemoved()
    {
        var store = NewStore();
        var w = store.BeginWrite(9);
        await w.AppendAsync(Layer, new[] { new HeatmapTileData(16, 1, 1, Png(1)) });
        await w.DisposeAsync(); // no commit

        Assert.False(File.Exists(store.GetPhysicalPath(9)));
        Assert.False(File.Exists(store.GetPhysicalPath(9) + ".tmp"));
        Assert.Null(await store.ReadTileAsync(9, 1, Layer, 16, 1, 1));
    }

    [Fact]
    public async Task Delete_IsIdempotent()
    {
        var store = NewStore();
        await using (var w = store.BeginWrite(3))
        {
            await w.AppendAsync(Layer, new[] { new HeatmapTileData(16, 1, 1, Png(1)) });
            await w.CommitAsync();
        }
        Assert.True(File.Exists(store.GetPhysicalPath(3)));

        store.Delete(3);
        Assert.False(File.Exists(store.GetPhysicalPath(3)));
        store.Delete(3);       // second delete is a no-op
        store.Delete(999999);  // never-written id is a no-op
    }

    [Fact]
    public async Task CorruptOrTruncatedFile_TreatedAsEmpty_NoThrow()
    {
        var store = NewStore();
        var path = store.GetPhysicalPath(11);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 }); // shorter than the footer, bad magic

        Assert.Null(await store.ReadTileAsync(11, 1, Layer, 16, 1, 1));
    }

    [Fact]
    public async Task ReadTile_FileDeletedAfterIndexCached_ReturnsNull()
    {
        // HM-12: segment delete / reset removes the pack while the parsed index (10 min TTL) is still
        // cached — the read must degrade to "no tiles" (transparent), never throw FileNotFoundException.
        var store = NewStore();
        await using (var w = store.BeginWrite(21))
        {
            await w.AppendAsync(Layer, new[] { new HeatmapTileData(16, 1, 1, Png(1)) });
            await w.CommitAsync();
        }
        Assert.NotNull(await store.ReadTileAsync(21, 1, Layer, 16, 1, 1)); // warms the index cache

        File.Delete(store.GetPhysicalPath(21));
        Assert.Null(await store.ReadTileAsync(21, 1, Layer, 16, 1, 1));    // same store, warm index
    }

    [Fact]
    public async Task ReadTile_NonPngBytes_ReturnsNull()
    {
        // HM-12: a stale cached index range-read against an atomically-replaced pack yields bytes that
        // are not a PNG — the signature guard must turn them into "no tile" instead of serving garbage.
        var store = NewStore();
        await using (var w = store.BeginWrite(23))
        {
            await w.AppendAsync(Layer, new[] { new HeatmapTileData(16, 1, 1, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }) });
            await w.CommitAsync();
        }
        Assert.Null(await store.ReadTileAsync(23, 1, Layer, 16, 1, 1));
    }

    [Fact]
    public async Task ReadTile_PoisonedIndexEntry_ReturnsNull()
    {
        // HM-12: a corrupt index entry (negative/absurd length) must not reach `new byte[len]`.
        var store = NewStore();
        var path = store.GetPhysicalPath(27);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var entries = new List<HtilesFormat.TileEntry>
        {
            new(Layer: 0, Z: 16, X: 1, Y: 1, Offset: 0, Len: -5),
            new(Layer: 0, Z: 16, X: 2, Y: 2, Offset: -3, Len: 16),
            new(Layer: 0, Z: 16, X: 3, Y: 3, Offset: 0, Len: 5_000_000),
        };
        await File.WriteAllBytesAsync(path, HtilesFormat.SerializeIndexAndFooter(entries, indexOffset: 0));

        Assert.Null(await store.ReadTileAsync(27, 1, Layer, 16, 1, 1));
        Assert.Null(await store.ReadTileAsync(27, 1, Layer, 16, 2, 2));
        Assert.Null(await store.ReadTileAsync(27, 1, Layer, 16, 3, 3));
    }

    [Fact]
    public async Task EmptyCommit_ProducesReadableEmptyFile()
    {
        var store = NewStore();
        await using (var w = store.BeginWrite(13))
            await w.CommitAsync(); // no tiles appended

        Assert.True(File.Exists(store.GetPhysicalPath(13)));
        Assert.Null(await store.ReadTileAsync(13, 1, Layer, 16, 0, 0));
    }
}
