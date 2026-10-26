using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Matching;
using RouteBaseWeb.Services.Segments;
using RouteBaseWeb.Services.Tracks;
using RouteBaseWeb.Tests;
using RouteBaseWeb.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Pipeline;

/// <summary>
/// Integration tests for the segment analysis pipeline.
/// Uses a real in-memory SQLite database (not the EF in-memory provider)
/// so SQL window functions, temp tables, and raw SQL all work as in production.
///
/// Coordinate design notes:
///   - Activity tracks: 20 GPS points, lat = 47.000 + i*0.001 (i=0..19). 0.001 deg step ~ 111 m.
///   - Segment AB: 8 GPS points at lat = 47.006..47.013. These align with activity GPS IDs 7-14
///     (auto-generated sequentially), giving a GPS-ID span of 7 which satisfies
///     Stage 6's minimum-span gate MAX(5, CAST(8 * 0.1 AS INT)) = 5.
///   - TrackB is offset 0.0001 deg east (within Stage 4's 0.00015 deg start_end_tight threshold).
/// </summary>
public class SegmentAnalysisPipelineTests : IAsyncLifetime
{
    private RouteBaseDbContext _context = null!;
    private ISegmentAnalysis _segmentService = null!;
    private IDbSchemaManager _schemaManager = null!;

    // 20-point activity track; optional longitude offset for the second track.
    private static (double lat, double lng)[] BuildTrack(double lngOffset = 0.0)
    {
        var pts = new (double lat, double lng)[20];
        for (int i = 0; i < 20; i++)
            pts[i] = (47.000 + i * 0.001, 11.000 + lngOffset);
        return pts;
    }

    // Segment covering 'count' GPS points starting at index 'startIdx' of the track above.
    private static (double lat, double lng)[] BuildSegment(int startIdx, int count = 8)
    {
        var pts = new (double lat, double lng)[count];
        for (int i = 0; i < count; i++)
            pts[i] = (47.000 + (startIdx + i) * 0.001, 11.000);
        return pts;
    }

    // -------------------------------------------------------------------------

    public async Task InitializeAsync()
    {
        var dbName = $"testdb_{Guid.NewGuid():N}";
        var connectionString = $"Data Source=file:{dbName}?mode=memory&cache=shared";

        var options = new DbContextOptionsBuilder<RouteBaseDbContext>()
            .UseSqlite(connectionString)
            .Options;

        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();

        _schemaManager = new DbSchemaManager(_context);
        await _schemaManager.EnsureActivityBboxTableAsync();
        await _schemaManager.EnsureTrackContentTablesAsync();
        await _schemaManager.EnsureIndexesAsync();

        // Seed a user row to satisfy the Activity.user_id FK constraint.
        _context.Users.Add(new AppUser
        {
            UserId      = "test-user",
            DisplayName = "Test User",
            Email       = "test@test.com",
            IsAdmin     = false,
            CreatedAt   = DateTime.UtcNow.ToString("O"),
            LastLogin   = DateTime.UtcNow.ToString("O"),
        });
        await _context.SaveChangesAsync();

        var cache = new MemoryCache(new MemoryCacheOptions());
        var settings = new ProfileSettingsService(
            _context, NullLogger<ProfileSettingsService>.Instance, cache, TimeProvider.System);
        // Cutover: engine-wired construction — these pins now exercise the in-memory engine (the SQL
        // pipeline is deleted in a later commit). One shared DirectTrackReader across both engines.
        var trackReader = new DirectTrackReader(_context);
        var shapeMatcher = new ShapeSegmentMatcher(_context, NullLogger<ShapeSegmentMatcher>.Instance, trackReader);
        var engine = new MatchEngineService(_context, trackReader, NullLogger<MatchEngineService>.Instance);
        var effortMath = new EffortMathService(_context, trackReader, NullLogger<EffortMathService>.Instance);
        _segmentService = SegmentServices.Create(
            _context, cache, new TestVisibilityService(), new UserCacheInvalidator(), settings,
            shapeMatcher, engine, effortMath, trackReader).Analysis;
    }

    public async Task DisposeAsync()
    {
        await _context.Database.EnsureDeletedAsync();
        await _context.DisposeAsync();
    }

