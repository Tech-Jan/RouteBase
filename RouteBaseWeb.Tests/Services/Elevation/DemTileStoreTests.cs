using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Elevation;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Elevation;

/// <summary>
/// Round-trip + concurrency semantics for <see cref="DemTileStore"/> against a real (in-memory) SQLite
/// context, same style as the schema-manager tests. Table created via the schema manager's DDL.
/// </summary>
public class DemTileStoreTests : IAsyncLifetime
{
    private RouteBaseDbContext _context = null!;
    private DemTileStore _store = null!;

    public async Task InitializeAsync()
    {
        var conn = $"Data Source=file:demtiles_test_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options;
        _context = new RouteBaseDbContext(options);
        // Keep one connection open for the whole test so the shared-cache in-memory DB survives the
        // store's own ref-counted open/close cycles.
        await _context.Database.OpenConnectionAsync();
        await _context.Database.EnsureCreatedAsync();
        await new DbSchemaManager(_context).EnsureDemTilesTableAsync();
        _store = new DemTileStore(_context, TimeProvider.System);
    }

    public async Task DisposeAsync()
    {
        await _context.Database.CloseConnectionAsync();
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task InsertThenRead_ReturnsSameBytes()
    {
        var png = new byte[] { 1, 2, 3, 4, 5 };
        await _store.InsertTileAsync(13, 4277, 2896, png);

        var read = await _store.ReadTileAsync(13, 4277, 2896);

        Assert.Equal(png, read);
    }

    [Fact]
    public async Task ReadTile_Missing_ReturnsNull()
    {
        Assert.Null(await _store.ReadTileAsync(13, 1, 2));
    }

    [Fact]
    public async Task InsertTile_DoubleInsert_FirstWriteWins()
    {
        var first = new byte[] { 10, 20, 30 };
        var second = new byte[] { 99, 98, 97 };
        await _store.InsertTileAsync(13, 100, 200, first);
        await _store.InsertTileAsync(13, 100, 200, second); // INSERT OR IGNORE — no-op

        Assert.Equal(first, await _store.ReadTileAsync(13, 100, 200));
    }

    [Fact]
    public async Task DeleteTile_RemovesRow_AndIsIdempotent()
    {
        // EL-01: purge path for poisoned entries — after delete the tile reads as missing, and a
        // re-insert (the refetch) is no longer shadowed by INSERT OR IGNORE.
        var poison = new byte[] { 7, 7, 7 };
        var good = new byte[] { 42, 42, 42 };
        await _store.InsertTileAsync(13, 5, 6, poison);

        await _store.DeleteTileAsync(13, 5, 6);
        Assert.Null(await _store.ReadTileAsync(13, 5, 6));

        await _store.DeleteTileAsync(13, 5, 6);            // idempotent — no throw
        await _store.InsertTileAsync(13, 5, 6, good);
        Assert.Equal(good, await _store.ReadTileAsync(13, 5, 6));
    }
}
