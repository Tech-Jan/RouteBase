using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Matching;
using RouteBaseWeb.Services.Segments;
using RouteBaseWeb.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Segments;

/// <summary>
/// SA-01 / SA-02 pins: the two effort read paths must gate on the *segment's own* visibility, not just
/// on the underlying activity's. Cross-user matching means an unseen segment can carry efforts on tracks
/// the caller CAN see (here: the caller's own track matched by another user's segment) — probing that
/// segment id must return empty, not leak effort rows / per-point chart data.
/// </summary>
public class SegmentEffortVisibilityTests : IAsyncLifetime
{
    private const string Owner  = "owner-A";   // segment owner (invisible to the viewer)
    private const string Viewer = "viewer-B";  // current user + owner of the matched track
    private static readonly DateTime T0 = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private RouteBaseDbContext _context = null!;
    private SegmentEfforts _service = null!;

    public async Task InitializeAsync()
    {
        var conn = $"Data Source=file:effvis_{Guid.NewGuid():N}?mode=memory&cache=shared";
        _context = new RouteBaseDbContext(
            new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options);
        await _context.Database.EnsureCreatedAsync();
        await new DbSchemaManager(_context).EnsureTrackContentTablesAsync();

        foreach (var u in new[] { Owner, Viewer })
            _context.Users.Add(new AppUser
            {
                UserId = u, DisplayName = u, Email = $"{u}@t.com", IsAdmin = false,
                CreatedAt = T0.ToString("O"), LastLogin = T0.ToString("O"),
            });
        await _context.SaveChangesAsync();

        var cache        = new MemoryCache(new MemoryCacheOptions());
        var settings     = new ProfileSettingsService(_context, NullLogger<ProfileSettingsService>.Instance, cache, TimeProvider.System);
        var reader       = new DirectTrackReader(_context);
        var shapeMatcher = new ShapeSegmentMatcher(_context, NullLogger<ShapeSegmentMatcher>.Instance, reader);
        var engine       = new MatchEngineService(_context, reader, NullLogger<MatchEngineService>.Instance);
        var effortMath   = new EffortMathService(_context, reader, NullLogger<EffortMathService>.Instance);
        // Current user = Viewer B; VisibleSegments/VisibleActivities scope to B only.
        _service = SegmentServices.Create(
            _context, cache, new TestVisibilityService(Viewer), new UserCacheInvalidator(), settings,
            shapeMatcher, engine, effortMath, reader).Efforts;
    }

    public async Task DisposeAsync()
    {
        await _context.Database.EnsureDeletedAsync();
        await _context.DisposeAsync();
    }

    /// <summary>Seeds a decodable 5-point track owned by <paramref name="userId"/>.</summary>
    private async Task<int> SeedTrackAsync(TrackId trackId, string userId)
    {
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

    private async Task<SegmentId> SeedSegmentAsync(SegmentId segId, string ownerUserId)
    {
        _context.Segments.Add(new Segment { SegmentId = segId, UserId = ownerUserId, SegmentName = "S" });
        await _context.SaveChangesAsync();
        return segId;
    }

    /// <summary>Effort row with full stored aggregates so the detailed path would include it (pre-gate).</summary>
    private async Task<GpsInSegmentId> SeedEffortAsync(SegmentId segId, TrackId trackId)
    {
        var row = new GpsInSegment
        {
            SegmentId = segId, TrackId = trackId,
            GpsIdEntryCalculated = 0, GpsIdExitCalculated = 4,
            EffortDistance = 400, EffortElevation = 40, EffortTime = 40, EffortSpeedAvg = 10,
            SegPerc = 100,
        };
        _context.GpsInSegments.Add(row);
        await _context.SaveChangesAsync();
        return row.GpsInSegmentId;
    }

    // SA-01: efforts-detailed for an unseen segment (owned by A) whose effort references the viewer's own
    // visible track must return empty.
    [Fact]
    public async Task EffortsDetailed_NonVisibleSegment_ReturnsEmpty()
    {
        var track = await SeedTrackAsync(trackId: new TrackId(1), userId: Viewer);
        var seg   = await SeedSegmentAsync(segId: new SegmentId(1), ownerUserId: Owner);
        await SeedEffortAsync(seg, new TrackId(track));

        var result = await _service.GetSegmentEffortsDetailedAsync(seg);
        Assert.Empty(result);
    }

    // Companion: a segment the viewer owns still serves its effort.
    [Fact]
    public async Task EffortsDetailed_VisibleSegment_ReturnsEffort()
    {
        var track = await SeedTrackAsync(trackId: new TrackId(1), userId: Viewer);
        var seg   = await SeedSegmentAsync(segId: new SegmentId(1), ownerUserId: Viewer);
        await SeedEffortAsync(seg, new TrackId(track));

        var result = await _service.GetSegmentEffortsDetailedAsync(seg);
        Assert.Single(result);
    }

    // SA-10: the index-card effort count must exclude efforts on activities invisible to the viewer
    // (mirrors the totals/detailed paths) — here one effort on B's own track, one on A's invisible track.
    [Fact]
    public async Task EffortCounts_ExcludesInvisibleActivities()
    {
        var ownTrack   = await SeedTrackAsync(trackId: new TrackId(1), userId: Viewer);
        var otherTrack = await SeedTrackAsync(trackId: new TrackId(2), userId: Owner);
        var seg        = await SeedSegmentAsync(segId: new SegmentId(1), ownerUserId: Viewer);
        await SeedEffortAsync(seg, new TrackId(ownTrack));
        await SeedEffortAsync(seg, new TrackId(otherTrack));

        var counts = await _service.GetEffortCountsForSegmentsAsync(new List<SegmentId> { seg });
        Assert.Equal(1, counts.GetValueOrDefault(seg));
    }

    // SA-02: performance-data/bulk for an unseen segment must return empty even when the effort's track
    // is visible to the caller.
    [Fact]
    public async Task PerfBulk_NonVisibleSegment_ReturnsEmpty()
    {
        var track = await SeedTrackAsync(trackId: new TrackId(1), userId: Viewer);
        var seg   = await SeedSegmentAsync(segId: new SegmentId(1), ownerUserId: Owner);
        var eff   = await SeedEffortAsync(seg, new TrackId(track));

        var result = await _service.GetPerformanceDataForEffortsAsync(seg, new HashSet<GpsInSegmentId> { eff });
        Assert.Empty(result);
    }

    // Companion: a segment the viewer owns still serves chart data.
    [Fact]
    public async Task PerfBulk_VisibleSegment_StillServes()
    {
        var track = await SeedTrackAsync(trackId: new TrackId(1), userId: Viewer);
        var seg   = await SeedSegmentAsync(segId: new SegmentId(1), ownerUserId: Viewer);
        var eff   = await SeedEffortAsync(seg, new TrackId(track));

        var result = await _service.GetPerformanceDataForEffortsAsync(seg, new HashSet<GpsInSegmentId> { eff });
        Assert.Single(result);
    }
}