    // -------------------------------------------------------------------------

    [Fact]
    public async Task AnalyzeSegment_DetectsEffortsOnBothMatchingTracks()
    {
        // Arrange: two 20-point tracks through the same corridor; segment covers GPS IDs 7-14.
        var trackA = await SeedActivityAsync(BuildTrack(lngOffset: 0.0));
        var trackB = await SeedActivityAsync(BuildTrack(lngOffset: 0.0001)); // within start_end_tight threshold
        var segId  = await SeedSegmentAsync(BuildSegment(startIdx: 6, count: 8));

        // Act
        await _segmentService.AnalyzeSegmentMatchesAsync(segId);

        // Assert: both tracks produce one effort each.
        var efforts = await _context.GpsInSegments
            .Where(e => e.SegmentId == segId)
            .ToListAsync();

        Assert.Equal(2, efforts.Count);
        Assert.Contains(efforts, e => e.TrackId == new TrackId(trackA));
        Assert.Contains(efforts, e => e.TrackId == new TrackId(trackB));
    }

    [Fact]
    public async Task AnalyzeSegment_NoEfforts_WhenTrackDoesNotCrossSegment()
    {
        // Arrange: activity far from the segment (different geo-cells, no proximity match).
        await SeedActivityAsync(
        [
            (48.000, 12.000),
            (48.001, 12.000),
            (48.002, 12.000),
        ]);
        var segId = await SeedSegmentAsync(BuildSegment(startIdx: 6, count: 8));

        // Act
        await _segmentService.AnalyzeSegmentMatchesAsync(segId);

        // Assert
        var count = await _context.GpsInSegments.CountAsync(e => e.SegmentId == segId);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task AnalyzeSegment_ReplacesExistingEffortsOnReRun()
    {
        // Arrange
        await SeedActivityAsync(BuildTrack());
        var segId = await SeedSegmentAsync(BuildSegment(startIdx: 6, count: 8));

        // First run
        await _segmentService.AnalyzeSegmentMatchesAsync(segId);
        var firstCount = await _context.GpsInSegments.CountAsync(e => e.SegmentId == segId);
        Assert.True(firstCount > 0, "First analysis should produce at least one effort");

        // Act: re-analyze the same segment
        await _segmentService.AnalyzeSegmentMatchesAsync(segId);
        var secondCount = await _context.GpsInSegments.CountAsync(e => e.SegmentId == segId);

        // Assert: no duplicate efforts introduced by a second run
        Assert.Equal(firstCount, secondCount);
    }

    [Fact]
    public async Task AnalyzeAllSegments_FindsEffortsForAllSegments()
    {
        // Arrange: one 20-point track passing through two non-overlapping segments.
        await SeedActivityAsync(BuildTrack());

        var seg1Id = await SeedSegmentAsync(BuildSegment(startIdx: 1, count: 8));  // aligns with GPS IDs ~2-9
        var seg2Id = await SeedSegmentAsync(BuildSegment(startIdx: 11, count: 8)); // aligns with GPS IDs ~12-19

        // Act: "Analyze All" = per-segment loop, as done by OperationQueueWorker.RunAnalysisAsync
        // (the segmentId = 0 batch mode was removed in fdb93c6).
        await _segmentService.ClearAllSegmentMatchesAsync("test-user");
        await _segmentService.AnalyzeSegmentMatchesAsync(seg1Id);
        await _segmentService.AnalyzeSegmentMatchesAsync(seg2Id);

        // Assert
        var seg1Count = await _context.GpsInSegments.CountAsync(e => e.SegmentId == seg1Id);
        var seg2Count = await _context.GpsInSegments.CountAsync(e => e.SegmentId == seg2Id);

        Assert.True(seg1Count > 0, $"Segment {seg1Id} should have at least one effort");
        Assert.True(seg2Count > 0, $"Segment {seg2Id} should have at least one effort");
    }

    // SB-02: the "Analyze All" safety-net wipe now runs AFTER the loop and only for unprocessed segments,
    // so a mid-run cancel (a normal UI action) never zeroes a not-yet-reached segment's prior efforts.
    [Fact]
    public async Task AnalyzeAllSegments_CancelledMidRun_UnprocessedSegmentsRetainPriorEfforts()
    {
        var track = await SeedActivityAsync(BuildTrack());
        var seg1  = await SeedSegmentAsync(BuildSegment(startIdx: 1, count: 8));
        var seg2  = await SeedPolylineSegmentForUserAsync("test-user");
        var seg3  = await SeedPolylineSegmentForUserAsync("test-user");

        // Prior efforts on the two segments the loop won't reach.
        foreach (var s in new[] { seg2, seg3 })
            _context.GpsInSegments.Add(new GpsInSegment { TrackId = new TrackId(track), SegmentId = s });
        await _context.SaveChangesAsync();

        // Mirror OperationQueueWorker.RunAnalysisAsync's NEW body: per-segment loop, orphan-clear AFTER it.
        var cts       = new CancellationTokenSource();
        var order     = new[] { seg1, seg2, seg3 };
        var processed = new List<SegmentId>();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            foreach (var s in order)
            {
                cts.Token.ThrowIfCancellationRequested();
                await _segmentService.AnalyzeSegmentMatchesAsync(s, "test-user", ct: cts.Token);
                processed.Add(s);
                cts.Cancel(); // user clicks Cancel after the first segment
            }
            await _segmentService.ClearOrphanedSegmentMatchesAsync("test-user", processed, cts.Token);
        });

