using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Tracks;
using RouteBaseWeb.Tests.Helpers;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Heatmaps;
using RouteBaseWeb.Services.Lifecycle;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Heatmaps;

/// <summary>
/// TE-06 characterization pins P3/P4: drive <see cref="HeatmapGenerationService.GenerateAsync"/>
/// end-to-end (paint → pyramid → tile persistence) on a real in-memory SQLite DB and freeze the
/// resulting Heatmap metadata + per-zoom tile counts. This is the parity contract for TE-05 — the
/// TrackReader-backed PaintAsync must reproduce these exact tiles. GenerateAsync had no test before
/// this pin (analysis TE-06). Do NOT edit the asserted values when the swap lands.
///
/// Fixture: two short parallel tracks near (47.000, 11.000); each effort paints its whole track.
/// The area is tiny, so the pyramid resolves to a single z16 tile (CountMax 1).
/// </summary>
public class HeatmapGenerationPinTests : IAsyncLifetime
{
    private const string Me = "me";

    // Frozen z16-tile geographic bounds + normalization max for the fixture below.
    private const double PinCountMax = 1;
    private const double PinSouth = 46.99898763815462;
    private const double PinWest = 10.997314453125;
    private const double PinNorth = 47.00273390667882;
    private const double PinEast = 11.0028076171875;

    private RouteBaseDbContext _context = null!;
    private HeatmapGenerationService _service = null!;
    private HeatmapTilePackStore _tileStore = null!;
    private string _tileRoot = null!;

    public async Task InitializeAsync()
    {
        var dbName = $"testdb_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>()
            .UseSqlite($"Data Source=file:{dbName}?mode=memory&cache=shared")
            .Options;

        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();
        await new DbSchemaManager(_context).EnsureTrackContentTablesAsync();

        _context.Users.Add(new AppUser
        {
            UserId = Me, DisplayName = Me, Email = $"{Me}@test.com", IsAdmin = false,
            CreatedAt = DateTime.UtcNow.ToString("O"), LastLogin = DateTime.UtcNow.ToString("O"),
        });
        await _context.SaveChangesAsync();

        _tileRoot = Path.Combine(Path.GetTempPath(), $"hmtiles_{Guid.NewGuid():N}");
        _tileStore = new HeatmapTilePackStore(
            new HeatmapStorageOptions { Root = _tileRoot },
            new HeatmapTileIndexCache(new Microsoft.Extensions.Caching.Memory.MemoryCache(
                new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())), TimeProvider.System);

        _service = new HeatmapGenerationService(
            _context,
            _tileStore,
            new HeatmapPyramidBuilder(),
            new HeatmapProgress(TimeProvider.System),
            NullLogger<HeatmapGenerationService>.Instance,
            new DirectTrackReader(_context),
            new TestVisibilityService(Me), TimeProvider.System); // HM-16: paint-time visibility filter
    }

