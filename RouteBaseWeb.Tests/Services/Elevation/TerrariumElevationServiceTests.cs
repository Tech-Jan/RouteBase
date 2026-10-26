using System.Net;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Elevation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Elevation;

/// <summary>
/// Service-level behaviour of <see cref="TerrariumElevationService"/> against the checked-in fixture
/// tile served by a fake handler, and a real (in-memory) SQLite <see cref="DemTileStore"/>. Pins:
/// exact per-point full replace, invalid points untouched, one fetch per distinct tile, a warm cache
/// second call does zero HTTP, and endpoint-down keeps the original GPS altitude.
/// </summary>
public class TerrariumElevationServiceTests : IAsyncLifetime
{
    // Jungfrau summit — the fixture tile (z13 x4277 y2896) covers this; bilinear ≈ 3993.05 m.
    private const double JungfrauLat = 46.5367;
    private const double JungfrauLon = 7.9625;
    private const double ExpectedDem = 3993.05;

    private static readonly byte[] FixturePng =
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "terrarium_13_4277_2896.png"));

    private RouteBaseDbContext _context = null!;
    private DemTileStore _store = null!;

    public async Task InitializeAsync()
    {
        var conn = $"Data Source=file:terrelev_test_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options;
        _context = new RouteBaseDbContext(options);
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

    private TerrariumElevationService Service(HttpMessageHandler handler) =>
        new(new FakeHttpClientFactory(handler), _store, NullLogger<TerrariumElevationService>.Instance);

    private static GpsData Point(double? lat, double? lon, double? altitude) =>
        new() { GpsLat = lat, GpsLong = lon, Altitude = altitude };

    [Fact]
    public async Task ValidPoints_GetExactBilinearValue_InvalidUntouched_OneFetchPerTile()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, FixturePng);
        var points = new List<GpsData>
        {
            Point(JungfrauLat, JungfrauLon, 1000),  // valid → replaced
            Point(null, null, 555),                  // no coords → untouched
            Point(JungfrauLat, JungfrauLon, 2000),  // valid → replaced (same tile)
        };

        await Service(handler).ApplyDemElevationAsync(points);

        Assert.Equal(ExpectedDem, points[0].Altitude!.Value, 2);
        Assert.Equal(555, points[1].Altitude!.Value);        // invalid point kept its GPS altitude
        Assert.Equal(ExpectedDem, points[2].Altitude!.Value, 2);
        Assert.Equal(1, handler.CallCount);                  // both valid points share the one tile
    }

    [Fact]
    public async Task SecondCall_HitsCache_ZeroHttp()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, FixturePng);
        var first = new List<GpsData> { Point(JungfrauLat, JungfrauLon, 1000) };
        await Service(handler).ApplyDemElevationAsync(first);
        Assert.Equal(1, handler.CallCount);

        var second = new List<GpsData> { Point(JungfrauLat, JungfrauLon, 1000) };
        await Service(handler).ApplyDemElevationAsync(second);

        Assert.Equal(1, handler.CallCount);                  // tile served from Dem_Tiles — no new fetch
        Assert.Equal(ExpectedDem, second[0].Altitude!.Value, 2);
    }

    [Fact]
    public async Task EndpointDown_KeepsGpsAltitude()
    {
        var handler = new FakeHandler(HttpStatusCode.InternalServerError, []);
        var points = new List<GpsData> { Point(JungfrauLat, JungfrauLon, 1234) };

        await Service(handler).ApplyDemElevationAsync(points);

        Assert.Equal(1234, points[0].Altitude!.Value);       // missing tile → original GPS altitude survives
    }

    [Fact]
    public async Task CorruptFetchedTile_NotStored_KeepsGpsAltitude_RefetchesNextCall()
    {
        // EL-01: a 200 body that fails PNG decode must never enter the no-expiry cache — it would
        // shadow the tile forever. Pre-fix this stored the garbage and the second call did 0 HTTP.
        var handler = new FakeHandler(HttpStatusCode.OK, [1, 2, 3, 4]);
        var points = new List<GpsData> { Point(JungfrauLat, JungfrauLon, 1234) };

        await Service(handler).ApplyDemElevationAsync(points);
        Assert.Equal(1234, points[0].Altitude!.Value);       // undecodable → fallback contract holds
        Assert.Null(await _store.ReadTileAsync(13, 4277, 2896));

        await Service(handler).ApplyDemElevationAsync(points);
        Assert.Equal(2, handler.CallCount);                  // not cached → refetched
    }

    [Fact]
    public async Task PoisonedCachedTile_PurgedAndRefetched()
    {
        // EL-01 self-heal: a poisoned row (stored by a pre-validation build) is deleted and refetched.
        await _store.InsertTileAsync(13, 4277, 2896, [9, 9, 9, 9]);
        var handler = new FakeHandler(HttpStatusCode.OK, FixturePng);
        var points = new List<GpsData> { Point(JungfrauLat, JungfrauLon, 1000) };

        var replaced = await Service(handler).ApplyDemElevationAsync(points);

        Assert.Equal(1, replaced);
        Assert.Equal(ExpectedDem, points[0].Altitude!.Value, 2);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(FixturePng, await _store.ReadTileAsync(13, 4277, 2896)); // healed row round-trips
    }

    [Fact]
    public async Task OversizedBody_KeepsGpsAltitude()
    {
        // EL-02: bodies beyond MaxResponseContentBufferSize (1 MB) fail the fetch instead of buffering.
        var handler = new FakeHandler(HttpStatusCode.OK, new byte[1024 * 1024 + 1]);
        var points = new List<GpsData> { Point(JungfrauLat, JungfrauLon, 777) };

        await Service(handler).ApplyDemElevationAsync(points);

        Assert.Equal(777, points[0].Altitude!.Value);
        Assert.Null(await _store.ReadTileAsync(13, 4277, 2896));
    }

    [Fact]
    public async Task ApplyDem_ReturnsReplacedCount()
    {
        // EL-08: the replaced-count feeds the refresh endpoint's honest result.
        var handler = new FakeHandler(HttpStatusCode.OK, FixturePng);
        var points = new List<GpsData>
        {
            Point(JungfrauLat, JungfrauLon, 1000),
            Point(null, null, 555),
            Point(JungfrauLat, JungfrauLon, 2000),
        };
        Assert.Equal(2, await Service(handler).ApplyDemElevationAsync(points));

        var down = new FakeHandler(HttpStatusCode.InternalServerError, []);
        var unlucky = new List<GpsData> { Point(-46.5367, -172.9625, 1234) }; // ocean tile, not cached above
        Assert.Equal(0, await Service(down).ApplyDemElevationAsync(unlucky));
    }

    private sealed class FakeHandler(HttpStatusCode status, byte[] body) : HttpMessageHandler
    {
        private int _callCount;
        public int CallCount => _callCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });
        }
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
