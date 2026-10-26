using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Matching;
using RouteBaseWeb.Services.Segments;
using RouteBaseWeb.Services.Tracks;
using RouteBaseWeb.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Segments;

/// <summary>
/// <see cref="SegmentEfforts.GetEffortPolylinesAsync"/> — the minimap preview for shape/gate segments,
/// which have no Segment_GPS. Each matched effort is sliced out of its decoded activity track (the same
/// OrdinalRange → GpsSlice funnel the perf path uses) into a lat/lng polyline; the effort count is
/// capped and the per-effort points downsampled. Excluded/invisible efforts drop out.
/// </summary>
public class GetEffortPolylinesTests : IAsyncLifetime
{
    private const string User = "test-user";
    private static readonly DateTime T0 = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private RouteBaseDbContext _context = null!;
    private SegmentEfforts _service = null!;

    public async Task InitializeAsync()
    {
        var conn = $"Data Source=file:polylines_{Guid.NewGuid():N}?mode=memory&cache=shared";
        _context = new RouteBaseDbContext(
            new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options);
        await _context.Database.EnsureCreatedAsync();
        await new DbSchemaManager(_context).EnsureTrackContentTablesAsync();

        _context.Users.Add(new AppUser
        {
            UserId = User, DisplayName = User, Email = "t@t.com", IsAdmin = false,
            CreatedAt = T0.ToString("O"), LastLogin = T0.ToString("O"),
        });
        await _context.SaveChangesAsync();

        var cache = new MemoryCache(new MemoryCacheOptions());
        var settings = new ProfileSettingsService(_context, NullLogger<ProfileSettingsService>.Instance, cache, TimeProvider.System);
        var reader = new DirectTrackReader(_context);
        var shapeMatcher = new ShapeSegmentMatcher(_context, NullLogger<ShapeSegmentMatcher>.Instance, reader);
        var engine = new MatchEngineService(_context, reader, NullLogger<MatchEngineService>.Instance);
        var effortMath = new EffortMathService(_context, reader, NullLogger<EffortMathService>.Instance);
        _service = SegmentServices.Create(
            _context, cache, new TestVisibilityService(User), new UserCacheInvalidator(), settings,
            shapeMatcher, engine, effortMath, reader).Efforts;
    }

    public async Task DisposeAsync()
    {
        await _context.Database.EnsureDeletedAsync();
        await _context.DisposeAsync();
    }

    // 5 points → ordinals 0..4; lat 47.000 + i·0.001, lng 11.000 (quantization-exact).
    private async Task<int> SeedActivityAsync(TrackId trackId = default, string userId = User)
    {
        if (trackId == default) trackId = new TrackId(1);
        _context.Activities.Add(new Activity { TrackId = trackId, UserId = userId, TrackName = "T" });
        await _context.SaveChangesAsync();
        var pts = new List<GpsData>();
        for (int i = 0; i < 5; i++)
            pts.Add(new GpsData
            {
                DateTimeUtc = T0.AddSeconds(i * 10),
                GpsLat = 47.000 + i * 0.001, GpsLong = 11.000,
                Distance = i * 100, Speed = 1.0 + i, Altitude = 100 + i * 10,
            });
        await TrackSeed.SeedTrackAsync(_context, trackId, pts);
        return trackId.Value;
    }

    private async Task<SegmentId> SeedSegmentAsync()
    {
        var segId = new SegmentId(1);
        _context.Segments.Add(new Segment { SegmentId = segId, UserId = User, SegmentName = "S", SegmentKind = "shapes" });
        await _context.SaveChangesAsync();
        return segId;
    }

    private async Task<GpsInSegmentId> SeedEffortAsync(SegmentId segId, TrackId trackId, int entry, int exit)
    {
        var row = new GpsInSegment
        {
            SegmentId = segId, TrackId = trackId,
            GpsIdEntryCalculated = entry, GpsIdExitCalculated = exit,
        };
        _context.GpsInSegments.Add(row);
        await _context.SaveChangesAsync();
        return row.GpsInSegmentId;
    }

    [Fact]
    public async Task EffortPolylines_SlicesEachEffortWindow_IntoLatLng()
    {
        var track = await SeedActivityAsync();
        var seg   = await SeedSegmentAsync();
        await SeedEffortAsync(seg, new TrackId(track), entry: 0, exit: 1);   // 2 points
        await SeedEffortAsync(seg, new TrackId(track), entry: 2, exit: 4);   // 3 points

        var summary = await _service.GetEffortPolylinesAsync(seg, maxEfforts: 50, maxPointsPerEffort: 100);

        Assert.False(summary.Truncated);
        Assert.Equal(2, summary.Efforts.Count);
        Assert.Equal(2, summary.Efforts[0].Points.Count);
        Assert.Equal(3, summary.Efforts[1].Points.Count);

        // Second effort covers ordinals 2..4 → lat 47.002 .. 47.004 at lng 11.000.
        Assert.Equal(47.002, summary.Efforts[1].Points[0].Lat, 6);
        Assert.Equal(47.004, summary.Efforts[1].Points[^1].Lat, 6);
        Assert.Equal(11.000, summary.Efforts[1].Points[0].Lng, 6);
    }

    [Fact]
    public async Task EffortPolylines_Downsample_CapsPointsPerEffort()
    {
        var track = await SeedActivityAsync();
        var seg   = await SeedSegmentAsync();
        await SeedEffortAsync(seg, new TrackId(track), entry: 0, exit: 4);   // 5 points

        var summary = await _service.GetEffortPolylinesAsync(seg, maxEfforts: 50, maxPointsPerEffort: 3);

        var poly = Assert.Single(summary.Efforts);
        Assert.Equal(3, poly.Points.Count);                    // stride [0,2,4], endpoints pinned
        Assert.Equal(47.000, poly.Points[0].Lat, 6);
        Assert.Equal(47.004, poly.Points[^1].Lat, 6);
    }