    public async Task DisposeAsync()
    {
        await _context.Database.EnsureDeletedAsync();
        await _context.DisposeAsync();
        try { Directory.Delete(_tileRoot, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    [Fact]
    public async Task GenerateAsync_SpanFixture_PaintsExactTiles()
    {
        var (segId, tA, tB) = await SeedTwoTrackSegmentAsync();
        await SeedCalculatedEffortsAsync(segId, tA, tB);
        await SeedHeatmapAsync(segId);

        await _service.GenerateAsync(segId, Me);

        var h = await _context.Heatmaps.AsNoTracking().FirstAsync(x => x.SegmentId == segId);
        await AssertFrozenOutputAsync(h, tA, tB);
    }

    [Fact]
    public async Task GenerateAsync_GuessedOnlyAndSwappedRange_SameTiles()
    {
        // Track A: guessed-only ids (no calculated) → ResolveGpsRange falls back to guessed.
        // Track B: calculated entry>exit (swapped) → ResolveGpsRange normalizes to entry<exit.
        // Both resolve to the same whole-track spans as the calculated fixture → identical tiles.
        var (segId, tA, tB) = await SeedTwoTrackSegmentAsync();
        await SeedGuessedAndSwappedEffortsAsync(segId, tA, tB);
        await SeedHeatmapAsync(segId);

        await _service.GenerateAsync(segId, Me);

        var h = await _context.Heatmaps.AsNoTracking().FirstAsync(x => x.SegmentId == segId);
        await AssertFrozenOutputAsync(h, tA, tB);
    }

    [Fact]
    public async Task GenerateAsync_SkipsEffortsOfNonVisibleUsers()
    {
        // HM-16: residue efforts of users no longer visible (ex-friends) must not be painted.
        var (segId, tA, tB) = await SeedTwoTrackSegmentAsync();
        await SeedCalculatedEffortsAsync(segId, tA, tB);
        await SeedHeatmapAsync(segId);

        _context.Users.Add(new AppUser
        {
            UserId = "stranger", DisplayName = "Stranger", Email = "s@test.com", IsAdmin = false,
            CreatedAt = DateTime.UtcNow.ToString("O"), LastLogin = DateTime.UtcNow.ToString("O"),
        });
        var strangerActivity = new Activity
        {
            StartGpsLat = 48.0, StartGpsLong = 12.0, EndGpsLat = 48.001, EndGpsLong = 12.0,
            UserId = "stranger",
        };
        _context.Activities.Add(strangerActivity);
        await _context.SaveChangesAsync();
        var pts = new List<GpsData>();
        for (int i = 0; i < 10; i++)
            pts.Add(new GpsData { GpsLat = 48.0 + i * 0.0001, GpsLong = 12.0 });
        await TrackSeed.SeedTrackAsync(_context, strangerActivity.TrackId, pts);
        _context.GpsInSegments.Add(new GpsInSegment
        {
            TrackId = strangerActivity.TrackId, SegmentId = segId,
            GpsIdEntryCalculated = 0, GpsIdExitCalculated = 9,
        });
        await _context.SaveChangesAsync();

        await _service.GenerateAsync(segId, Me);

        // The stranger's far-away track (48°/12°) would have widened the bounds and joined
        // ActivityIds — the frozen own-tracks-only output proves it was filtered out.
        var h = await _context.Heatmaps.AsNoTracking().FirstAsync(x => x.SegmentId == segId);
        await AssertFrozenOutputAsync(h, tA, tB);
    }

    [Fact]
    public async Task GenerateAsync_SkipsTrashedOwnRide()
    {
        // SEC-01: a ride out of circulation is invisible everywhere else, so it is not painted either.
        var (segId, tA, tB) = await SeedTwoTrackSegmentAsync();
        await SeedCalculatedEffortsAsync(segId, tA, tB);
        await SeedHeatmapAsync(segId);
        await SeedFarEffortAsync(segId, Visibilities.Friends, LifecycleStates.Trashed);

        await _service.GenerateAsync(segId, Me);

        var h = await _context.Heatmaps.AsNoTracking().FirstAsync(x => x.SegmentId == segId);
        await AssertFrozenOutputAsync(h, tA, tB);
    }

    [Fact]
    public async Task GenerateAsync_PublicSegment_PaintsOnlyPublicRides()
    {
        // SEC-01: a public segment's tiles reach every user, so even the OWNER's friends-only ride stays out.
        var (segId, tA, tB) = await SeedTwoTrackSegmentAsync();
        await SeedCalculatedEffortsAsync(segId, tA, tB);
        await SeedHeatmapAsync(segId);
        await _context.Segments.Where(s => s.SegmentId == segId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Visibility, Visibilities.Public));
        await _context.Activities.Where(a => a.TrackId == new TrackId(tA) || a.TrackId == new TrackId(tB))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Visibility, Visibilities.Public));
        await SeedFarEffortAsync(segId, Visibilities.Friends, LifecycleStates.Released);

        await _service.GenerateAsync(segId, Me);

        var h = await _context.Heatmaps.AsNoTracking().FirstAsync(x => x.SegmentId == segId);
        await AssertFrozenOutputAsync(h, tA, tB);
    }

    // ── API-04: the poll endpoint's Error must never carry a raw exception message ──

