using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Lifecycle;
using RouteBaseWeb.Services.Matching;
using RouteBaseWeb.Services.Photos;
using RouteBaseWeb.Services.Segments;
using RouteBaseWeb.Services.Segments.Rules;
using RouteBaseWeb.Services.TrackEdit;
using RouteBaseWeb.Services.Tracks;
using RouteBaseWeb.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Pipeline;

/// <summary>
/// Approving an auto-detected ride actually puts it on its segments — asserted on the EFFORT ROWS, not on
/// the enqueue.
///
/// Every other test of this path stops at a fake derive queue and asserts only that something was queued
/// (<c>AutoReviewServiceTests.Confirm_EnqueuesMatchForTheApprovedTrack</c>,
/// <c>RecordingPipelineTests.SegmentMatch</c>). That left the whole span from the queue payload to
/// <c>GPSinSegment</c> untested, and a user-visible gap lived there: approved rides reached no segment
/// until a manual "Analyze All" was run.
///
/// So this runs the REAL chain — AutoReviewService → the real TrackDeriveQueue → the real OperationQueue
/// payload → TrackEditDeriveService → SegmentAnalysis — and reads the rows back. Only the queue WORKER is
/// absent (drained inline here); everything it would dispatch to is genuine.
/// </summary>
public class ApproveSegmentMatchTests : IAsyncLifetime
{
    private const string User = "test-user";

    private RouteBaseDbContext _context = null!;
    private AutoReviewService _review = null!;
    private TrackEditDeriveService _derive = null!;
    private OperationQueue _queue = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>()
            .UseSqlite($"Data Source=file:approvematch_{Guid.NewGuid():N}?mode=memory&cache=shared")
            .Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();

        var schema = new DbSchemaManager(_context);
        await schema.EnsureActivityBboxTableAsync();
        await schema.EnsureTrackContentTablesAsync();
        await schema.EnsureIndexesAsync();

        _context.Users.Add(new AppUser
        {
            UserId = User, DisplayName = "Test User", Email = "test@test.com", IsAdmin = false,
            CreatedAt = DateTime.UtcNow.ToString("O"), LastLogin = DateTime.UtcNow.ToString("O"),
        });
        await _context.SaveChangesAsync();

        // The production service graph, mirroring DI wiring (SegmentServices.Create is the shared factory).
        var visibility  = new TestVisibilityService(User);
        var invalidator = new UserCacheInvalidator();
        var cache       = new MemoryCache(new MemoryCacheOptions());
        var settings    = new ProfileSettingsService(_context, NullLogger<ProfileSettingsService>.Instance, cache, TimeProvider.System);
        var trackReader = new DirectTrackReader(_context);
        var shapeMatcher = new ShapeSegmentMatcher(_context, NullLogger<ShapeSegmentMatcher>.Instance, trackReader);
        var engine      = new MatchEngineService(_context, trackReader, NullLogger<MatchEngineService>.Instance);
        var effortMath  = new EffortMathService(_context, trackReader, NullLogger<EffortMathService>.Instance);
        var analysis    = SegmentServices.Create(
            _context, cache, visibility, invalidator, settings,
            shapeMatcher, engine, effortMath, trackReader).Analysis;

        // The REAL queue + enqueue seam: this is where the payload buckets (MatchOnlyIds vs FullDeriveIds)
        // are actually decided, so a mis-bucketed enqueue has to be able to fail this test.
        _queue = new OperationQueue(TimeProvider.System);
        var derives = new TrackDeriveQueue(_queue, NullLogger<TrackDeriveQueue>.Instance);

        var lifecycle = TestLifecycle.For(_context, visibility, invalidator);
        var command   = new ActivityCommandService(
            _context, visibility, invalidator, NullLogger<ActivityCommandService>.Instance, lifecycle);

        _review = new AutoReviewService(
            _context, visibility, lifecycle, command, derives, settings,
            NullLogger<AutoReviewService>.Instance, TimeProvider.System);

        _derive = new TrackEditDeriveService(
            analysis, new NoOpPhotoCommandService(), invalidator,
            NullLogger<TrackEditDeriveService>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _context.Database.EnsureDeletedAsync();
        await _context.DisposeAsync();
    }

