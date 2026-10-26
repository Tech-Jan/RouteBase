using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Heatmaps;
using RouteBaseWeb.Services.Matching;
using RouteBaseWeb.Services.Segments;
using RouteBaseWeb.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// New whole-track segment removal seam (A2 §5.4): deletes every GPSinSegment row for a track, returns the
/// affected segment ids, marks their done-heatmaps stale, and KEEPS the SegmentEffortExclusion rows
/// (pair-level user intent survives an edit of the same track id).
/// </summary>
public class SegmentAnalysisRemoveTrackTests : IAsyncLifetime
{
    private const string Me = "test-user";

    private RouteBaseDbContext _context = null!;
    private SegmentAnalysis _analysis = null!;
    private RecordingHeatmapLifecycle _heatmaps = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>()
            .UseSqlite($"Data Source=file:segremove_{Guid.NewGuid():N}?mode=memory&cache=shared").Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.OpenConnectionAsync();
        await _context.Database.EnsureCreatedAsync();

        var cache = new MemoryCache(new MemoryCacheOptions());
        var vis = new TestVisibilityService(Me);
        var segCache = new SegmentCache(cache, vis, new UserCacheInvalidator());
        var settings = new ProfileSettingsService(_context, NullLogger<ProfileSettingsService>.Instance, cache, TimeProvider.System);
        var reader = new DirectTrackReader(_context);
        _heatmaps = new RecordingHeatmapLifecycle();
        _analysis = new SegmentAnalysis(
            _context, NullLogger<SegmentAnalysis>.Instance, vis, segCache,
            new ShapeSegmentMatcher(_context, NullLogger<ShapeSegmentMatcher>.Instance, reader),
            new MatchEngineService(_context, reader, NullLogger<MatchEngineService>.Instance),
            new EffortMathService(_context, reader, NullLogger<EffortMathService>.Instance),
            reader, _heatmaps, settings);

        _context.Users.Add(new AppUser { UserId = Me, DisplayName = Me, Email = $"{Me}@t.com", IsAdmin = false });
        _context.Activities.AddRange(
            new Activity { TrackId = new TrackId(7), UserId = Me, ActivityType = "Ride" },
            new Activity { TrackId = new TrackId(8), UserId = Me, ActivityType = "Ride" });
        _context.Segments.AddRange(
            new Segment { SegmentId = new SegmentId(1), SegmentName = "S1", UserId = Me, ActivityType = "Ride" },
            new Segment { SegmentId = new SegmentId(2), SegmentName = "S2", UserId = Me, ActivityType = "Ride" });
        await _context.SaveChangesAsync();

        // track 7 crosses segments 1 & 2; track 8 also crosses segment 1 (must survive the wipe of track 7).
        _context.GpsInSegments.AddRange(
            new GpsInSegment { TrackId = new TrackId(7), SegmentId = new SegmentId(1) },
            new GpsInSegment { TrackId = new TrackId(7), SegmentId = new SegmentId(1) },
            new GpsInSegment { TrackId = new TrackId(7), SegmentId = new SegmentId(2) },
            new GpsInSegment { TrackId = new TrackId(8), SegmentId = new SegmentId(1) });
        _context.SegmentEffortExclusions.Add(new SegmentEffortExclusion
        {
            SegmentId = new SegmentId(1), TrackId = new TrackId(7), EntryIndex = 0, ExcludedAt = DateTime.UtcNow.ToString("O"),
        });
        await _context.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.Database.CloseConnectionAsync();
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task RemoveTrack_DeletesAllRows_ReturnsAffectedSegments()
    {
        var affected = await _analysis.RemoveTrackFromSegmentsAsync(new TrackId(7), Me);

        Assert.Equal(new[] { new SegmentId(1), new SegmentId(2) }, affected.OrderBy(x => x));
        Assert.Empty(await _context.GpsInSegments.Where(g => g.TrackId == new TrackId(7)).ToListAsync());
        Assert.Single(await _context.GpsInSegments.Where(g => g.TrackId == new TrackId(8)).ToListAsync()); // untouched
    }

    [Fact]
    public async Task RemoveTrack_MarksAffectedHeatmapsStale()
    {
        await _analysis.RemoveTrackFromSegmentsAsync(new TrackId(7), Me);

        Assert.Equal(new[] { new SegmentId(1), new SegmentId(2) }, _heatmaps.MarkedStale.OrderBy(x => x));
    }

    [Fact]
    public async Task RemoveTrack_KeepsEffortExclusions()
    {
        await _analysis.RemoveTrackFromSegmentsAsync(new TrackId(7), Me);

        Assert.True(await _context.SegmentEffortExclusions.AnyAsync(e => e.TrackId == new TrackId(7) && e.SegmentId == new SegmentId(1)));
    }

    /// <summary>Records MarkStaleAsync calls; other lifecycle members are never reached by this seam.</summary>
    private sealed class RecordingHeatmapLifecycle : IHeatmapLifecycle
    {
        public readonly List<SegmentId> MarkedStale = new();
        public Task WithdrawAsync(SegmentId segmentId, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkStaleAsync(SegmentId segmentId, CancellationToken ct = default)
        {
            MarkedStale.Add(segmentId);
            return Task.CompletedTask;
        }

        public Task MarkStaleForTrackAsync(TrackId trackId, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<HeatmapAttachResult> AttachAsync(SegmentId segmentId, IEnumerable<string> activityTypes) =>
            throw new NotImplementedException();
        public Task FailPendingAsync(SegmentId segmentId, string error) => throw new NotImplementedException();
        public Task<Heatmap?> GetVisibleAsync(SegmentId segmentId, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<int?> CaptureIdForSegmentAsync(SegmentId segmentId, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public void PurgeTiles(int heatmapId) => throw new NotImplementedException();
    }
}
