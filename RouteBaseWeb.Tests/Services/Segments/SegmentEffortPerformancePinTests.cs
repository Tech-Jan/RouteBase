using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Matching;
using RouteBaseWeb.Services.Segments;
using RouteBaseWeb.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Segments;

/// <summary>
/// TE-37 characterization pin: <see cref="SegmentEfforts.GetPerformanceDataForEffortsAsync"/> now
/// slices each effort's GPS_ID window out of an <see cref="Services.Tracks.DecodedTrack"/> (via the
/// injected ITrackReader) instead of the OR'd multi-window SQL. Fixture values are quantization-exact
/// (6-decimal coords, whole seconds, 0.01 speeds, integer distances) so the expected numbers are
/// bit-identical whether the reader is rows-v1 (this batch) or blob-backed (post-flip).
/// </summary>
public class SegmentEffortPerformancePinTests : IAsyncLifetime
{
    private const string User = "test-user";
    private static readonly DateTime T0 = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private RouteBaseDbContext _context = null!;
    private SegmentEfforts _service = null!;

    public async Task InitializeAsync()
    {
        var conn = $"Data Source=file:efforts_{Guid.NewGuid():N}?mode=memory&cache=shared";
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

    private async Task<int> SeedActivityAsync()
    {
        TrackId trackId = new TrackId(1);
        _context.Activities.Add(new Activity { TrackId = trackId, UserId = User, TrackName = "T" });
        await _context.SaveChangesAsync();
        // 5 points → ordinals 0..4, 10 s apart, 100 m / 1 m·s⁻¹ increments.
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
        _context.Segments.Add(new Segment { SegmentId = segId, UserId = User, SegmentName = "S" });
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

    // Effort over ordinals 1..3 (the 2nd..4th points) → 3 points; distances/times relative to that window's start.
    [Fact]
    public async Task PerformanceData_SlicesEffortWindow_ByOrdinalRange()
    {
        var track = await SeedActivityAsync();
        var seg   = await SeedSegmentAsync();
        var eff   = await SeedEffortAsync(seg, new TrackId(track), entry: 1, exit: 3);

        var data = await _service.GetPerformanceDataForEffortsAsync(seg, new HashSet<GpsInSegmentId> { eff });

        var epd = Assert.Single(data);
        Assert.Equal(3, epd.Points.Distance.Length);

        // window start (ord1): baseline; window end (ord3): dist 300-100=200, time (30-10)=20 s.
        Assert.Equal(0,   epd.Points.Distance[0]);
        Assert.Equal(0,   epd.Points.Time[0]);
        Assert.Equal(200, epd.Points.Distance[^1]);
        Assert.Equal(20,  epd.Points.Time[^1]);

        // Speed km/h = m·s⁻¹ × 3.6, rounded to 2 dp (ord1 speed 2.0 → 7.2; ord3 speed 4.0 → 14.4).
        Assert.Equal(7.2,  epd.Points.Speed[0]);
        Assert.Equal(14.4, epd.Points.Speed[^1]);

        Assert.Equal(20, epd.ElapsedTime);
    }

    // B3: ActivityDate goes out as day-at-midnight wall-clock ISO — bare yyyy-MM-dd parses as UTC
    // midnight in the browser and shifted a day for UTC-minus viewers.
    [Fact]
    public async Task PerformanceData_ActivityDate_IsWallClockMidnightIso()
    {
        var track = await SeedActivityAsync();
        var seg   = await SeedSegmentAsync();
        var eff   = await SeedEffortAsync(seg, new TrackId(track), entry: 0, exit: 4);
        var act = _context.Activities.First(a => a.TrackId == new TrackId(track));
        act.StartDateTimeLocal = new DateTime(2026, 7, 1, 14, 5, 30, DateTimeKind.Unspecified);
        await _context.SaveChangesAsync();

        var data = await _service.GetPerformanceDataForEffortsAsync(seg, new HashSet<GpsInSegmentId> { eff });

        Assert.Equal("2026-07-01T00:00:00", Assert.Single(data).ActivityDate);
    }

    // Whole-track effort (ordinals 0..4) → all 5 points, elapsed 40 s.
    [Fact]
    public async Task PerformanceData_FullTrackWindow_ReturnsAllPoints()
    {
        var track = await SeedActivityAsync();
        var seg   = await SeedSegmentAsync();
        var eff   = await SeedEffortAsync(seg, new TrackId(track), entry: 0, exit: 4);

        var data = await _service.GetPerformanceDataForEffortsAsync(seg, new HashSet<GpsInSegmentId> { eff });

        var epd = Assert.Single(data);
        Assert.Equal(5, epd.Points.Distance.Length);
        Assert.Equal(400, epd.Points.Distance[^1]);
        Assert.Equal(40,  epd.ElapsedTime);
    }

    // Empty result set → empty list (no throw), effort id not resolvable.
    [Fact]
    public async Task PerformanceData_UnknownEffort_ReturnsEmpty()
    {
        var seg = await SeedSegmentAsync();
        var data = await _service.GetPerformanceDataForEffortsAsync(seg, new HashSet<GpsInSegmentId> { new(999) });
        Assert.Empty(data);
    }

    // SE-01: a coordinate-less point inside the window (with the Distance channel absent → haversine
    // fallback) must NOT anchor a leg to Null Island (0,0); it contributes zero distance instead.
    [Fact]
    public async Task PerformanceData_NullCoordinatePoint_DoesNotExplodeDistance()
    {
        int track = 1;
        _context.Activities.Add(new Activity { TrackId = new TrackId(track), UserId = User, TrackName = "T" });
        await _context.SaveChangesAsync();
        var pts = new List<GpsData>();
        for (int i = 0; i < 5; i++)
            pts.Add(new GpsData
            {
                DateTimeUtc = T0.AddSeconds(i * 10),
                GpsLat  = i == 2 ? null : 47.000 + i * 0.001,   // point 2 has no coordinates
                GpsLong = i == 2 ? null : 11.000,
                Distance = null, Speed = 1.0 + i, Altitude = 100 + i * 10,   // no Distance → haversine path
            });
        await TrackSeed.SeedTrackAsync(_context, new TrackId(track), pts);
        var seg = await SeedSegmentAsync();
        var eff = await SeedEffortAsync(seg, new TrackId(track), entry: 0, exit: 4);

        var epd = Assert.Single(await _service.GetPerformanceDataForEffortsAsync(seg, new HashSet<GpsInSegmentId> { eff }));

        // Real legs are ~111 m each (two of the four legs touch the null point → zero); pre-fix a Null
        // Island leg added thousands of km. Assert bounded and monotone non-decreasing.
        Assert.True(epd.Points.Distance[^1] < 1000, $"cumulative distance {epd.Points.Distance[^1]} exploded");
        for (int i = 1; i < epd.Points.Distance.Length; i++)
            Assert.True(epd.Points.Distance[i] >= epd.Points.Distance[i - 1], "distance must stay monotone");
    }

    // PS-01: a budget of 3 over the 5-point effort keeps stride indices [0,2,4] with byte-identical
    // values to the full-res points at those indices; budget 0 keeps all 5 and is not truncated.
    [Fact]
    public async Task PerformanceData_Downsampled_ValuesMatchFullResAtKeptIndices()
    {
        var track = await SeedActivityAsync();
        var seg   = await SeedSegmentAsync();
        var eff   = await SeedEffortAsync(seg, new TrackId(track), entry: 0, exit: 4);

        var full = Assert.Single(await _service.GetPerformanceDataForEffortsAsync(
            seg, new HashSet<GpsInSegmentId> { eff }, maxPointsPerEffort: 0));
        Assert.False(full.PointsTruncated);
        Assert.Equal(5, full.Points.Distance.Length);

        var down = Assert.Single(await _service.GetPerformanceDataForEffortsAsync(
            seg, new HashSet<GpsInSegmentId> { eff }, maxPointsPerEffort: 3));
        Assert.True(down.PointsTruncated);
        Assert.Equal(3, down.Points.Distance.Length);

        // Kept points equal full-res points at stride indices [0, 2, 4] — exact columnar values.
        int[] keptIdx = [0, 2, 4];
        for (int k = 0; k < keptIdx.Length; k++)
        {
            int src = keptIdx[k];
            Assert.Equal(full.Points.Distance[src], down.Points.Distance[k]);
            Assert.Equal(full.Points.Time[src],     down.Points.Time[k]);
            Assert.Equal(full.Points.Speed[src],    down.Points.Speed[k]);
            Assert.Equal(full.Points.Altitude![src], down.Points.Altitude![k]);
        }
    }
}