    // ── The contract ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Approve_PutsTheRideOnItsPolylineSegment()
    {
        var segId  = await SeedPolylineSegmentAsync();
        var trackId = await SeedPendingAutoRecordingAsync();

        Assert.True(await _review.ConfirmAsync(trackId, "Ride"));
        await RunQueuedDerivesAsync(expected: 1);

        Assert.NotEmpty(await EffortsAsync(segId, trackId));
    }

    /// <summary>
    /// The same for a shape/rule segment. Shape segments are matched by <see cref="ShapeSegmentMatcher"/>
    /// rather than by the polyline engine, and the incremental path used to skip them outright — so an
    /// approved ride never reached them, which for a user whose segments are mostly shapes reads as
    /// "approving does nothing at all".
    /// </summary>
    [Fact]
    public async Task Approve_PutsTheRideOnItsShapeSegment()
    {
        var segId   = await SeedShapeSegmentAsync();
        var trackId = await SeedPendingAutoRecordingAsync();

        Assert.True(await _review.ConfirmAsync(trackId, "Ride"));
        await RunQueuedDerivesAsync(expected: 1);

        Assert.NotEmpty(await EffortsAsync(segId, trackId));
    }

    /// <summary>Bulk approve is one queue item for the whole batch — every ride in it must still land.</summary>
    [Fact]
    public async Task BulkApprove_PutsEveryRideOnBothSegmentKinds()
    {
        var polyId  = await SeedPolylineSegmentAsync();
        var shapeId = await SeedShapeSegmentAsync();
        var first   = await SeedPendingAutoRecordingAsync();
        var second  = await SeedPendingAutoRecordingAsync();

        Assert.Equal(2, await _review.ConfirmManyAsync(new[] { first, second }));
        await RunQueuedDerivesAsync(expected: 1);   // ONE item carrying both ids

        foreach (var trackId in new[] { first, second })
        {
            Assert.NotEmpty(await EffortsAsync(polyId, trackId));
            Assert.NotEmpty(await EffortsAsync(shapeId, trackId));
        }
    }

    /// <summary>A ride that never left the review queue must not be matched — approve is the gate, and a
    /// pending ride's efforts would be painted into regenerated heatmaps (which filter by user, not
    /// lifecycle).</summary>
    [Fact]
    public async Task PendingRide_IsNotMatched()
    {
        var segId = await SeedPolylineSegmentAsync();
        var trackId = await SeedPendingAutoRecordingAsync();

        Assert.Equal(0, _queue.PendingCountFor(User));
        Assert.Empty(await EffortsAsync(segId, trackId));
    }

    // ── Harness ──────────────────────────────────────────────────────────────────────────────────────