        // The wipe never ran → unreached segments keep prior efforts (pre-fix upfront wipe zeroed them).
        Assert.Equal(1, await _context.GpsInSegments.CountAsync(g => g.SegmentId == seg2));
        Assert.Equal(1, await _context.GpsInSegments.CountAsync(g => g.SegmentId == seg3));
    }

    // SB-02: the narrowed orphan clear deletes efforts only for segments NOT in the processed list.
    [Fact]
    public async Task ClearOrphanedSegmentMatches_DeletesOnlyUnprocessedSegments()
    {
        var track = await SeedActivityAsync(BuildTrack());
        var seg1  = await SeedPolylineSegmentForUserAsync("test-user");
        var seg2  = await SeedPolylineSegmentForUserAsync("test-user");
        var seg3  = await SeedPolylineSegmentForUserAsync("test-user");
        foreach (var s in new[] { seg1, seg2, seg3 })
            _context.GpsInSegments.Add(new GpsInSegment { TrackId = new TrackId(track), SegmentId = s });
        await _context.SaveChangesAsync();

        await _segmentService.ClearOrphanedSegmentMatchesAsync("test-user", new[] { seg1, seg2 });

        Assert.Equal(1, await _context.GpsInSegments.CountAsync(g => g.SegmentId == seg1));
        Assert.Equal(1, await _context.GpsInSegments.CountAsync(g => g.SegmentId == seg2));
        Assert.Equal(0, await _context.GpsInSegments.CountAsync(g => g.SegmentId == seg3));
    }

    // SD-01: a polyline re-analysis re-shapes the segment's effort spans, so an attached `done`
    // heatmap (painted per GPSinSegment span) must be flipped `stale` — previously only the shape
    // branch did this.
    [Fact]
    public async Task PolylineReanalyze_MarksAttachedHeatmapStale()
    {
        await SeedActivityAsync(BuildTrack());
        var segId = await SeedSegmentAsync(BuildSegment(startIdx: 6, count: 8));
        await SeedHeatmapAsync(segId, "done");

        await _segmentService.AnalyzeSegmentMatchesAsync(segId);

        var status = await _context.Heatmaps.Where(h => h.SegmentId == segId)
            .Select(h => h.Status).FirstAsync();
        Assert.Equal("stale", status);
    }

    // SD-01: an incremental import that matches nothing on the segment must NOT flip its heatmap
    // stale — no spans changed, so the tiles are still valid (no badge thrash).
    [Fact]
    public async Task IncrementalImport_NoMatch_LeavesHeatmapDone()
    {
        var segId = await SeedSegmentAsync(BuildSegment(startIdx: 6, count: 8)); // around lat 47.006
        await SeedHeatmapAsync(segId, "done");

        // A far-away track (lat 48.x) — no bbox overlap with the segment → no candidate → no change.
        var farTrack = await SeedActivityAsync([(48.000, 12.000), (48.001, 12.000), (48.002, 12.000)]);
        await _segmentService.MatchNewTrackAsync(new TrackId(farTrack), "test-user");

        var status = await _context.Heatmaps.Where(h => h.SegmentId == segId)
            .Select(h => h.Status).FirstAsync();
        Assert.Equal("done", status);
    }

    private async Task SeedHeatmapAsync(SegmentId segmentId, string status)
    {
        _context.Heatmaps.Add(new Heatmap { SegmentId = segmentId, Status = status });
        await _context.SaveChangesAsync();
    }

    // -------------------------------------------------------------------------
    // Loop-segment regression test.
    //
    // A loop segment's start pin and end pin are at the same geographic location.
    // The track crosses that location twice: once when entering the loop, and once
    // when finishing it.  Before the pair-coherence fix, the "latest crossing" rule
    // picked the loop-finish crossing as the entry, collapsing the effort to ~0 m.
    //
    // Track layout (12 points — clockwise rectangular loop):
    //   0 (47.000, 11.000)  approach from south
    //   1 (47.001, 11.000)
    //   2 (47.002, 11.000)  ← LOOP START/END pin
    //   3 (47.003, 11.000)  going north
    //   4 (47.004, 11.000)
    //   5 (47.004, 11.001)  going east
    //   6 (47.003, 11.001)  going south
    //   7 (47.002, 11.001)
    //   8 (47.002, 11.000)  ← LOOP FINISH (= start pin!)
    //   9 (47.001, 11.000)  exiting south
    //  10 (47.000, 11.000)
    [Fact]
    public async Task Analyze_LoopSegment_EntryBeforeExit_AndEffortMatchesSegmentDistance()
    {
        var trackPts = new (double lat, double lng)[]
        {
            (47.000, 11.000),
            (47.001, 11.000),
            (47.002, 11.000),  // loop entry  (index 2)
            (47.003, 11.000),
            (47.004, 11.000),
            (47.004, 11.001),
            (47.003, 11.001),
            (47.002, 11.001),
            (47.002, 11.000),  // loop finish (index 8)
            (47.001, 11.000),
            (47.000, 11.000),
        };

        // Asymmetric clockwise loop: same start and end pin (47.002, 11.000).
        var segPts = new (double lat, double lng)[]
        {
            (47.002, 11.000),  // start = end pin
            (47.003, 11.000),
            (47.004, 11.000),
            (47.004, 11.001),
            (47.003, 11.001),
            (47.002, 11.001),
            (47.002, 11.000),  // end = start pin (loop!)
        };

        TrackId trackId = new TrackId(await SeedActivityWithDistancesAsync(trackPts));
        var segId   = await SeedSegmentAsync(segPts);

        await _segmentService.AnalyzeSegmentMatchesAsync(segId);

        // Use AsNoTracking to bypass EF's change-tracker cache. The direction validator
        // loads GpsInSegment entities mid-pipeline (before offset calculation), so the
        // tracker holds stale snapshots with null calculated IDs without AsNoTracking.
        var efforts = await _context.GpsInSegments.AsNoTracking()
            .Where(e => e.SegmentId == segId).ToListAsync();
        Assert.Single(efforts);

        var effort = efforts[0];
        // Entry must come before exit (the pre-fix bug produced entry > exit).
        Assert.True(effort.GpsIdEntryCalculated < effort.GpsIdExitCalculated,
            $"Entry GPS_ID ({effort.GpsIdEntryCalculated}) must be before exit GPS_ID ({effort.GpsIdExitCalculated})");

        // Retrieve cumulative distances at the entry/exit ordinals to verify effort length.
        var decoded = await DecodeTrackAsync(trackId);
        var entryDist = decoded[effort.GpsIdEntryCalculated!.Value].Distance;
        var exitDist = decoded[effort.GpsIdExitCalculated!.Value].Distance;

        double? effortMeters = exitDist - entryDist;
        double segmentDistance = (await _context.Segments.FindAsync(segId))!.SegmentDistance!.Value;

        // Effort distance should be within 10% of the segment distance.
        Assert.True(Math.Abs(effortMeters!.Value - segmentDistance) < segmentDistance * 0.10,
            $"Effort distance {effortMeters:F0} m should be within 10% of segment distance {segmentDistance:F0} m");
    }

    // -------------------------------------------------------------------------
    // Multi-pass linear regression test (regression guard for the "up" segment fix).
    //
    // The track crosses the segment start pin TWICE: first going in the wrong direction
    // (south, while the segment runs north), then correctly after turning around.
    // The correct entry is the SECOND (later) crossing of the start pin.
    //
    // Segment: A=(47.006,11.000) → B=(47.013,11.000), 7 hops × ~111 m = ~777 m.
    //
    // Track:
    //   0 (47.013) d=0      at B, going south (wrong direction)
    //   1 (47.010) d=333
    //   2 (47.007) d=666
    //   3 (47.006) d=777    ← first crossing of start pin A — wrong direction!
    //   4 (47.005) d=888    past A, about to turn
    //   5 (47.006) d=999    ← second crossing of A — now going north (correct)
    //   6 (47.007) d=1110
    //   7 (47.008) d=1221
    //   8 (47.009) d=1332
    //   9 (47.010) d=1443
    //  10 (47.011) d=1554
    //  11 (47.012) d=1665
    //  12 (47.013) d=1776   at B — correct traversal complete
    [Fact]
    public async Task Analyze_MultiPassLinear_EntryIsSecondStartCrossing()
    {
        var trackPts = new (double lat, double lng)[]
        {
            (47.013, 11.000),  // 0  — near end pin, going wrong direction
            (47.010, 11.000),  // 1
            (47.007, 11.000),  // 2
            (47.006, 11.000),  // 3  — first crossing of start pin (wrong direction)
            (47.005, 11.000),  // 4  — turnaround
            (47.006, 11.000),  // 5  — second crossing of start pin (correct direction)
            (47.007, 11.000),  // 6
            (47.008, 11.000),  // 7
            (47.009, 11.000),  // 8
            (47.010, 11.000),  // 9
            (47.011, 11.000),  // 10
            (47.012, 11.000),  // 11
            (47.013, 11.000),  // 12 — near end pin, correct traversal done
        };

        var segPts = new (double lat, double lng)[]
        {
            (47.006, 11.000),  // A — start pin
            (47.007, 11.000),
            (47.008, 11.000),
            (47.009, 11.000),
            (47.010, 11.000),
            (47.011, 11.000),
            (47.012, 11.000),
            (47.013, 11.000),  // B — end pin
        };

        TrackId trackId = new TrackId(await SeedActivityWithDistancesAsync(trackPts));
        var segId   = await SeedSegmentAsync(segPts);

        await _segmentService.AnalyzeSegmentMatchesAsync(segId);

        // Use AsNoTracking to bypass EF's change-tracker cache (same reason as loop test).
        var efforts = await _context.GpsInSegments.AsNoTracking()
            .Where(e => e.SegmentId == segId).ToListAsync();
        Assert.Single(efforts);

        var effort = efforts[0];

        // Retrieve the lat at the calculated entry ordinal to confirm it is the second crossing.
        var decoded = await DecodeTrackAsync(trackId);
        var entryLat = decoded[effort.GpsIdEntryCalculated!.Value].GpsLat;

        Assert.True(entryLat > 47.0059 && entryLat < 47.0061,
            $"Entry should be at the start pin (~47.006), found lat {entryLat}");

        // Confirm that there is a point at an earlier ordinal also at ~47.006 (the wrong-direction
        // crossing), i.e. the first crossing was correctly skipped.
        var priorCrossingExists = decoded.Take(effort.GpsIdEntryCalculated!.Value)
            .Any(g => g.GpsLat > 47.0059 && g.GpsLat < 47.0061);

        Assert.True(priorCrossingExists,
            "There should be an earlier start-pin crossing that was correctly skipped");
    }

    // -------------------------------------------------------------------------
    // SG-01 / SG-02 authorization tests. A second SegmentService instance scoped to
    // "other-user" (sharing _context) exercises the non-owner / not-visible paths.

    [Fact]
    public async Task CanAnalyzeSegment_True_ForOwnPolylineSegment()
    {
        var segId = await SeedSegmentAsync(BuildSegment(startIdx: 6, count: 8)); // polyline (kind null)
        Assert.True(await _segmentService.CanAnalyzeSegmentAsync(segId));
    }

    [Fact]
    public async Task CanAnalyzeSegment_False_ForForeignShapeSegment()
    {
        await SeedOtherUserAsync();
        var segId = await SeedShapeSegmentForUserAsync("test-user"); // invisible to other-user
        Assert.False(await OtherUserService().Analysis.CanAnalyzeSegmentAsync(segId));
    }

    [Fact]
    public async Task CanAnalyzeSegment_False_ForUnknownId()
    {
        Assert.False(await _segmentService.CanAnalyzeSegmentAsync(new SegmentId(999999)));
    }

    [Fact]
    public async Task AnalyzeSegmentMatches_ShapeSegment_ForeignUser_LeavesEffortsIntact()
    {
        await SeedOtherUserAsync();
        TrackId trackId = new TrackId(await SeedActivityAsync(BuildTrack()));
        var segId   = await SeedShapeSegmentForUserAsync("test-user");
        _context.GpsInSegments.Add(new GpsInSegment { TrackId = trackId, SegmentId = segId });
        await _context.SaveChangesAsync();

        // other-user must not be able to trigger the shape rebuild's global effort DELETE.
        await OtherUserService().Analysis.AnalyzeSegmentMatchesAsync(segId, "other-user");

        Assert.Equal(1, await _context.GpsInSegments.CountAsync(e => e.SegmentId == segId));
    }

    [Fact]
    public async Task GetSegmentsForActivity_Empty_ForForeignActivity()
    {
        await SeedOtherUserAsync();
        TrackId trackId  = new TrackId(await SeedActivityAsync(BuildTrack()));              // owned by test-user
        var otherSeg = await SeedPolylineSegmentForUserAsync("other-user"); // visible to other-user
        _context.GpsInSegments.Add(new GpsInSegment { TrackId = trackId, SegmentId = otherSeg });
        await _context.SaveChangesAsync();

        // other-user sees otherSeg but not test-user's activity → must not learn the crossing.
        Assert.Empty(await OtherUserService().Crud.GetSegmentsForActivityAsync(trackId));
    }

    private SegmentServices OtherUserService()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var settings = new ProfileSettingsService(
            _context, NullLogger<ProfileSettingsService>.Instance, cache, TimeProvider.System);
        var trackReader = new DirectTrackReader(_context);
        var shapeMatcher = new ShapeSegmentMatcher(_context, NullLogger<ShapeSegmentMatcher>.Instance, trackReader);
        var engine = new MatchEngineService(_context, trackReader, NullLogger<MatchEngineService>.Instance);
        var effortMath = new EffortMathService(_context, trackReader, NullLogger<EffortMathService>.Instance);
        return SegmentServices.Create(
            _context, cache, new TestVisibilityService("other-user"), new UserCacheInvalidator(), settings,
            shapeMatcher, engine, effortMath, trackReader);
    }

    private async Task SeedOtherUserAsync()
    {
        if (await _context.Users.AnyAsync(u => u.UserId == "other-user")) return;
        _context.Users.Add(new AppUser
        {
            UserId      = "other-user",
            DisplayName = "Other User",
            Email       = "other@test.com",
            IsAdmin     = false,
            CreatedAt   = DateTime.UtcNow.ToString("O"),
            LastLogin   = DateTime.UtcNow.ToString("O"),
        });
        await _context.SaveChangesAsync();
    }

    private async Task<SegmentId> SeedShapeSegmentForUserAsync(string userId)
    {
        var seg = new Segment { UserId = userId, SegmentKind = "shapes", SegmentDistance = 100 };
        _context.Segments.Add(seg);
        await _context.SaveChangesAsync();
        return seg.SegmentId;
    }

    private async Task<SegmentId> SeedPolylineSegmentForUserAsync(string userId)
    {
        var seg = new Segment { UserId = userId, SegmentDistance = 100 };
        _context.Segments.Add(seg);
        await _context.SaveChangesAsync();
        return seg.SegmentId;
    }

    // -------------------------------------------------------------------------
    // Decode a track's blob to per-point POCOs (GpsId = ordinal) for by-ordinal assertions.
    private async Task<List<GpsData>> DecodeTrackAsync(TrackId trackId) =>
        (await new DirectTrackReader(_context).GetTrackAsync(trackId))!.ToGpsDataList(trackId);

    // -------------------------------------------------------------------------
    // Seed helpers -- let EF auto-generate all primary keys.

    private async Task<int> SeedActivityAsync((double lat, double lng)[] points)
    {
        var activity = new Activity
        {
            StartGpsLat  = points[0].lat,
            StartGpsLong = points[0].lng,
            EndGpsLat    = points[^1].lat,
            EndGpsLong   = points[^1].lng,
            UserId       = "test-user",
        };
        _context.Activities.Add(activity);
        await _context.SaveChangesAsync();

        TrackId trackId = activity.TrackId; // EF fills after insert

        var pts = points.Select(p => new GpsData { GpsLat = p.lat, GpsLong = p.lng }).ToList();
        await TrackSeed.SeedTrackAsync(_context, trackId, pts);

        // Stage 1 reads Activity_Bbox; seed it now to avoid the full GPS GROUP BY fallback.
        await _context.Database.ExecuteSqlRawAsync(
            @"INSERT OR IGNORE INTO Activity_Bbox (track_ID, min_lat, max_lat, min_long, max_long)
              VALUES ({0}, {1}, {2}, {3}, {4})",
            trackId.Value,
            points.Min(p => p.lat),
            points.Max(p => p.lat),
            points.Min(p => p.lng),
            points.Max(p => p.lng));

        return trackId.Value;
    }

    // Like SeedActivityAsync but also computes and stores cumulative Haversine distances
    // in GPS.distance. Required for pair-coherence scoring to work correctly.
    private async Task<int> SeedActivityWithDistancesAsync((double lat, double lng)[] points)
    {
        var activity = new Activity
        {
            StartGpsLat  = points[0].lat,
            StartGpsLong = points[0].lng,
            EndGpsLat    = points[^1].lat,
            EndGpsLong   = points[^1].lng,
            UserId       = "test-user",
        };
        _context.Activities.Add(activity);
        await _context.SaveChangesAsync();

        TrackId trackId = activity.TrackId;

        var pts = new List<GpsData>();
        double cumDist = 0;
        var prev = points[0];
        foreach (var (lat, lng) in points)
        {
            cumDist += HaversineMeters(prev.lat, prev.lng, lat, lng);
            pts.Add(new GpsData { GpsLat = lat, GpsLong = lng, Distance = cumDist });
            prev = (lat, lng);
        }
        await TrackSeed.SeedTrackAsync(_context, trackId, pts);

        await _context.Database.ExecuteSqlRawAsync(
            @"INSERT OR IGNORE INTO Activity_Bbox (track_ID, min_lat, max_lat, min_long, max_long)
              VALUES ({0}, {1}, {2}, {3}, {4})",
            trackId.Value,
            points.Min(p => p.lat),
            points.Max(p => p.lat),
            points.Min(p => p.lng),
            points.Max(p => p.lng));

        return trackId.Value;
    }

    private async Task<SegmentId> SeedSegmentAsync((double lat, double lng)[] points)
    {
        double dist = 0;
        for (int i = 1; i < points.Length; i++)
            dist += HaversineMeters(points[i - 1].lat, points[i - 1].lng, points[i].lat, points[i].lng);

        var segment = new Segment { UserId = "test-user", SegmentDistance = dist };
        _context.Segments.Add(segment);
        await _context.SaveChangesAsync();

        var segId = segment.SegmentId; // EF fills after insert

        foreach (var (lat, lng) in points)
        {
            _context.SegmentGps.Add(new SegmentGps { SegmentId = segId, GpsLat = lat, GpsLong = lng });
        }
        await _context.SaveChangesAsync();

        return segId;
    }

    private static double HaversineMeters(double lat1, double lng1, double lat2, double lng2)
    {
        const double R = 6_371_000;
        double dLat = (lat2 - lat1) * Math.PI / 180;
        double dLng = (lng2 - lng1) * Math.PI / 180;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                 + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180)
                 * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return 2 * R * Math.Asin(Math.Sqrt(a));
    }
}