    [Fact]
    public async Task GenerateAsync_ArbitraryException_PersistsGenericError_NotTheRawMessage()
    {
        // A raw exception thrown deep in generation (here: the track read) must be collapsed to a generic
        // message before it is persisted to Heatmap.Error — that field is surfaced to friends'/public-segment
        // viewers by GetHeatmap. The exception's own text must never appear there.
        const string secret = "SENSITIVE /var/secret/connection-string";
        var (segId, tA, tB) = await SeedTwoTrackSegmentAsync();
        await SeedCalculatedEffortsAsync(segId, tA, tB);
        await SeedHeatmapAsync(segId);

        var leaky = new HeatmapGenerationService(
            _context, _tileStore, new HeatmapPyramidBuilder(), new HeatmapProgress(TimeProvider.System),
            NullLogger<HeatmapGenerationService>.Instance,
            new ThrowingTrackReader(secret), new TestVisibilityService(Me), TimeProvider.System);

        await Assert.ThrowsAnyAsync<Exception>(() => leaky.GenerateAsync(segId, Me));

        var h = await _context.Heatmaps.AsNoTracking().FirstAsync(x => x.SegmentId == segId);
        Assert.Equal("failed", h.Status);
        Assert.DoesNotContain(secret, h.Error!, StringComparison.Ordinal);
        Assert.Equal(HeatmapGenerationService.GenericFailureMessage, h.Error);
    }

    [Fact]
    public async Task GenerateAsync_NoEfforts_PersistsCuratedError()
    {
        // The inverse: a CURATED failure ("run analysis first") IS meant for the viewer, so its message must
        // survive verbatim — the generic collapse must not swallow genuinely-useful, safe text.
        var (segId, _, _) = await SeedTwoTrackSegmentAsync();
        await SeedHeatmapAsync(segId); // no efforts seeded → spans.Count == 0

        await Assert.ThrowsAsync<HeatmapGenerationException>(() => _service.GenerateAsync(segId, Me));

        var h = await _context.Heatmaps.AsNoTracking().FirstAsync(x => x.SegmentId == segId);
        Assert.Equal("failed", h.Status);
        Assert.Equal("No efforts for this segment — run analysis first.", h.Error);
    }

    // A track reader that throws a raw exception with a distinctive (would-be-leaked) message — models any
    // provider/IO failure deep in the paint loop.
    private sealed class ThrowingTrackReader(string message) : ITrackReader
    {
        public Task<DecodedTrack?> GetTrackAsync(TrackId trackId, CancellationToken ct = default) =>
            throw new InvalidOperationException(message);
    }

    // ---------------------------------------------------------------- assertions

    private async Task AssertFrozenOutputAsync(Heatmap h, int tA, int tB)
    {
        Assert.Equal("done", h.Status);
        Assert.Equal(16, h.MinZoom);
        Assert.Equal(16, h.MaxZoom);
        Assert.Equal(PinCountMax, h.CountMax);
        Assert.Equal(PinSouth, h.BoundsSouth);
        Assert.Equal(PinWest, h.BoundsWest);
        Assert.Equal(PinNorth, h.BoundsNorth);
        Assert.Equal(PinEast, h.BoundsEast);
        Assert.Equal($"{tA},{tB}", h.ActivityIds);

        var tilesByZoom = await TileCountsByZoomAsync(h.HeatmapId);
        Assert.Equal(new Dictionary<int, int> { [16] = 1 }, tilesByZoom);
    }

    // ---------------------------------------------------------------- helpers

    // Per-zoom tile counts read back from the packed .htiles file (tiles moved from DB blobs to FS).
    private async Task<Dictionary<int, int>> TileCountsByZoomAsync(int heatmapId)
    {
        var entries = await HtilesFormat.ReadEntriesAsync(_tileStore.GetPhysicalPath(heatmapId), CancellationToken.None);
        Assert.NotNull(entries);
        return entries!.GroupBy(e => (int)e.Z).ToDictionary(g => g.Key, g => g.Count());
    }

    private async Task<(SegmentId segId, int trackA, int trackB)> SeedTwoTrackSegmentAsync()
    {
        var segment = new Segment { UserId = Me, SegmentKind = "shapes", SegmentDistance = 100 };
        _context.Segments.Add(segment);
        await _context.SaveChangesAsync();

        var tA = await SeedTrackAsync(lngBase: 11.0000);
        var tB = await SeedTrackAsync(lngBase: 11.0002);
        return (segment.SegmentId, tA, tB);
    }

