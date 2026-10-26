using System.Globalization;
using System.Text.Json;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Strava;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Matching;
using RouteBaseWeb.Services.Segments;
using RouteBaseWeb.Services.Segments.Rules;
using RouteBaseWeb.Services.Tracks;
using RouteBaseWeb.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Pipeline;

/// <summary>
/// Equivalence oracle for incremental per-import matching (subproject candidate-selection).
///
/// The contract (analysis.md cand-01): an incremental sequence of per-import matches must produce a
/// <c>GPSinSegment</c> row set byte-identical to a full "Analyze All", modulo the <c>GPSinSegment_ID</c>
/// autoincrement + physical row order. <see cref="NormalizeAsync"/> projects every computed column EXCEPT
/// the PK and renders doubles by their exact bit pattern, so comparison is exact — a tolerance would hide
/// divergence, and the pure cores are bit-deterministic.
///
/// cand-T0 pins the analyze-all path as a stable, non-empty characterization snapshot BEFORE any
/// orchestration change (pins-first). cand-T1..T4 (added in I1c) assert the incremental path equals it.
/// </summary>
public class IncrementalMatchEquivalenceTests : IAsyncLifetime
{
    private RouteBaseDbContext _context = null!;
    private ISegmentAnalysis _segmentService = null!;

    private const string User = "test-user";

    // -------------------------------------------------------------------------
    // Fixture

    public async Task InitializeAsync()
    {
        var dbName = $"testdb_{Guid.NewGuid():N}";
        var connectionString = $"Data Source=file:{dbName}?mode=memory&cache=shared";

        var options = new DbContextOptionsBuilder<RouteBaseDbContext>()
            .UseSqlite(connectionString)
            .Options;

        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();

        var schemaManager = new DbSchemaManager(_context);
        await schemaManager.EnsureActivityBboxTableAsync();
        await schemaManager.EnsureTrackContentTablesAsync();
        await schemaManager.EnsureIndexesAsync();

        _context.Users.Add(new AppUser
        {
            UserId      = User,
            DisplayName = "Test User",
            Email       = "test@test.com",
            IsAdmin     = false,
            CreatedAt   = DateTime.UtcNow.ToString("O"),
            LastLogin   = DateTime.UtcNow.ToString("O"),
        });
        await _context.SaveChangesAsync();

        _segmentService = BuildAnalysis();
    }