    [Fact]
    public async Task EffortPolylines_MaxEfforts_CapsAndFlagsTruncated()
    {
        var track = await SeedActivityAsync();
        var seg   = await SeedSegmentAsync();
        await SeedEffortAsync(seg, new TrackId(track), entry: 0, exit: 4);
        await SeedEffortAsync(seg, new TrackId(track), entry: 0, exit: 3);
        await SeedEffortAsync(seg, new TrackId(track), entry: 1, exit: 4);

        var summary = await _service.GetEffortPolylinesAsync(seg, maxEfforts: 2, maxPointsPerEffort: 100);

        Assert.True(summary.Truncated);
        Assert.Equal(2, summary.Efforts.Count);
    }

    [Fact]
    public async Task EffortPolylines_ExcludedEffort_Omitted()
    {
        var track = await SeedActivityAsync();
        var seg   = await SeedSegmentAsync();
        await SeedEffortAsync(seg, new TrackId(track), entry: 0, exit: 4);   // will be excluded
        await SeedEffortAsync(seg, new TrackId(track), entry: 1, exit: 3);   // survives
        _context.SegmentEffortExclusions.Add(new SegmentEffortExclusion
        {
            SegmentId = seg, TrackId = new TrackId(track), EntryIndex = 0, ExcludedAt = T0.ToString("O"),
        });
        await _context.SaveChangesAsync();

        var summary = await _service.GetEffortPolylinesAsync(seg, maxEfforts: 50, maxPointsPerEffort: 100);

        var poly = Assert.Single(summary.Efforts);
        Assert.Equal(3, poly.Points.Count);                    // the (1,3) effort, not the excluded (0,4)
    }

    [Fact]
    public async Task EffortPolylines_CapCountsOnlyVisibleEfforts()
    {
        // The maxEfforts cut must come AFTER the visibility filter. Cutting first spends the quota on
        // invisible (foreign) efforts that are then filtered away — fewer than maxEfforts polylines with
        // Truncated=true even though enough visible efforts exist. Foreign efforts are seeded FIRST so a
        // pre-filter cut would keep exactly the wrong rows.
        _context.Users.Add(new AppUser
        {
            UserId = "someone-else", DisplayName = "O", Email = "o@t.com", IsAdmin = false,
            CreatedAt = T0.ToString("O"), LastLogin = T0.ToString("O"),
        });
        await _context.SaveChangesAsync();
        var foreign = await SeedActivityAsync(trackId: new TrackId(9), userId: "someone-else");
        var track   = await SeedActivityAsync();
        var seg     = await SeedSegmentAsync();
        await SeedEffortAsync(seg, new TrackId(foreign), entry: 0, exit: 4);
        await SeedEffortAsync(seg, new TrackId(foreign), entry: 1, exit: 4);
        await SeedEffortAsync(seg, new TrackId(track), entry: 0, exit: 1);
        await SeedEffortAsync(seg, new TrackId(track), entry: 1, exit: 3);
        await SeedEffortAsync(seg, new TrackId(track), entry: 2, exit: 4);

        var summary = await _service.GetEffortPolylinesAsync(seg, maxEfforts: 3, maxPointsPerEffort: 100);

        Assert.Equal(3, summary.Efforts.Count);   // all 3 visible efforts — the quota is not spent on foreign rows
        Assert.False(summary.Truncated);          // exactly maxEfforts visible efforts exist → no cut happened
    }

    [Fact]
    public async Task EffortPolylines_UnknownSegment_ReturnsEmpty()
    {
        var summary = await _service.GetEffortPolylinesAsync(new SegmentId(999), maxEfforts: 50, maxPointsPerEffort: 100);
        Assert.Empty(summary.Efforts);
        Assert.False(summary.Truncated);
    }

    [Fact]
    public async Task EffortPolylines_CoordsAreRoundedToWirePrecision()
    {
        // Dequantized doubles (int32 · 1e-6) carry IEEE artifacts like 47.123456999999995 — the wire
        // must round them to GpsPrecision.LatLng like every sibling coordinate wire (gps-summary,
        // TrackPointDto, photo markers). Rounding is idempotent, so this pins "rounded at the wire".
        _context.Activities.Add(new Activity { TrackId = new TrackId(9), UserId = User, TrackName = "T" });
        await _context.SaveChangesAsync();
        var pts = new List<GpsData>();
        for (int i = 0; i < 3; i++)
            pts.Add(new GpsData
            {
                DateTimeUtc = T0.AddSeconds(i * 10),
                GpsLat = 47.123457 + i * 0.000687, GpsLong = 11.876543 + i * 0.000091,
                Distance = i * 100, Speed = 1.0 + i, Altitude = 100,
            });
        await TrackSeed.SeedTrackAsync(_context, new TrackId(9), pts);
        var seg = await SeedSegmentAsync();
        await SeedEffortAsync(seg, new TrackId(9), entry: 0, exit: 2);

        var summary = await _service.GetEffortPolylinesAsync(seg, maxEfforts: 50, maxPointsPerEffort: 100);

        var poly = Assert.Single(summary.Efforts);
        Assert.All(poly.Points, p =>
        {
            Assert.Equal(Math.Round(p.Lat, GpsPrecision.LatLng), p.Lat);
            Assert.Equal(Math.Round(p.Lng, GpsPrecision.LatLng), p.Lng);
        });
    }
}