    private async Task<int> SeedTrackAsync(double lngBase)
    {
        var activity = new Activity
        {
            StartGpsLat = 47.0000, StartGpsLong = lngBase,
            EndGpsLat = 47.0009, EndGpsLong = lngBase, UserId = Me,
        };
        _context.Activities.Add(activity);
        await _context.SaveChangesAsync();
        TrackId trackId = activity.TrackId;

        var pts = new List<GpsData>();
        for (int i = 0; i < 10; i++)
            pts.Add(new GpsData { GpsLat = 47.0000 + i * 0.0001, GpsLong = lngBase });
        await TrackSeed.SeedTrackAsync(_context, trackId, pts);
        return trackId.Value;
    }

    // Ordinal bounds (first/last point) of a track, for effort spans — dense 0..n-1 in the blob.
    private async Task<(int first, int last)> TrackGpsRangeAsync(TrackId trackId)
    {
        var counts = await _context.Database
            .SqlQueryRaw<int>("SELECT (SELECT point_count FROM Activity_Version WHERE track_id = {0} AND is_head = 1) AS Value", trackId.Value)
            .ToListAsync();
        return (0, counts.First() - 1);
    }

    private async Task SeedCalculatedEffortsAsync(SegmentId segId, int tA, int tB)
    {
        var (aFirst, aLast) = await TrackGpsRangeAsync(new TrackId(tA));
        var (bFirst, bLast) = await TrackGpsRangeAsync(new TrackId(tB));
        _context.GpsInSegments.Add(new GpsInSegment
        {
            TrackId = new TrackId(tA), SegmentId = segId, GpsIdEntryCalculated = aFirst, GpsIdExitCalculated = aLast,
        });
        _context.GpsInSegments.Add(new GpsInSegment
        {
            TrackId = new TrackId(tB), SegmentId = segId, GpsIdEntryCalculated = bFirst, GpsIdExitCalculated = bLast,
        });
        await _context.SaveChangesAsync();
    }

    private async Task SeedGuessedAndSwappedEffortsAsync(SegmentId segId, int tA, int tB)
    {
        var (aFirst, aLast) = await TrackGpsRangeAsync(new TrackId(tA));
        var (bFirst, bLast) = await TrackGpsRangeAsync(new TrackId(tB));
        // Track A: guessed only (calculated absent).
        _context.GpsInSegments.Add(new GpsInSegment
        {
            TrackId = new TrackId(tA), SegmentId = segId, GpsIdEntryGuessed = aFirst, GpsIdExitGuessed = aLast,
        });
        // Track B: calculated present but entry>exit (swapped) — ResolveGpsRange must reorder.
        _context.GpsInSegments.Add(new GpsInSegment
        {
            TrackId = new TrackId(tB), SegmentId = segId, GpsIdEntryCalculated = bLast, GpsIdExitCalculated = bFirst,
        });
        await _context.SaveChangesAsync();
    }

    // An own effort far from the fixture (48°/12°): painting it would widen the frozen bounds and join
    // ActivityIds, so an unchanged frozen output proves it was filtered out.
    private async Task SeedFarEffortAsync(SegmentId segId, string visibility, string lifecycle)
    {
        var far = new Activity
        {
            StartGpsLat = 48.0, StartGpsLong = 12.0, EndGpsLat = 48.001, EndGpsLong = 12.0,
            UserId = Me, Visibility = visibility, Lifecycle = lifecycle,
        };
        _context.Activities.Add(far);
        await _context.SaveChangesAsync();
        var pts = new List<GpsData>();
        for (int i = 0; i < 10; i++)
            pts.Add(new GpsData { GpsLat = 48.0 + i * 0.0001, GpsLong = 12.0 });
        await TrackSeed.SeedTrackAsync(_context, far.TrackId, pts);
        _context.GpsInSegments.Add(new GpsInSegment
        {
            TrackId = far.TrackId, SegmentId = segId, GpsIdEntryCalculated = 0, GpsIdExitCalculated = 9,
        });
        await _context.SaveChangesAsync();
    }

    private async Task SeedHeatmapAsync(SegmentId segId)
    {
        _context.Heatmaps.Add(new Heatmap { SegmentId = segId, Status = "pending" });
        await _context.SaveChangesAsync();
    }
}