    // A fresh service graph over the shared context (mirrors DI wiring). One DirectTrackReader shared
    // across both engines, exactly as SegmentAnalysisPipelineTests.
    private ISegmentAnalysis BuildAnalysis()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var settings = new ProfileSettingsService(_context, NullLogger<ProfileSettingsService>.Instance, cache, TimeProvider.System);
        var trackReader = new DirectTrackReader(_context);
        var shapeMatcher = new ShapeSegmentMatcher(_context, NullLogger<ShapeSegmentMatcher>.Instance, trackReader);
        var engine = new MatchEngineService(_context, trackReader, NullLogger<MatchEngineService>.Instance);
        var effortMath = new EffortMathService(_context, trackReader, NullLogger<EffortMathService>.Instance);
        return SegmentServices.Create(
            _context, cache, new TestVisibilityService(), new UserCacheInvalidator(), settings,
            shapeMatcher, engine, effortMath, trackReader).Analysis;
    }

    public async Task DisposeAsync()
    {
        await _context.Database.EnsureDeletedAsync();
        await _context.DisposeAsync();
    }

    // -------------------------------------------------------------------------
    // cand-T0 — Analyze-all characterization snapshot (pins-first, no production code).

    [Fact]
    public async Task AnalyzeAll_ProducesStableNonEmptySnapshot()
    {
        var (_, segIds) = await SeedPopulationAsync();

        var run1 = await RunAnalyzeAllAsync(segIds);
        var run2 = await RunAnalyzeAllAsync(segIds);

        Assert.NotEmpty(run1);
        Assert.Equal(run1.Count, run2.Count);
        Assert.Equal(run1, run2);   // analyze-all is deterministic across repeated runs.
    }

    // -------------------------------------------------------------------------
    // cand-T1 — Equivalence oracle: incremental imports == analyze-all.

    [Fact]
    public async Task IncrementalImports_EqualAnalyzeAll()
    {
        var (trackIds, segIds) = await SeedPopulationAsync();

        // Path A: full analyze-all → golden snapshot.
        var sa = await RunAnalyzeAllAsync(segIds);

        // Path B: clean slate, then import each track (segments pre-existing) in creation order.
        await _segmentService.ClearAllSegmentMatchesAsync(User);
        foreach (var trackId in trackIds.Select(i => new TrackId(i)))
            await _segmentService.MatchNewTrackAsync(trackId, User);
        var sb = await NormalizeAsync();

        Assert.Equal(sa.Count, sb.Count);   // legible failure before the element-wise compare.
        Assert.Equal(sa, sb);
        Assert.NotEmpty(sb);
    }

    // -------------------------------------------------------------------------
    // cand-T2 — Mixed interleaved sequence (import + create triggers) == analyze-all over the same final
    // population. Pins the coverage argument (one of track/segment is always created last).

    [Fact]
    public async Task InterleavedImportsAndCreates_EqualAnalyzeAll()
    {
        // Interleaved live sequence: import T1; create S1; import T2; create S2.
        var t1 = await SeedActivityAsync(BuildLinearTrack());
        await _segmentService.MatchNewTrackAsync(new TrackId(t1), User);               // no segments yet → no rows.

        var s1 = await SeedSegmentAsync(BuildSegment(startIdx: 6, count: 8));
        await _segmentService.AnalyzeSegmentMatchesAsync(s1, User);       // create trigger → matches T1.

        var t2 = await SeedActivityWithDistancesAsync(BuildMultiPassTrack());
        await _segmentService.MatchNewTrackAsync(new TrackId(t2), User);              // import trigger → matches S1.

        var s2 = await SeedSegmentAsync(BuildMultiPassSegment());
        await _segmentService.AnalyzeSegmentMatchesAsync(s2, User);      // create trigger → matches T1,T2.

        var incremental = await NormalizeAsync();

        // From-scratch analyze-all over the identical final population.
        var analyzeAll = await RunAnalyzeAllAsync(new List<SegmentId> { s1, s2 });

        Assert.Equal(analyzeAll.Count, incremental.Count);
        Assert.Equal(analyzeAll, incremental);
        Assert.NotEmpty(incremental);
    }

    // -------------------------------------------------------------------------
    // cand-T3 — Isolation: importing one track does not disturb a pre-existing track's rows.
    // Pins cand-07 (delete scope) + cand-04 (guard scope).

    [Fact]
    public async Task Import_DoesNotDisturbOtherTracksRows()
    {
        var t1 = await SeedActivityAsync(BuildLinearTrack());
        var seg = await SeedSegmentAsync(BuildSegment(startIdx: 6, count: 8));
        await _segmentService.AnalyzeSegmentMatchesAsync(seg, User);      // T1 matched.

        var t1Before = await NormalizeForTrackAsync(new TrackId(t1));
        Assert.NotEmpty(t1Before);

        var t2 = await SeedActivityWithDistancesAsync(BuildMultiPassTrack());
        await _segmentService.MatchNewTrackAsync(new TrackId(t2), User);

        // T1's rows are byte-unchanged; T2 gained its own rows iff gated.
        var t1After = await NormalizeForTrackAsync(new TrackId(t1));
        Assert.Equal(t1Before, t1After);

        int t2Count = await _context.GpsInSegments.AsNoTracking().CountAsync(r => r.TrackId == new TrackId(t2));
        Assert.True(t2Count > 0, "T2 should have produced at least one effort on the overlapping segment");
    }

    // -------------------------------------------------------------------------
    // cand-T4 — Candidate-segment selection (cand-06): overlapping segment only; far + shape never chosen.

    [Fact]
    public async Task Import_MatchesOverlappingSegmentOnly()
    {
        var track   = await SeedActivityAsync(BuildLinearTrack());
        var segNear = await SeedSegmentAsync(BuildSegment(startIdx: 6, count: 8)); // overlaps
        var segFar  = await SeedSegmentAsync(BuildFarSegment());                   // no overlap
        var segShape = await SeedShapeSegmentAsync();                              // never a polyline candidate

        await _segmentService.MatchNewTrackAsync(new TrackId(track), User);

        Assert.True(await _context.GpsInSegments.AsNoTracking().CountAsync(r => r.SegmentId == segNear) > 0,
            "Overlapping segment should have efforts");
        Assert.Equal(0, await _context.GpsInSegments.AsNoTracking().CountAsync(r => r.SegmentId == segFar));
        // Zero because this shape segment carries no rule document, NOT because imports skip shapes —
        // cand-T5 below pins that they DO match. What this case proves is narrower and still true: the
        // polyline candidate predicate never picks a shape segment up.
        Assert.Equal(0, await _context.GpsInSegments.AsNoTracking().CountAsync(r => r.SegmentId == segShape));
    }

    // -------------------------------------------------------------------------
    // cand-T5 — Shape segments join on the IMPORT path, not just on approve.
    //
    // The regression pinned here: both incremental entry points used to load polyline candidates only and
    // return when that set came back empty, so a user whose segments are all shapes matched nothing on
    // import, recording finalize or approve. Seed NO polyline segment in these cases — an empty polyline
    // candidate set is the whole point. Approve-path coverage lives in ApproveSegmentMatchTests and
    // ApproveWorkerSegmentMatchTests; this is the import half.

    [Fact]
    public async Task Import_ShapesOnlyUser_MatchesShapeSegment()
    {
        var track = await SeedActivityWithDistancesAsync(BuildLinearTrack());
        var shape = await SeedShapeSegmentWithRulesAsync();

        await _segmentService.MatchNewTrackAsync(new TrackId(track), User);

        Assert.True(await _context.GpsInSegments.AsNoTracking().CountAsync(r => r.SegmentId == shape) > 0,
            "A shapes-only user's ride must land on its shape segment via the single-track entry point");
    }

    // The batch entry point is what an import drain actually calls (ImportMatchStep), and it does its own
    // load-and-bail — so it can regress independently of the single-track one.
    [Fact]
    public async Task ImportBatch_ShapesOnlyUser_MatchesShapeSegment()
    {
        var track = await SeedActivityWithDistancesAsync(BuildLinearTrack());
        var shape = await SeedShapeSegmentWithRulesAsync();

        await _segmentService.MatchNewTracksAsync([track], User);

        Assert.True(await _context.GpsInSegments.AsNoTracking().CountAsync(r => r.SegmentId == shape) > 0,
            "The batch import drain must match shape segments too");
    }

    // Guards the other end: the shape pass must MATCH, not blanket-insert.
    [Fact]
    public async Task Import_ShapeSegmentAwayFromTheTrack_GetsNoEfforts()
    {
        var track = await SeedActivityWithDistancesAsync(BuildLinearTrack());
        var near  = await SeedShapeSegmentWithRulesAsync();
        var far   = await SeedShapeSegmentWithRulesAsync(FarFromTheCorridor);

        await _segmentService.MatchNewTrackAsync(new TrackId(track), User);

        Assert.True(await _context.GpsInSegments.AsNoTracking().CountAsync(r => r.SegmentId == near) > 0,
            "The corridor zone should have matched");
        Assert.Equal(0, await _context.GpsInSegments.AsNoTracking().CountAsync(r => r.SegmentId == far));
    }

    // -------------------------------------------------------------------------
    // inval-T1 — Import wiring == analyze-all (end-to-end). Drive the REAL StravaImportOrchestrator over the
    // shared context (real ActivityImportService persists blob + Activity_Bbox), collecting fresh trackIds
    // via the onActivityImported callback exactly as OperationQueueWorker.RunImportAsync does. Then match
    // those collected ids and assert the row set equals a full analyze-all over the same population.

    [Fact]
    public async Task ImportCallbackTracks_MatchedIncrementally_EqualAnalyzeAll()
    {
        var importService = new ActivityImportService(
            _context, new TrackContentStore(_context, new TrackObjectStore(_context, TimeProvider.System), TimeProvider.System), NullLogger<ActivityImportService>.Instance);
        var api = new FakeStravaApiClient();
        var orchestrator = new StravaImportOrchestrator(
            _context, api, importService, NullLogger<StravaImportOrchestrator>.Instance);

        // Two fresh Strava activities over the linear corridor (distinct ApiIds → both import).
        api.Streams[101] = StreamsFor(BuildLinearTrack());
        api.Streams[102] = StreamsFor(BuildLinearTrack());
        var summaries = new[]
        {
            StravaImportOrchestrator.MapToExternalSummary(new StravaActivity { Id = 101, Name = "A101" }),
            StravaImportOrchestrator.MapToExternalSummary(new StravaActivity { Id = 102, Name = "A102" }),
        };

        var collected = new List<int>();
        var (imported, _, _) = await orchestrator.ImportActivitiesParallelAsync(
            summaries, User, (_, _, _, _, _) => { },
            onActivityImported: id => { lock (collected) collected.Add(id); },
            CancellationToken.None);

        // Wiring pin: the callback surfaced exactly the tracks that were persisted.
        var dbTrackIds = await _context.Activities.AsNoTracking()
            .Where(a => a.UserId == User).Select(a => a.TrackId).ToListAsync();
        Assert.Equal(2, imported);
        Assert.Equal(dbTrackIds.Select(t => t.Value).OrderBy(x => x), collected.OrderBy(x => x));

        // Segments overlapping the imported tracks (created after import, pre-existing at match time).
        var segIds = new List<SegmentId>
        {
            await SeedSegmentAsync(BuildSegment(startIdx: 6, count: 8)),
            await SeedSegmentAsync(BuildSegment(startIdx: 1, count: 8)),
        };

        // Path A: analyze-all → golden. Path B: match each collected trackId → incremental.
        var sa = await RunAnalyzeAllAsync(segIds);

        await _segmentService.ClearAllSegmentMatchesAsync(User);
        foreach (var trackId in collected.Select(i => new TrackId(i)))
            await _segmentService.MatchNewTrackAsync(trackId, User);
        var sb = await NormalizeAsync();

        Assert.NotEmpty(sa);
        Assert.Equal(sa.Count, sb.Count);
        Assert.Equal(sa, sb);
    }

    // -------------------------------------------------------------------------
    // inval-T3 — Threshold parity: the incremental import-match honours matchThresholdPercent exactly as
    // analyze-all does. The worker sources this value from GetDefaultMatchThresholdAsync for BOTH paths
    // (inval-05, verified by gate-grep); this pins that, given the same value, the two paths produce
    // byte-identical rows — including at a threshold that STRADDLES the crossing's MatchPerc, so the row
    // exists in one run and not the other (proving the gate actually consults the parameter).

    [Fact]
    public async Task ImportMatch_HonoursThreshold_IdenticallyToAnalyzeAll()
    {
        // Partial-coverage crossing: the track covers only the start+end zones of the segment (its middle
        // points bulge off-corridor), so MatchPerc ≈ 60%. threshold 40 admits it; threshold 80 rejects it.
        var track = await SeedActivityAsync(BuildLinearTrack());
        var seg   = await SeedSegmentAsync(BuildPartialCoverageSegment());
        var segIds = new List<SegmentId> { seg };

        // Below MatchPerc → both paths keep the row.
        var saLow = await RunAnalyzeAllAsync(segIds, 40);
        await _segmentService.ClearAllSegmentMatchesAsync(User);
        await _segmentService.MatchNewTrackAsync(new TrackId(track), User, 40);
        var sbLow = await NormalizeAsync();
        Assert.NotEmpty(saLow);
        Assert.Equal(saLow, sbLow);

        // Above MatchPerc → both paths drop it.
        var saHigh = await RunAnalyzeAllAsync(segIds, 80);
        await _segmentService.ClearAllSegmentMatchesAsync(User);
        await _segmentService.MatchNewTrackAsync(new TrackId(track), User, 80);
        var sbHigh = await NormalizeAsync();
        Assert.Empty(saHigh);
        Assert.Equal(saHigh, sbHigh);
    }

    // -------------------------------------------------------------------------
    // inval-T4 — Idempotent double-match (inval-07 #3): re-running MatchNewTrackAsync for the same track over
    // the same segment produces byte-identical rows (the per-(track,seg) DELETE+insert is a clean replace),
    // and a later analyze-all overwrites the import's rows with the same values (inval-07 #1).

    [Fact]
    public async Task DoubleMatch_SameTrack_IsIdempotent()
    {
        var track = await SeedActivityAsync(BuildLinearTrack());
        await SeedSegmentAsync(BuildSegment(startIdx: 6, count: 8));

        await _segmentService.MatchNewTrackAsync(new TrackId(track), User);
        var first = await NormalizeForTrackAsync(new TrackId(track));
        Assert.NotEmpty(first);

        await _segmentService.MatchNewTrackAsync(new TrackId(track), User);   // double-fire (queue redelivery / manual re-run).
        var second = await NormalizeForTrackAsync(new TrackId(track));

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task ImportMatch_ThenAnalyzeAll_LeavesTrackRowsIdentical()
    {
        var track = await SeedActivityAsync(BuildLinearTrack());
        var seg   = await SeedSegmentAsync(BuildSegment(startIdx: 6, count: 8));

        await _segmentService.MatchNewTrackAsync(new TrackId(track), User);
        var afterImport = await NormalizeForTrackAsync(new TrackId(track));
        Assert.NotEmpty(afterImport);

        await RunAnalyzeAllAsync(new List<SegmentId> { seg });   // clean-slate rebuild overwrites the import's rows.
        var afterAnalyzeAll = await NormalizeForTrackAsync(new TrackId(track));

        Assert.Equal(afterImport, afterAnalyzeAll);
    }

    // -------------------------------------------------------------------------
    // Shared seed population — reused by cand-T1..T4. ≥3 tracks (incl. distance-bearing loop + multi-pass
    // linear) + ≥3 overlapping polyline segments + ≥1 non-overlapping segment + ≥1 far track.

    private async Task<(List<int> trackIds, List<SegmentId> segIds)> SeedPopulationAsync()
    {
        var trackIds = new List<int>
        {
            await SeedActivityAsync(BuildLinearTrack()),          // T1 linear corridor
            await SeedActivityWithDistancesAsync(BuildLoopTrack()),      // T2 loop
            await SeedActivityWithDistancesAsync(BuildMultiPassTrack()), // T3 multi-pass linear
            await SeedActivityAsync(BuildFarTrack()),             // T4 far — matches nothing
        };

        var segIds = new List<SegmentId>
        {
            await SeedSegmentAsync(BuildSegment(startIdx: 6, count: 8)),  // overlaps T1
            await SeedSegmentAsync(BuildSegment(startIdx: 1, count: 8)),  // overlaps T1
            await SeedSegmentAsync(BuildLoopSegment()),                   // overlaps T2
            await SeedSegmentAsync(BuildMultiPassSegment()),             // overlaps T3 (and T1)
            await SeedSegmentAsync(BuildFarSegment()),                   // overlaps nothing
        };

        return (trackIds, segIds);
    }

    // Analyze All = clean-slate wipe + per-segment analyze loop (OperationQueueWorker.RunAnalysisAsync).
    private Task<List<string>> RunAnalyzeAllAsync(List<SegmentId> segIds) =>
        RunAnalyzeAllAsync(segIds, SegmentAnalysis.DefaultMatchThresholdPercent);

    private async Task<List<string>> RunAnalyzeAllAsync(List<SegmentId> segIds, int matchThresholdPercent)
    {
        await _segmentService.ClearAllSegmentMatchesAsync(User);
        foreach (var segId in segIds)
            await _segmentService.AnalyzeSegmentMatchesAsync(segId, User, matchThresholdPercent);
        return await NormalizeAsync();
    }

    // -------------------------------------------------------------------------
    // Normalize — canonical, PK-free, order-independent projection of the GPSinSegment set.
    //
    // Projects every computed column EXCEPT GPSinSegment_ID (cand-T1 spec). Doubles are rendered by their
    // exact 64-bit pattern (BitConverter) so NaN compares reflexively and identical pure code compares
    // bit-equal. AsNoTracking (cand-05 caveat: the direction validator loads tracked entities mid-run).
    private async Task<List<string>> NormalizeAsync()
    {
        var rows = await _context.GpsInSegments.AsNoTracking().ToListAsync();
        return rows
            .Select(RowKey)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
    }

    private static string RowKey(GpsInSegment r) => string.Join('|',
        // Natural key first (SegmentId, TrackId, entry/exit guessed) → legible ordering.
        r.SegmentId, r.TrackId, N(r.GpsIdEntryGuessed), N(r.GpsIdExitGuessed),
        N(r.SegGpsCount), N(r.SegTotalCount), N(r.SegPerc), N(r.GpsIdCount),
        N(r.GpsIdEntryCalculated), N(r.GpsIdExitCalculated),
        D(r.OffsetEntry), D(r.OffsetExit),
        D(r.EffortDistance), D(r.EffortElevation), D(r.EffortTime),
        D(r.EffortSpeedAvg), D(r.EffortHrAvg), D(r.EffortPowerAvg), D(r.EffortCadenceAvg));

    private async Task<List<string>> NormalizeForTrackAsync(TrackId trackId)
    {
        var rows = await _context.GpsInSegments.AsNoTracking().Where(r => r.TrackId == trackId).ToListAsync();
        return rows.Select(RowKey).OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    private static string N(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "-";
    private static string D(double? v) => v.HasValue ? BitConverter.DoubleToInt64Bits(v.Value).ToString(CultureInfo.InvariantCulture) : "-";

    // -------------------------------------------------------------------------
    // Coordinate builders.

    // 20-point linear corridor, lat 47.000..47.019 at lng 11.000.
    private static (double lat, double lng)[] BuildLinearTrack()
    {
        var pts = new (double lat, double lng)[20];
        for (int i = 0; i < 20; i++) pts[i] = (47.000 + i * 0.001, 11.000);
        return pts;
    }

    // Polyline segment covering 'count' points starting at index 'startIdx' of the linear corridor.
    private static (double lat, double lng)[] BuildSegment(int startIdx, int count = 8)
    {
        var pts = new (double lat, double lng)[count];
        for (int i = 0; i < count; i++) pts[i] = (47.000 + (startIdx + i) * 0.001, 11.000);
        return pts;
    }

    // 10-point segment over the linear corridor whose MIDDLE four points bulge far off-corridor
    // (lng 11.010, well beyond the match tolerance). The track matches only the 6 start/end-zone points
    // on the corridor → MatchPerc ≈ 60%, straddled by thresholds 40 (kept) and 80 (dropped). (inval-T3)
    private static (double lat, double lng)[] BuildPartialCoverageSegment() =>
    [
        (47.005, 11.000), (47.006, 11.000), (47.007, 11.000),   // start zone — on corridor, matched
        (47.008, 11.010), (47.009, 11.010),                     // middle — bulged off, unmatched
        (47.010, 11.010), (47.011, 11.010),                     // middle — bulged off, unmatched
        (47.012, 11.000), (47.013, 11.000), (47.014, 11.000),   // end zone — on corridor, matched
    ];

    private static (double lat, double lng)[] BuildLoopTrack() =>
    [
        (47.000, 11.000), (47.001, 11.000), (47.002, 11.000), (47.003, 11.000),
        (47.004, 11.000), (47.004, 11.001), (47.003, 11.001), (47.002, 11.001),
        (47.002, 11.000), (47.001, 11.000), (47.000, 11.000),
    ];

    private static (double lat, double lng)[] BuildLoopSegment() =>
    [
        (47.002, 11.000), (47.003, 11.000), (47.004, 11.000), (47.004, 11.001),
        (47.003, 11.001), (47.002, 11.001), (47.002, 11.000),
    ];

    private static (double lat, double lng)[] BuildMultiPassTrack() =>
    [
        (47.013, 11.000), (47.010, 11.000), (47.007, 11.000), (47.006, 11.000),
        (47.005, 11.000), (47.006, 11.000), (47.007, 11.000), (47.008, 11.000),
        (47.009, 11.000), (47.010, 11.000), (47.011, 11.000), (47.012, 11.000),
        (47.013, 11.000),
    ];

    private static (double lat, double lng)[] BuildMultiPassSegment() =>
    [
        (47.006, 11.000), (47.007, 11.000), (47.008, 11.000), (47.009, 11.000),
        (47.010, 11.000), (47.011, 11.000), (47.012, 11.000), (47.013, 11.000),
    ];

    // Far track / segment: different geo-cells → never a candidate, never an effort.
    private static (double lat, double lng)[] BuildFarTrack() =>
        [(48.000, 12.000), (48.001, 12.000), (48.002, 12.000)];

    private static (double lat, double lng)[] BuildFarSegment() =>
    [
        (49.000, 13.000), (49.001, 13.000), (49.002, 13.000), (49.003, 13.000),
        (49.004, 13.000), (49.005, 13.000), (49.006, 13.000), (49.007, 13.000),
    ];

    // -------------------------------------------------------------------------
    // Seed helpers — let EF auto-generate PKs. Mirror SegmentAnalysisPipelineTests.

    private async Task<int> SeedActivityAsync((double lat, double lng)[] points)
    {
        var activity = new Activity
        {
            StartGpsLat  = points[0].lat,
            StartGpsLong = points[0].lng,
            EndGpsLat    = points[^1].lat,
            EndGpsLong   = points[^1].lng,
            UserId       = User,
        };
        _context.Activities.Add(activity);
        await _context.SaveChangesAsync();
        TrackId trackId = activity.TrackId;

        var pts = points.Select(p => new GpsData { GpsLat = p.lat, GpsLong = p.lng }).ToList();
        await TrackSeed.SeedTrackAsync(_context, trackId, pts);
        await SeedBboxAsync(trackId, points);
        return trackId.Value;
    }

    private async Task<int> SeedActivityWithDistancesAsync((double lat, double lng)[] points)
    {
        var activity = new Activity
        {
            StartGpsLat  = points[0].lat,
            StartGpsLong = points[0].lng,
            EndGpsLat    = points[^1].lat,
            EndGpsLong   = points[^1].lng,
            UserId       = User,
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
        await SeedBboxAsync(trackId, points);
        return trackId.Value;
    }

    private async Task SeedBboxAsync(TrackId trackId, (double lat, double lng)[] points) =>
        await _context.Database.ExecuteSqlRawAsync(
            @"INSERT OR IGNORE INTO Activity_Bbox (track_ID, min_lat, max_lat, min_long, max_long)
              VALUES ({0}, {1}, {2}, {3}, {4})",
            trackId.Value,
            points.Min(p => p.lat), points.Max(p => p.lat),
            points.Min(p => p.lng), points.Max(p => p.lng));

    private async Task<SegmentId> SeedSegmentAsync((double lat, double lng)[] points)
    {
        double dist = 0;
        for (int i = 1; i < points.Length; i++)
            dist += HaversineMeters(points[i - 1].lat, points[i - 1].lng, points[i].lat, points[i].lng);

        var segment = new Segment { UserId = User, SegmentDistance = dist };
        _context.Segments.Add(segment);
        await _context.SaveChangesAsync();
        var segId = segment.SegmentId;

        foreach (var (lat, lng) in points)
            _context.SegmentGps.Add(new SegmentGps { SegmentId = segId, GpsLat = lat, GpsLong = lng });
        await _context.SaveChangesAsync();
        return segId;
    }

    // Shape segment: no Segment_GPS → never selected by the polyline candidate predicate (cand-06).
    // Also no rule document, so ShapeSegmentMatcher skips it — use SeedShapeSegmentWithRulesAsync
    // when the shape half is what's under test.
    private async Task<SegmentId> SeedShapeSegmentAsync()
    {
        var seg = new Segment { UserId = User, SegmentKind = "shapes", SegmentDistance = 100 };
        _context.Segments.Add(seg);
        await _context.SaveChangesAsync();
        return seg.SegmentId;
    }

    // Zone over the whole linear corridor (lat 47.000..47.019 @ lng 11.000). Same rect as
    // ApproveWorkerSegmentMatchTests, which drives this corridor from the approve path.
    private static RectGeom OverTheCorridor =>
        new() { South = 46.999, West = 10.999, North = 47.020, East = 11.001 };

    // A zone the corridor never enters — a legitimate non-match.
    private static RectGeom FarFromTheCorridor =>
        new() { South = 40.000, West = 5.000, North = 40.100, East = 5.100 };

    // Shape segment that CAN match: the rule document is what makes ShapeSegmentMatcher act on it.
    private async Task<SegmentId> SeedShapeSegmentWithRulesAsync(RectGeom? zone = null)
    {
        var rules = new RuleDocument
        {
            Shapes = new()
            {
                new ShapeDef { No = 1, Kind = "zone", Geom = zone ?? OverTheCorridor },
            },
            Membership = { new PassesAllRule { Zones = { 1 } } },
        };

        var seg = new Segment
        {
            UserId = User, SegmentKind = SegmentKinds.Shapes, MatchRulesJson = rules.Serialize(),
        };
        _context.Segments.Add(seg);
        await _context.SaveChangesAsync();
        return seg.SegmentId;
    }

    // -------------------------------------------------------------------------
    // inval-T1 Strava-import fakes: a stream dict whose latlng data matches the given corridor coords,
    // and an IStravaApiClient that serves it. ConvertStreamsToGpsPoints maps latlng[i] → point[i].

    private static Dictionary<string, StravaStream> StreamsFor((double lat, double lng)[] points)
    {
        var latlng = string.Join(",", points.Select(p => $"[{p.lat.ToString(System.Globalization.CultureInfo.InvariantCulture)},{p.lng.ToString(System.Globalization.CultureInfo.InvariantCulture)}]"));
        return JsonSerializer.Deserialize<Dictionary<string, StravaStream>>(
            $$"""{ "latlng": { "data": [{{latlng}}] } }""")!;
    }

    private sealed class FakeStravaApiClient : IStravaApiClient
    {
        public Dictionary<long, Dictionary<string, StravaStream>?> Streams { get; } = new();

        public Task<Dictionary<string, StravaStream>?> GetActivityStreamsAsync(long activityId)
            => Task.FromResult(Streams.TryGetValue(activityId, out var s) ? s : new Dictionary<string, StravaStream>(StringComparer.Ordinal));
        public Task<StravaDetailedActivity?> GetActivityDetailAsync(long activityId)
            => Task.FromResult<StravaDetailedActivity?>(null);
        public Task<List<StravaActivity>> GetActivitiesAsync(DateTime? after = null, int page = 1, int perPage = 30)
            => Task.FromResult(new List<StravaActivity>());
        public Task<List<StravaActivity>> GetAllActivitiesAsync(DateTime? after = null)
            => Task.FromResult(new List<StravaActivity>());
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
