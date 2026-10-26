using System.Globalization;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Matching;
using RouteBaseWeb.Services.Segments;
using RouteBaseWeb.Tests;
using RouteBaseWeb.Tests.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Pipeline;

/// <summary>
/// Characterization pins (TrackEngine TE-12/TE-19, pins P1–P13) for the SQL analysis pipeline's
/// INSERT-gate + windowing quirks (P1–P7) and the post-insert effort math — offset quality
/// guard, pair fallback, julianday effort_time, friend-effort aggregates, HR/cadence quirks and
/// reverse-direction removal (P8–P13, S8/S9/S10). These pin bug-for-bug SQL behavior that the
/// in-memory engine (MatchEngine + EffortMathCore) must reproduce exactly. Run against the
/// untouched SQL pipeline BEFORE any engine/seam code lands.
///
/// Separate fixture from SegmentAnalysisPipelineTests so those tests stay unedited and this
/// file can seed explicit, non-contiguous GPS_IDs (EF autoincrement can't produce gaps).
///
/// Coordinate facts used throughout:
///   - geo_cell = CAST(lat*500) *1e6 + CAST(lng*500); 0.002 deg lat ≈ one cell step.
///   - Points spaced 0.001 deg lat (~111 m) so each track point tight-matches exactly one
///     segment point (0.001 &gt; the 0.00015/0.0002/0.0003 tolerances).
/// </summary>
public class SegmentAnalysisGateTests : IAsyncLifetime
{
    private RouteBaseDbContext _context = null!;
    private ISegmentAnalysis _segmentService = null!;
    private IDbSchemaManager _schemaManager = null!;