    // Drain the queue inline, running each item through the real derive service. Stands in for
    // OperationQueueWorker, which only resolves the handler and dispatches.
    private async Task RunQueuedDerivesAsync(int expected)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int seen = 0;
        await foreach (var item in _queue.ReadAllAsync(cts.Token))
        {
            Assert.Equal(OperationQueue.OperationType.TrackEditDerive, item.Type);
            var payload = Assert.IsType<TrackEditDerivePayload>(item.Payload);
            await _derive.RunAsync(payload, CancellationToken.None);
            if (++seen >= expected) break;
        }
        Assert.Equal(expected, seen);
    }

    private Task<List<GpsInSegment>> EffortsAsync(SegmentId segId, TrackId trackId) =>
        _context.GpsInSegments.AsNoTracking()
            .Where(e => e.SegmentId == segId && e.TrackId == trackId).ToListAsync();

    // ── Seeds ────────────────────────────────────────────────────────────────────────────────────────
    // One corridor, lat 47.000..47.019 @ lng 11.000, shared by every seeded ride and both segments.

    private static (double lat, double lng)[] Corridor()
    {
        var pts = new (double lat, double lng)[20];
        for (int i = 0; i < 20; i++) pts[i] = (47.000 + i * 0.001, 11.000);
        return pts;
    }

    /// <summary>A finalized auto-detected ride sitting in the review queue: in-work / awaiting-review,
    /// geometry and bbox already final (Seam-2 holds from finalize).</summary>
    private async Task<TrackId> SeedPendingAutoRecordingAsync()
    {
        var points = Corridor();
        var activity = new Activity
        {
            UserId       = User,
            ActivityType = "Ride",
            Source       = ActivitySources.AutoRecording,
            Lifecycle    = LifecycleStates.InWork,
            LifecycleReason = LifecycleReasons.AwaitingReview,
            Visibility   = "private",
            StartGpsLat  = points[0].lat,
            StartGpsLong = points[0].lng,
            EndGpsLat    = points[^1].lat,
            EndGpsLong   = points[^1].lng,
        };
        _context.Activities.Add(activity);
        await _context.SaveChangesAsync();
        TrackId trackId = activity.TrackId;

        await TrackSeed.SeedTrackAsync(
            _context, trackId,
            points.Select(p => new GpsData { GpsLat = p.lat, GpsLong = p.lng }).ToList());

        await _context.Database.ExecuteSqlRawAsync(
            @"INSERT OR IGNORE INTO Activity_Bbox (track_ID, min_lat, max_lat, min_long, max_long)
              VALUES ({0}, {1}, {2}, {3}, {4})",
            trackId.Value,
            points.Min(p => p.lat), points.Max(p => p.lat),
            points.Min(p => p.lng), points.Max(p => p.lng));

        return trackId;
    }

    // Polyline segment over the middle of the corridor (points 6..13).
    private async Task<SegmentId> SeedPolylineSegmentAsync()
    {
        var pts = new (double lat, double lng)[8];
        for (int i = 0; i < 8; i++) pts[i] = (47.000 + (6 + i) * 0.001, 11.000);

        double dist = 0;
        for (int i = 1; i < pts.Length; i++)
            dist += Haversine(pts[i - 1], pts[i]);

        var segment = new Segment { UserId = User, SegmentDistance = dist };
        _context.Segments.Add(segment);
        await _context.SaveChangesAsync();

        foreach (var (lat, lng) in pts)
            _context.SegmentGps.Add(new SegmentGps { SegmentId = segment.SegmentId, GpsLat = lat, GpsLong = lng });
        await _context.SaveChangesAsync();
        return segment.SegmentId;
    }

    // Shape segment: one zone covering the whole corridor, membership = passes that zone. No Segment_GPS —
    // shape segments carry a rule document instead of a polyline.
    private async Task<SegmentId> SeedShapeSegmentAsync()
    {
        var rules = new RuleDocument
        {
            Shapes = new()
            {
                new ShapeDef
                {
                    No = 1, Kind = "zone",
                    Geom = new RectGeom { South = 46.999, West = 10.999, North = 47.020, East = 11.001 },
                },
            },
            Membership = { new PassesAllRule { Zones = { 1 } } },
        };

        var segment = new Segment
        {
            UserId = User, SegmentKind = SegmentKinds.Shapes, MatchRulesJson = rules.Serialize(),
        };
        _context.Segments.Add(segment);
        await _context.SaveChangesAsync();
        return segment.SegmentId;
    }

    private static double Haversine((double lat, double lng) a, (double lat, double lng) b)
    {
        const double R = 6371000;
        double dLat = (b.lat - a.lat) * Math.PI / 180;
        double dLng = (b.lng - a.lng) * Math.PI / 180;
        double h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                 + Math.Cos(a.lat * Math.PI / 180) * Math.Cos(b.lat * Math.PI / 180)
                 * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return 2 * R * Math.Asin(Math.Sqrt(h));
    }

    /// <summary>Photos are a sibling concern of the derive chain and not under test here.</summary>
    private sealed class NoOpPhotoCommandService : IPhotoCommandService
    {
        public Task<PhotoCommandOutcome> PlaceAsync(PhotoId photoId, double lat, double lng, CancellationToken ct = default)
            => Task.FromResult(PhotoCommandOutcome.Ok);
        public Task<PhotoCommandOutcome> UnplaceAsync(PhotoId photoId, CancellationToken ct = default)
            => Task.FromResult(PhotoCommandOutcome.Ok);
        public Task<RestoreResult> RestoreAsync(PhotoId photoId, CancellationToken ct = default)
            => Task.FromResult(new RestoreResult(PhotoCommandOutcome.Ok, null, null));
        public Task<PhotoCommandOutcome> DeleteAsync(PhotoId photoId, CancellationToken ct = default)
            => Task.FromResult(PhotoCommandOutcome.Ok);
        public Task<int> DeleteManyAsync(IReadOnlyCollection<int> photoIds, CancellationToken ct = default)
            => Task.FromResult(0);
        public Task<int> RematchTrackPhotosAsync(TrackId trackId, CancellationToken ct = default)
            => Task.FromResult(0);
    }
}