    public async Task InitializeAsync()
    {
        var dbName = $"gatedb_{Guid.NewGuid():N}";
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
        // Cutover: engine-wired construction — these characterization pins now run against the
        // in-memory engine (the SQL pipeline is deleted in a later commit). Shared DirectTrackReader.
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
    // P1 — match_perc truncates, never rounds (S6.2).
    // 7 of 8 segment points matched → CAST(7*100.0/8)=CAST(87.5)=87.
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Analyze_MatchPerc_TruncatesNotRounds()
    {
        var segId = await SeedSegmentAsync(BuildSegment(6, 8)); // seg lats 47.006..47.013

        // Track hits every segment lat EXCEPT the middle 47.009 → 7 of 8 matched.
        await SeedActivityRawAsync(new[]
        {
            (1, 47.006, 11.000, (double?)null),
            (2, 47.007, 11.000, (double?)null),
            (3, 47.008, 11.000, (double?)null),
            (4, 47.010, 11.000, (double?)null),
            (5, 47.011, 11.000, (double?)null),
            (6, 47.012, 11.000, (double?)null),
            (7, 47.013, 11.000, (double?)null),
        });

        await _segmentService.AnalyzeSegmentMatchesAsync(segId);

        var effort = Assert.Single(await EffortsAsync(segId));
        Assert.Equal(8, effort.SegTotalCount);
        Assert.Equal(7, effort.SegGpsCount);
        Assert.Equal(87, effort.SegPerc); // 87.5 truncated, not rounded to 88
    }

    // -------------------------------------------------------------------------
    // P2 — gps_id_count is the ordinal span exit − entry (S7.2). The pre-BlobStore variant used
    // non-contiguous GPS_IDs within one crossing to make span ≫ point-count; ordinals are dense
    // post-migration, so only the ported formula gps_id_count = exit − entry survives (pinned on a dense
    // full crossing: 8 points → ordinals 0..7).
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Analyze_GpsIdCount_IsOrdinalSpan()
    {
        var segId = await SeedSegmentAsync(BuildSegment(6, 8));

        await SeedActivityRawAsync(new[]
        {
            (1, 47.006, 11.000, (double?)null),
            (2, 47.007, 11.000, (double?)null),
            (3, 47.008, 11.000, (double?)null),
            (4, 47.009, 11.000, (double?)null),
            (5, 47.010, 11.000, (double?)null),
            (6, 47.011, 11.000, (double?)null),
            (7, 47.012, 11.000, (double?)null),
            (8, 47.013, 11.000, (double?)null),
        });

        await _segmentService.AnalyzeSegmentMatchesAsync(segId);

        var effort = Assert.Single(await EffortsAsync(segId));
        Assert.Equal(0, effort.GpsIdEntryGuessed);        // dense ordinals 0..7
        Assert.Equal(7, effort.GpsIdExitGuessed);
        Assert.Equal(7, effort.GpsIdCount);               // exit − entry
    }

    // -------------------------------------------------------------------------
    // P3 — gap grouping is strict '>' in ordinal units (S5.1). gap threshold = MAX(300, MIN(8*10,1500)) = 300.
    // Post-BlobStore the gap is real (unmatched filler points between crossings) rather than a synthetic
    // GPS_ID jump: gap 301 → two efforts; gap exactly 300 → one effort.
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Analyze_Gap301_ProducesTwoEfforts()
    {
        var segA = await SeedSegmentAsync(BuildSegment(6, 8, lng: 11.000));
        await SeedActivityRawAsync(TwoCrossingsWithGap(11.000, fillerCount: 300)); // ordinal gap 301

        await _segmentService.AnalyzeSegmentMatchesAsync(segA);
        Assert.Equal(2, (await EffortsAsync(segA)).Count);
    }

    [Fact]
    public async Task Analyze_Gap300_ProducesOneEffort()
    {
        var segB = await SeedSegmentAsync(BuildSegment(6, 8, lng: 11.000));
        await SeedActivityRawAsync(TwoCrossingsWithGap(11.000, fillerCount: 299)); // ordinal gap 300

        await _segmentService.AnalyzeSegmentMatchesAsync(segB);
        Assert.Single(await EffortsAsync(segB));
    }

    // -------------------------------------------------------------------------
    // P4 — middle-only overlap rejected by the zone-hit gate (S7.1).
    // A track hitting only the segment middle produces 0 start/end tight hits → 0 rows.
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Analyze_MiddleOnlyMatch_RejectedByZoneGate()
    {
        var segId = await SeedSegmentAsync(BuildSegment(6, 8)); // seg lats 47.006..47.013

        // Only the middle lats (segment ids ~3–5): no coverage of the start/end zones.
        await SeedActivityRawAsync(new[]
        {
            (1, 47.008, 11.000, (double?)null),
            (2, 47.009, 11.000, (double?)null),
            (3, 47.010, 11.000, (double?)null),
        });

        await _segmentService.AnalyzeSegmentMatchesAsync(segId);
        Assert.Empty(await EffortsAsync(segId));
    }

    // -------------------------------------------------------------------------
    // P5 — distance gate: rejects a too-short effort, but MISSING distance passes (S6.1/S7.1).
    // effort_dist_m = MAX(distance) − MIN(distance); NULL distances → −1 → passes.
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Analyze_DistanceGate_RejectsShortEffort_PassesWithoutDistanceData()
    {
        // (a) full geometric match, but cumulative-distance span (70 m) < 60% of segment
        //     distance (~778 m) → rejected.
        var segA = await SeedSegmentAsync(BuildSegment(6, 8));
        await SeedActivityRawAsync(new[]
        {
            (1, 47.006, 11.000, (double?)0),
            (2, 47.007, 11.000, (double?)10),
            (3, 47.008, 11.000, (double?)20),
            (4, 47.009, 11.000, (double?)30),
            (5, 47.010, 11.000, (double?)40),
            (6, 47.011, 11.000, (double?)50),
            (7, 47.012, 11.000, (double?)60),
            (8, 47.013, 11.000, (double?)70),
        });
        await _segmentService.AnalyzeSegmentMatchesAsync(segA);
        Assert.Empty(await EffortsAsync(segA));

        // (b) identical geometry, but distance channel entirely NULL → effort_dist −1 passes.
        // Distinct lng keeps segA's track out of segB's Stage-1 candidate set.
        var segB = await SeedSegmentAsync(BuildSegment(6, 8, lng: 15.000));
        await SeedActivityRawAsync(new[]
        {
            (101, 47.006, 15.000, (double?)null),
            (102, 47.007, 15.000, (double?)null),
            (103, 47.008, 15.000, (double?)null),
            (104, 47.009, 15.000, (double?)null),
            (105, 47.010, 15.000, (double?)null),
            (106, 47.011, 15.000, (double?)null),
            (107, 47.012, 15.000, (double?)null),
            (108, 47.013, 15.000, (double?)null),
        });
        await _segmentService.AnalyzeSegmentMatchesAsync(segB);
        Assert.Single(await EffortsAsync(segB));
    }

    // -------------------------------------------------------------------------
    // P6 — match_perc threshold gate is inclusive '>=' (S7.1).
    // Produced perc = 87. threshold 87 → inserted; threshold 88 → rejected.
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Analyze_Threshold_InclusiveBoundary()
    {
        var segId = await SeedSegmentAsync(BuildSegment(6, 8));

        // 7 of 8 matched → perc 87 (same geometry as P1).
        await SeedActivityRawAsync(new[]
        {
            (1, 47.006, 11.000, (double?)null),
            (2, 47.007, 11.000, (double?)null),
            (3, 47.008, 11.000, (double?)null),
            (4, 47.010, 11.000, (double?)null),
            (5, 47.011, 11.000, (double?)null),
            (6, 47.012, 11.000, (double?)null),
            (7, 47.013, 11.000, (double?)null),
        });

        // threshold == perc → included (>=).
        await _segmentService.AnalyzeSegmentMatchesAsync(segId, matchThresholdPercent: 87);
        Assert.Single(await EffortsAsync(segId));

        // one above perc → excluded (re-analyze deletes own efforts first).
        await _segmentService.AnalyzeSegmentMatchesAsync(segId, matchThresholdPercent: 88);
        Assert.Empty(await EffortsAsync(segId));
    }

    // -------------------------------------------------------------------------
    // P7 — 100-point zone-hit rule (S7.1): total_points < 100 OR (start >= 2 AND end >= 2).
    // A 100-pt segment with exactly 1 tight hit per zone → rejected;
    // an otherwise-identical 99-pt segment → accepted.
    // Threshold 0 neutralizes the perc gate so only the zone-hit rule differs.
    // -------------------------------------------------------------------------
    // Each track hits exactly the segment's start pin and end pin — the only two matched points,
    // one tight hit per zone. Placing them at the pins (not the zone interior) keeps
    // Offset_entry/exit ≈ 0 so the post-insert offset quality guard (~0.005) can't delete the
    // surviving effort. Threshold 0 neutralizes the perc gate, so ONLY the 100-point zone-hit rule
    // decides the outcome.
    [Fact]
    public async Task Analyze_ZoneHit_100Point_Rejected()
    {
        // 100-pt segment (pins at 47.000 / 47.099). total_points >= 100 → the gate needs
        // start_zone_hits >= 2 AND end_zone_hits >= 2; we supply 1 each → rejected at INSERT.
        var seg100 = await SeedSegmentAsync(BuildLinearSegment(100, lng: 11.000));
        await SeedActivityRawAsync(ZoneHitTrack(11.000, 47.099)); // span clears the min-span gate
        await _segmentService.AnalyzeSegmentMatchesAsync(seg100, matchThresholdPercent: 0);
        Assert.Empty(await EffortsAsync(seg100));
    }

    [Fact]
    public async Task Analyze_ZoneHit_99Point_Accepted()
    {
        // 99-pt segment (pins at 47.000 / 47.098), same 1 tight hit per zone.
        // total_points < 100 → the OR arm passes with just >0 hits → 1 row.
        var seg99 = await SeedSegmentAsync(BuildLinearSegment(99, lng: 11.000));
        await SeedActivityRawAsync(ZoneHitTrack(11.000, 47.098)); // span clears the min-span gate
        await _segmentService.AnalyzeSegmentMatchesAsync(seg99, matchThresholdPercent: 0);
        Assert.Single(await EffortsAsync(seg99));
    }

    // -------------------------------------------------------------------------
    // P8 — offset quality guard deletes an effort whose exit stays far from the
    // end pin (S8.13). 10-pt segment; end pin (ordinal 9) is displaced +0.006° in
    // lng, so the track (matching ordinals 0–8, passing the INSERT gate via its
    // end-zone hits on ordinals 7/8) is ≥0.007 from the end pin → Offset_exit
    // > 0.005 → deleted. Distances NULL so the INSERT distance gate passes and the
    // offset pair guard is bypassed (NULL cum), isolating the exit-offset guard.
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Analyze_OffsetGuard_DeletesEffortWithFarExitPin()
    {
        var segPts = new (double lat, double lng)[10];
        for (int i = 0; i < 9; i++) segPts[i] = (47.000 + i * 0.001, 11.000);
        segPts[9] = (47.009, 11.006);                 // end pin displaced far in lng
        var segId = await SeedSegmentCustomAsync(segPts, nullDistance: true);

        // Track covers ordinals 0..8 (lat 47.000..47.008, lng 11.000); never nears the end pin.
        var rows = new GpsRow[9];
        for (int i = 0; i < 9; i++) rows[i] = new GpsRow(i + 1, 47.000 + i * 0.001, 11.000);
        await SeedTrackAsync(rows);

        await _segmentService.AnalyzeSegmentMatchesAsync(segId);
        Assert.Empty(await EffortsAsync(segId));       // exit offset ≈0.007 > 0.005 → guard deletes
    }

    // -------------------------------------------------------------------------
    // P9 — no pair survives the 10% guard → entry/exit fallback (S8.11/S8.12).
    // segment_distance NULL makes every both-cum-present pair fail the guard
    // (`0 >= NULL` is unknown), so calculated ids fall back to the guessed ids and
    // the offsets become the raw Round5 pin distances at those guessed GPS rows.
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Analyze_OffsetFallback_WhenAllPairsFailTenPctGuard()
    {
        var segPts = new (double lat, double lng)[8];
        for (int i = 0; i < 8; i++) segPts[i] = (47.000 + i * 0.001, 11.000);
        var segId = await SeedSegmentCustomAsync(segPts, nullDistance: true); // NULL segment_distance

        // Track matches interior ordinals 1..6 (not the pins). Constant distance channel present.
        var rows = new GpsRow[6];
        for (int i = 0; i < 6; i++) rows[i] = new GpsRow(i + 1, 47.001 + i * 0.001, 11.000, Dist: 500);
        await SeedTrackAsync(rows);

        await _segmentService.AnalyzeSegmentMatchesAsync(segId);

        var effort = Assert.Single(await EffortsAsync(segId));
        Assert.Equal(effort.GpsIdEntryGuessed, effort.GpsIdEntryCalculated); // fallback: calc == guessed
        Assert.Equal(effort.GpsIdExitGuessed,  effort.GpsIdExitCalculated);
        Assert.Equal(0, effort.GpsIdEntryGuessed);       // first matched ordinal (0 = 47.001)
        Assert.Equal(5, effort.GpsIdExitGuessed);        // last matched ordinal (5 = 47.006)
        Assert.Equal(SqliteCompat.Round5(0.001), effort.OffsetEntry); // |47.000 − 47.001| pin distance
        Assert.Equal(SqliteCompat.Round5(0.001), effort.OffsetExit);  // |47.007 − 47.006|
    }

    // -------------------------------------------------------------------------
    // P10 — effort_time is the julianday value, NOT the exact integer second
    // difference (S9.5). Pins the frozen SQL against the SqliteCompat replica:
    // 1815 whole seconds → stored value 1815.0000125… (CAST-sensitive fingerprint).
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Analyze_EffortTime_IsJuliandayValue_NotIntegerSeconds()
    {
        var segPts = new (double lat, double lng)[8];
        for (int i = 0; i < 8; i++) segPts[i] = (47.000 + i * 0.001, 11.000);
        var segId = await SeedSegmentCustomAsync(segPts);

        var baseUtc = new DateTime(2021, 6, 15, 10, 0, 0, DateTimeKind.Utc);
        int[] secs = { 0, 100, 200, 300, 400, 500, 700, 1815 };  // min 0, max 1815
        var rows = new GpsRow[8];
        for (int i = 0; i < 8; i++)
            rows[i] = new GpsRow(i + 1, 47.000 + i * 0.001, 11.000,
                Dist: i * 111.0,                                  // span 777 ≥ 60% of ~778 m
                DateTimeUtc: baseUtc.AddSeconds(secs[i]).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        await SeedTrackAsync(rows);

        await _segmentService.AnalyzeSegmentMatchesAsync(segId);

        var effort = Assert.Single(await EffortsAsync(segId));
        double minEpoch = (baseUtc - DateTime.UnixEpoch).TotalSeconds;
        double maxEpoch = (baseUtc.AddSeconds(1815) - DateTime.UnixEpoch).TotalSeconds;
        double expected = SqliteCompat.EffortTimeSeconds(minEpoch, maxEpoch);

        Assert.NotNull(effort.EffortTime);
        Assert.Equal(expected, effort.EffortTime!.Value);   // bit-exact julianday replica
        Assert.NotEqual(1815.0, effort.EffortTime!.Value);  // NOT the exact integer second diff
    }

    // -------------------------------------------------------------------------
    // P11 — friend-track efforts get aggregates (any owner) but never offsets
    // (own-only) (S9.1 + S8.1). Pre-seed a friend effort (guessed window only) on
    // an own segment; after analyze its distance/time are filled while
    // gps_id_entry_calculated stays NULL.
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Analyze_Aggregates_FillFriendEffort_WithoutOffsets()
    {
        var segPts = new (double lat, double lng)[8];
        for (int i = 0; i < 8; i++) segPts[i] = (47.000 + i * 0.001, 11.000);
        var segId = await SeedSegmentCustomAsync(segPts);

        await SeedUserAsync("friend-user");
        var baseUtc = new DateTime(2021, 6, 15, 10, 0, 0, DateTimeKind.Utc);
        var rows = new GpsRow[6];
        for (int i = 0; i < 6; i++)
            rows[i] = new GpsRow(100 + i, 47.001 + i * 0.001, 11.000,
                Dist: i * 100.0,                              // span 500
                DateTimeUtc: baseUtc.AddSeconds(i * 60).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        var friendTrack = await SeedTrackAsync(rows, userId: "friend-user");
        await SeedGuessedEffortAsync(segId, new TrackId(friendTrack), entryGuessed: 0, exitGuessed: 5);   // friend track dense ordinals 0..5

        await _segmentService.AnalyzeSegmentMatchesAsync(segId);

        var effort = Assert.Single(await EffortsAsync(segId));
        Assert.Equal(new TrackId(friendTrack), effort.TrackId);
        Assert.NotNull(effort.EffortDistance);
        Assert.Equal(500.0, effort.EffortDistance!.Value);   // MAX−MIN over friend window
        Assert.NotNull(effort.EffortTime);
        Assert.Null(effort.GpsIdEntryCalculated);            // offsets never ran on the friend row
    }

    // -------------------------------------------------------------------------
    // P12 — HR averages positive samples only; cadence averages over power-present
    // points with NULL cadence counted as 0 (S9.7/S9.9). Uses a friend effort so
    // the hand-crafted channel window survives the own-effort delete.
    // hr {0,NULL,120,140} → 130; power on 2 pts {200,250} → 225;
    // cadence {90@no-power, NULL, NULL@power, 80@power} → (0+80)/2 = 40.
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Analyze_Aggregates_HrPositiveOnly_CadenceOverPowerPoints()
    {
        var segPts = new (double lat, double lng)[8];
        for (int i = 0; i < 8; i++) segPts[i] = (47.000 + i * 0.001, 11.000);
        var segId = await SeedSegmentCustomAsync(segPts);

        await SeedUserAsync("friend-user");
        var rows = new[]
        {
            new GpsRow(200, 47.001, 11.000, HeartRate: 0,    Power: null, Cadence: 90),
            new GpsRow(201, 47.002, 11.000, HeartRate: null, Power: null, Cadence: null),
            new GpsRow(202, 47.003, 11.000, HeartRate: 120,  Power: 200,  Cadence: null),
            new GpsRow(203, 47.004, 11.000, HeartRate: 140,  Power: 250,  Cadence: 80),
        };
        var friendTrack = await SeedTrackAsync(rows, userId: "friend-user");
        await SeedGuessedEffortAsync(segId, new TrackId(friendTrack), entryGuessed: 0, exitGuessed: 3);   // friend track dense ordinals 0..3

        await _segmentService.AnalyzeSegmentMatchesAsync(segId);

        var effort = Assert.Single(await EffortsAsync(segId));
        Assert.Equal(130.0, effort.EffortHrAvg);       // (120 + 140) / 2, 0 and NULL excluded
        Assert.Equal(225.0, effort.EffortPowerAvg);    // (200 + 250) / 2
        Assert.Equal(40.0,  effort.EffortCadenceAvg);  // (0 + 80) / COUNT(power)=2 ; 90 excluded (no power)
    }

    // -------------------------------------------------------------------------
    // P13 — a strictly reverse traversal passes the (direction-symmetric) INSERT
    // gate, then the direction validator removes it (down-transitions > up) → 0
    // rows. Pins Direction.cs behavior before TE-18's TrackReader swap.
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Analyze_ReverseDirectionTrack_RemovedByValidation()
    {
        var segPts = new (double lat, double lng)[8];
        for (int i = 0; i < 8; i++) segPts[i] = (47.000 + i * 0.001, 11.000);
        var segId = await SeedSegmentCustomAsync(segPts, nullDistance: true);

        // Same geometry as the segment but traversed high→low lat under ascending GPS_IDs.
        var rows = new GpsRow[8];
        for (int i = 0; i < 8; i++) rows[i] = new GpsRow(i + 1, 47.007 - i * 0.001, 11.000);
        await SeedTrackAsync(rows);

        await _segmentService.AnalyzeSegmentMatchesAsync(segId);
        Assert.Empty(await EffortsAsync(segId));
    }

    // -------------------------------------------------------------------------
    // Geometry + seed helpers
    // -------------------------------------------------------------------------

    // Segment of 'count' points starting at track index 'startIdx' (lat 47.000 + idx*0.001).
    // 'lng' isolates scenarios: a distinct lng keeps a segment's bbox disjoint from another
    // scenario's tracks so Stage 1 never cross-matches them.
    private static (double lat, double lng)[] BuildSegment(int startIdx, int count, double lng = 11.000)
    {
        var pts = new (double lat, double lng)[count];
        for (int i = 0; i < count; i++)
            pts[i] = (47.000 + (startIdx + i) * 0.001, lng);
        return pts;
    }

    // Linear segment of 'count' points from lat 47.000 upward (0.001 deg step).
    private static (double lat, double lng)[] BuildLinearSegment(int count, double lng = 11.000)
    {
        var pts = new (double lat, double lng)[count];
        for (int i = 0; i < count; i++)
            pts[i] = (47.000 + i * 0.001, lng);
        return pts;
    }

    // One full traversal of BuildSegment(6,8): 8 points at lats 47.006..47.013, GPS_IDs
    // startId..startId+7, no distance.
    private static IEnumerable<(int, double, double, double?)> BuildTraversalRaw(int startId, double lng)
    {
        for (int i = 0; i < 8; i++)
            yield return (startId + i, 47.006 + i * 0.001, lng, (double?)null);
    }

    // Two 8-point crossings separated by `fillerCount` unmatched points parked far away in latitude (they
    // never hit a zone but still occupy ordinals), giving the matched points an ordinal gap of
    // fillerCount + 1 — the dense-ordinal analogue of the old GPS_ID jump. Seed gpsIds are ignored (the
    // blob re-ramps to 0..n-1); the array order is what matters.
    private static (int, double, double, double?)[] TwoCrossingsWithGap(double lng, int fillerCount)
    {
        var list = new List<(int, double, double, double?)>();
        int id = 1;
        for (int i = 0; i < 8; i++) list.Add((id++, 47.006 + i * 0.001, lng, (double?)null));
        for (int i = 0; i < fillerCount; i++) list.Add((id++, 48.500 + i * 0.00001, lng, (double?)null));
        for (int i = 0; i < 8; i++) list.Add((id++, 47.006 + i * 0.001, lng, (double?)null));
        return list.ToArray();
    }

    // One start-zone hit + one end-zone hit separated by 15 unmatched filler points, so the effort's ordinal
    // span (16) clears the min-span gate (>= max(5, n*0.1)); acceptance is then governed by the zone-hit
    // 100-boundary alone. Replaces the pre-BlobStore 2-point tracks whose GPS_ID gap faked the span.
    private static (int, double, double, double?)[] ZoneHitTrack(double lng, double endLat)
    {
        var list = new List<(int, double, double, double?)>();
        int id = 1;
        list.Add((id++, 47.000, lng, (double?)null));
        for (int i = 0; i < 15; i++) list.Add((id++, 48.500 + i * 0.00001, lng, (double?)null));
        list.Add((id++, endLat, lng, (double?)null));
        return list.ToArray();
    }

    // Seeds an activity and its points as a packed Track_Blob (was raw GPS rows). geo_cell is gone —
    // the engine hashes cells in C#. gpsIds in the tuples are ignored; the blob addresses by ordinal.
    private async Task<int> SeedActivityRawAsync((int gpsId, double lat, double lng, double? dist)[] pts)
    {
        var activity = new Activity
        {
            StartGpsLat  = pts[0].lat,
            StartGpsLong = pts[0].lng,
            EndGpsLat    = pts[^1].lat,
            EndGpsLong   = pts[^1].lng,
            UserId       = "test-user",
        };
        _context.Activities.Add(activity);
        await _context.SaveChangesAsync();
        TrackId trackId = activity.TrackId;

        // Points seeded in array order (ascending original GPS_ID) → dense ordinals 0..n-1 in the blob.
        var gpsRows = pts.Select(p => new GpsData { GpsLat = p.lat, GpsLong = p.lng, Distance = p.dist }).ToList();
        await TrackSeed.SeedTrackAsync(_context, trackId, gpsRows);

        await _context.Database.ExecuteSqlRawAsync(
            @"INSERT OR IGNORE INTO Activity_Bbox (track_ID, min_lat, max_lat, min_long, max_long)
              VALUES ({0}, {1}, {2}, {3}, {4})",
            trackId.Value,
            pts.Min(p => p.lat), pts.Max(p => p.lat),
            pts.Min(p => p.lng), pts.Max(p => p.lng));

        return trackId.Value;
    }

    // Full-channel GPS row for the effort-math pins (P8–P13). Nulls stay SQL NULL.
    private sealed record GpsRow(
        int GpsId, double Lat, double Lng, double? Dist = null, string? DateTimeUtc = null,
        double? Speed = null, double? Altitude = null, double? HeartRate = null,
        double? Cadence = null, double? Power = null);

    // Seeds an activity + all-channel GPS rows (explicit GPS_IDs, geo_cell backfilled by the
    // pipeline) for the given owner. Datetime is written as raw 19-char TEXT so julianday()
    // parses it as whole seconds (bypasses EF's fractional DateTime formatting).
    private async Task<int> SeedTrackAsync(GpsRow[] pts, string userId = "test-user")
    {
        var activity = new Activity
        {
            StartGpsLat  = pts[0].Lat,
            StartGpsLong = pts[0].Lng,
            EndGpsLat    = pts[^1].Lat,
            EndGpsLong   = pts[^1].Lng,
            UserId       = userId,
        };
        _context.Activities.Add(activity);
        await _context.SaveChangesAsync();
        TrackId trackId = activity.TrackId;

        // Points in array order (ascending original GPS_ID) → dense ordinals in the blob. Whole-second
        // datetimes round-trip exactly through the codec's 1-s time quantization (julianday parity holds).
        var gpsRows = pts.Select(p => new GpsData
        {
            GpsLat = p.Lat, GpsLong = p.Lng, Distance = p.Dist,
            DateTimeUtc = p.DateTimeUtc == null
                ? null
                : DateTime.SpecifyKind(DateTime.Parse(p.DateTimeUtc, CultureInfo.InvariantCulture), DateTimeKind.Utc),
            Speed = p.Speed, Altitude = p.Altitude, HeartRate = p.HeartRate, Cadence = p.Cadence, Power = p.Power,
        }).ToList();
        await TrackSeed.SeedTrackAsync(_context, trackId, gpsRows);

        await _context.Database.ExecuteSqlRawAsync(
            @"INSERT OR IGNORE INTO Activity_Bbox (track_ID, min_lat, max_lat, min_long, max_long)
              VALUES ({0}, {1}, {2}, {3}, {4})",
            trackId.Value,
            pts.Min(p => p.Lat), pts.Max(p => p.Lat),
            pts.Min(p => p.Lng), pts.Max(p => p.Lng));

        return trackId.Value;
    }

    // Seeds a polyline segment from explicit points; nullDistance leaves SegmentDistance NULL
    // (exercises the S8.8 / INSERT-gate NULL-distance branches).
    private async Task<SegmentId> SeedSegmentCustomAsync((double lat, double lng)[] points, bool nullDistance = false)
    {
        double? dist = null;
        if (!nullDistance)
        {
            double d = 0;
            for (int i = 1; i < points.Length; i++)
                d += HaversineMeters(points[i - 1].lat, points[i - 1].lng, points[i].lat, points[i].lng);
            dist = d;
        }

        var segment = new Segment { UserId = "test-user", SegmentDistance = dist };
        _context.Segments.Add(segment);
        await _context.SaveChangesAsync();
        var segId = segment.SegmentId;

        foreach (var (lat, lng) in points)
            _context.SegmentGps.Add(new SegmentGps { SegmentId = segId, GpsLat = lat, GpsLong = lng });
        await _context.SaveChangesAsync();

        return segId;
    }

    private async Task SeedUserAsync(string userId)
    {
        _context.Users.Add(new AppUser
        {
            UserId      = userId,
            DisplayName = userId,
            Email       = $"{userId}@test.com",
            IsAdmin     = false,
            CreatedAt   = DateTime.UtcNow.ToString("O"),
            LastLogin   = DateTime.UtcNow.ToString("O"),
        });
        await _context.SaveChangesAsync();
    }

    // Pre-seeds a GPSinSegment effort with a guessed window only (calculated/offsets/aggregates
    // NULL) — models a friend effort that survives the own-only delete and gets aggregates but
    // never offsets.
    private async Task SeedGuessedEffortAsync(SegmentId segId, TrackId trackId, int entryGuessed, int exitGuessed)
    {
        _context.GpsInSegments.Add(new GpsInSegment
        {
            SegmentId         = segId,
            TrackId           = trackId,
            GpsIdEntryGuessed = entryGuessed,
            GpsIdExitGuessed  = exitGuessed,
        });
        await _context.SaveChangesAsync();
    }

    private async Task<SegmentId> SeedSegmentAsync((double lat, double lng)[] points)
    {
        double dist = 0;
        for (int i = 1; i < points.Length; i++)
            dist += HaversineMeters(points[i - 1].lat, points[i - 1].lng, points[i].lat, points[i].lng);

        var segment = new Segment { UserId = "test-user", SegmentDistance = dist };
        _context.Segments.Add(segment);
        await _context.SaveChangesAsync();
        var segId = segment.SegmentId;

        foreach (var (lat, lng) in points)
            _context.SegmentGps.Add(new SegmentGps { SegmentId = segId, GpsLat = lat, GpsLong = lng });
        await _context.SaveChangesAsync();

        return segId;
    }

    // AsNoTracking bypasses EF's change-tracker (the direction validator loads efforts
    // mid-pipeline, leaving stale snapshots otherwise — see SegmentAnalysisPipelineTests).
    private Task<List<GpsInSegment>> EffortsAsync(SegmentId segId) =>
        _context.GpsInSegments.AsNoTracking().Where(e => e.SegmentId == segId).ToListAsync();

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
