using System.Globalization;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Geometry;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Matching;
using RouteBaseWeb.Services.Segments;
using RouteBaseWeb.Services.Segments.Rules;
using RouteBaseWeb.Services.Tracks;
using RouteBaseWeb.Tests.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Matching;

/// <summary>
/// Cutover engine pins (TrackEngine TE-25). The SQL pipeline is deleted, so these are no longer
/// dual-mode parity comparisons — each scenario runs the in-memory engine
/// (<see cref="MatchEngineService"/> + <see cref="EffortMathService"/>) once and asserts the concrete
/// GPSinSegment output directly. They mirror the SQL characterization pins
/// (SegmentAnalysisGateTests P1–P13) that pinned the same bug-for-bug quirks, plus engine-specific
/// contracts (own-scope candidates, insert ordering, teardown, decode-null fallback).
///
/// Each pin seeds a fresh in-memory SQLite DB, analyzes, and reads GPSinSegment back. The engine's
/// <see cref="ITrackReader"/> decodes from the same (pinned) context — the Open/Close ref-count nests
/// safely inside the analysis window (S0.4).
/// </summary>
public class MatchEngineServiceTests
{
    // -------------------------------------------------------------------------
    // Engine-wired harness
    // -------------------------------------------------------------------------

    private sealed class TestDb : IAsyncDisposable
    {
        public RouteBaseDbContext Context = null!;
        public ISegmentAnalysis Service = null!;

        // effortReaderOverride lets the decode-null pins wire the effort-math engine to a reader that
        // always returns null while the match engine keeps a real reader (so a row is still inserted).
        public static async Task<TestDb> CreateAsync(ITrackReader? effortReaderOverride = null)
        {
            var dbName = $"enginedb_{Guid.NewGuid():N}";
            var options = new DbContextOptionsBuilder<RouteBaseDbContext>()
                .UseSqlite($"Data Source=file:{dbName}?mode=memory&cache=shared")
                .Options;

            var db = new TestDb { Context = new RouteBaseDbContext(options) };
            await db.Context.Database.EnsureCreatedAsync();

            var schema = new DbSchemaManager(db.Context);
            await schema.EnsureActivityBboxTableAsync();
            await schema.EnsureTrackContentTablesAsync();
            await schema.EnsureIndexesAsync();

            db.Context.Users.Add(new AppUser
            {
                UserId = "test-user", DisplayName = "Test User", Email = "test@test.com",
                IsAdmin = false, CreatedAt = DateTime.UtcNow.ToString("O"), LastLogin = DateTime.UtcNow.ToString("O"),
            });
            await db.Context.SaveChangesAsync();

            var cache = new MemoryCache(new MemoryCacheOptions());
            var settings = new ProfileSettingsService(db.Context, NullLogger<ProfileSettingsService>.Instance, cache, TimeProvider.System);
            var trackReader = new DirectTrackReader(db.Context);
            var shapeMatcher = new ShapeSegmentMatcher(db.Context, NullLogger<ShapeSegmentMatcher>.Instance, trackReader);
            var engine = new MatchEngineService(db.Context, trackReader, NullLogger<MatchEngineService>.Instance);
            var effortMath = new EffortMathService(
                db.Context, effortReaderOverride ?? trackReader, NullLogger<EffortMathService>.Instance);

            db.Service = SegmentServices.Create(
                db.Context, cache, new TestVisibilityService(), new UserCacheInvalidator(), settings,
                shapeMatcher, engine, effortMath, trackReader).Analysis;
            return db;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.Database.EnsureDeletedAsync();
            await Context.DisposeAsync();
        }
    }

    // Always returns null → drives the EffortMath decode-null fallback (S8.11/12, S9.10).
    private sealed class NullTrackReader : ITrackReader
    {
        public Task<DecodedTrack?> GetTrackAsync(TrackId trackId, CancellationToken ct = default)
            => Task.FromResult<DecodedTrack?>(null);
    }

    private static Task<List<GpsInSegment>> EffortsAsync(RouteBaseDbContext ctx, SegmentId segId) =>
        ctx.GpsInSegments.AsNoTracking().Where(e => e.SegmentId == segId).ToListAsync();

    // -------------------------------------------------------------------------
    // Converted match/gate pins (mirror SegmentAnalysisGateTests P1–P7 + pipeline scenarios)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Parity_BothTracks()
    {
        await using var db = await TestDb.CreateAsync();
        await SeedActivityAsync(db.Context, BuildTrack(0.0));
        await SeedActivityAsync(db.Context, BuildTrack(0.0001));
        var segId = await SeedSegmentAsync(db.Context, BuildSegment(6, 8));
        await db.Service.AnalyzeSegmentMatchesAsync(segId);
        Assert.Equal(2, (await EffortsAsync(db.Context, segId)).Count);
    }

    [Fact]
    public async Task Parity_NoCross()
    {
        await using var db = await TestDb.CreateAsync();
        await SeedActivityAsync(db.Context, new[] { (48.0, 12.0), (48.001, 12.0), (48.002, 12.0) });
        var segId = await SeedSegmentAsync(db.Context, BuildSegment(6, 8));
        await db.Service.AnalyzeSegmentMatchesAsync(segId);
        Assert.Empty(await EffortsAsync(db.Context, segId));
    }

    [Fact]
    public async Task Parity_ReRunIdempotent()
    {
        await using var db = await TestDb.CreateAsync();
        await SeedActivityAsync(db.Context, BuildTrack(0.0));
        var segId = await SeedSegmentAsync(db.Context, BuildSegment(6, 8));
        await db.Service.AnalyzeSegmentMatchesAsync(segId);
        await db.Service.AnalyzeSegmentMatchesAsync(segId);   // second run must not duplicate.
        Assert.Single(await EffortsAsync(db.Context, segId));
    }

    [Fact]
    public async Task Parity_AnalyzeAll_TwoSegments()
    {
        await using var db = await TestDb.CreateAsync();
        await SeedActivityAsync(db.Context, BuildTrack(0.0));
        var seg1 = await SeedSegmentAsync(db.Context, BuildSegment(1, 8));
        var seg2 = await SeedSegmentAsync(db.Context, BuildSegment(11, 8));
        await db.Service.ClearAllSegmentMatchesAsync("test-user");
        await db.Service.AnalyzeSegmentMatchesAsync(seg1);
        await db.Service.AnalyzeSegmentMatchesAsync(seg2);
        Assert.Single(await EffortsAsync(db.Context, seg1));
        Assert.Single(await EffortsAsync(db.Context, seg2));
    }

    [Fact]
    public async Task Parity_LoopSegment()
    {
        await using var db = await TestDb.CreateAsync();
        var trackPts = new (double lat, double lng)[]
        {
            (47.000, 11.000), (47.001, 11.000), (47.002, 11.000), (47.003, 11.000),
            (47.004, 11.000), (47.004, 11.001), (47.003, 11.001), (47.002, 11.001),
            (47.002, 11.000), (47.001, 11.000), (47.000, 11.000),
        };
        var segPts = new (double lat, double lng)[]
        {
            (47.002, 11.000), (47.003, 11.000), (47.004, 11.000), (47.004, 11.001),
            (47.003, 11.001), (47.002, 11.001), (47.002, 11.000),
        };
        await SeedActivityWithDistancesAsync(db.Context, trackPts);
        var segId = await SeedSegmentAsync(db.Context, segPts);
        await db.Service.AnalyzeSegmentMatchesAsync(segId);

        var effort = Assert.Single(await EffortsAsync(db.Context, segId));
        Assert.True(effort.GpsIdEntryCalculated < effort.GpsIdExitCalculated,
            $"entry {effort.GpsIdEntryCalculated} must precede exit {effort.GpsIdExitCalculated}");
    }

    [Fact]
    public async Task Parity_MultiPassLinear()
    {
        await using var db = await TestDb.CreateAsync();
        var trackPts = new (double lat, double lng)[]
        {
            (47.013, 11.000), (47.010, 11.000), (47.007, 11.000), (47.006, 11.000),
            (47.005, 11.000), (47.006, 11.000), (47.007, 11.000), (47.008, 11.000),
            (47.009, 11.000), (47.010, 11.000), (47.011, 11.000), (47.012, 11.000),
            (47.013, 11.000),
        };
        var segPts = new (double lat, double lng)[]
        {
            (47.006, 11.000), (47.007, 11.000), (47.008, 11.000), (47.009, 11.000),
            (47.010, 11.000), (47.011, 11.000), (47.012, 11.000), (47.013, 11.000),
        };
        await SeedActivityWithDistancesAsync(db.Context, trackPts);
        var segId = await SeedSegmentAsync(db.Context, segPts);
        await db.Service.AnalyzeSegmentMatchesAsync(segId);
        Assert.Single(await EffortsAsync(db.Context, segId));
    }

    // P1 — match_perc truncates (7/8 → 87).
    [Fact]
    public async Task Parity_MatchPercTruncates()
    {
        await using var db = await TestDb.CreateAsync();
        var segId = await SeedSegmentAsync(db.Context, BuildSegment(6, 8));
        await SeedActivityRawAsync(db.Context, new[]
        {
            (1, 47.006, 11.000, (double?)null), (2, 47.007, 11.000, (double?)null),
            (3, 47.008, 11.000, (double?)null), (4, 47.010, 11.000, (double?)null),
            (5, 47.011, 11.000, (double?)null), (6, 47.012, 11.000, (double?)null),
            (7, 47.013, 11.000, (double?)null),
        });
        await db.Service.AnalyzeSegmentMatchesAsync(segId);

        var effort = Assert.Single(await EffortsAsync(db.Context, segId));
        Assert.Equal(8, effort.SegTotalCount);
        Assert.Equal(7, effort.SegGpsCount);
        Assert.Equal(87, effort.SegPerc);   // 87.5 truncated, not rounded to 88.
    }

    // P2 — gps_id_count is the ordinal span exit − entry. (The pre-BlobStore variant used non-contiguous
    // GPS_IDs within one crossing to make span ≠ point-count; ordinals are dense post-migration, so the
    // span-vs-gaps distinction is unrepresentable — the ported formula gps_id_count = exit − entry is what
    // survives and is pinned here on a dense full crossing.)
    [Fact]
    public async Task Parity_GpsIdCountIsOrdinalSpan()
    {
        await using var db = await TestDb.CreateAsync();
        var segId = await SeedSegmentAsync(db.Context, BuildSegment(6, 8));
        await SeedActivityRawAsync(db.Context, new[]
        {
            (1, 47.006, 11.000, (double?)null), (2, 47.007, 11.000, (double?)null),
            (3, 47.008, 11.000, (double?)null), (4, 47.009, 11.000, (double?)null),
            (5, 47.010, 11.000, (double?)null), (6, 47.011, 11.000, (double?)null),
            (7, 47.012, 11.000, (double?)null), (8, 47.013, 11.000, (double?)null),
        });
        await db.Service.AnalyzeSegmentMatchesAsync(segId);

        var effort = Assert.Single(await EffortsAsync(db.Context, segId));
        Assert.Equal(0, effort.GpsIdEntryGuessed);        // dense ordinals 0..7
        Assert.Equal(7, effort.GpsIdExitGuessed);
        Assert.Equal(7, effort.GpsIdCount);               // exit − entry
    }

    // P3 split A — ordinal gap 301 (> 300) between two crossings → two efforts. Post-BlobStore the gap is
    // real (300 unmatched filler points between the crossings) rather than a synthetic GPS_ID jump.
    [Fact]
    public async Task Parity_Gap301_TwoEfforts()
    {
        await using var db = await TestDb.CreateAsync();
        var segId = await SeedSegmentAsync(db.Context, BuildSegment(6, 8, 11.000));
        await SeedActivityRawAsync(db.Context, TwoCrossingsWithGap(11.000, fillerCount: 300)); // gap 301
        await db.Service.AnalyzeSegmentMatchesAsync(segId);
        Assert.Equal(2, (await EffortsAsync(db.Context, segId)).Count);
    }

    // P3 split B — ordinal gap exactly 300 (not > 300) → one effort.
    [Fact]
    public async Task Parity_Gap300_OneEffort()
    {
        await using var db = await TestDb.CreateAsync();
        var segId = await SeedSegmentAsync(db.Context, BuildSegment(6, 8, 11.000));
        await SeedActivityRawAsync(db.Context, TwoCrossingsWithGap(11.000, fillerCount: 299)); // gap 300
        await db.Service.AnalyzeSegmentMatchesAsync(segId);
        Assert.Single(await EffortsAsync(db.Context, segId));
    }

    // P4 — middle-only overlap rejected by the zone-hit gate.
    [Fact]
    public async Task Parity_MiddleOnlyRejected()
    {
        await using var db = await TestDb.CreateAsync();
        var segId = await SeedSegmentAsync(db.Context, BuildSegment(6, 8));
        await SeedActivityRawAsync(db.Context, new[]
        {
            (1, 47.008, 11.000, (double?)null), (2, 47.009, 11.000, (double?)null),
            (3, 47.010, 11.000, (double?)null),
        });
        await db.Service.AnalyzeSegmentMatchesAsync(segId);
        Assert.Empty(await EffortsAsync(db.Context, segId));
    }

    // P5 split A — distance gate rejects a short effort.
    [Fact]
    public async Task Parity_DistanceGate_ShortEffortRejected()
    {
        await using var db = await TestDb.CreateAsync();
        var segId = await SeedSegmentAsync(db.Context, BuildSegment(6, 8));
        await SeedActivityRawAsync(db.Context, new[]
        {
            (1, 47.006, 11.000, (double?)0), (2, 47.007, 11.000, (double?)10),
            (3, 47.008, 11.000, (double?)20), (4, 47.009, 11.000, (double?)30),
            (5, 47.010, 11.000, (double?)40), (6, 47.011, 11.000, (double?)50),
            (7, 47.012, 11.000, (double?)60), (8, 47.013, 11.000, (double?)70),
        });
        await db.Service.AnalyzeSegmentMatchesAsync(segId);
        Assert.Empty(await EffortsAsync(db.Context, segId));
    }

    // P5 split B — missing distance channel passes the gate.
    [Fact]
    public async Task Parity_DistanceGate_MissingDistancePasses()
    {
        await using var db = await TestDb.CreateAsync();
        var segId = await SeedSegmentAsync(db.Context, BuildSegment(6, 8, 15.000));
        await SeedActivityRawAsync(db.Context, new[]
        {
            (101, 47.006, 15.000, (double?)null), (102, 47.007, 15.000, (double?)null),
            (103, 47.008, 15.000, (double?)null), (104, 47.009, 15.000, (double?)null),
            (105, 47.010, 15.000, (double?)null), (106, 47.011, 15.000, (double?)null),
            (107, 47.012, 15.000, (double?)null), (108, 47.013, 15.000, (double?)null),
        });
        await db.Service.AnalyzeSegmentMatchesAsync(segId);
        Assert.Single(await EffortsAsync(db.Context, segId));
    }

    // P6 split A — threshold gate inclusive '>=' (perc 87, threshold 87 → in).
    [Fact]
    public async Task Parity_ThresholdInclusive_87In()
    {
        await using var db = await TestDb.CreateAsync();
        var segId = await SeedSegmentAsync(db.Context, BuildSegment(6, 8));
        await SeedActivityRawAsync(db.Context, ThresholdTrack());
        await db.Service.AnalyzeSegmentMatchesAsync(segId, matchThresholdPercent: 87);
        Assert.Single(await EffortsAsync(db.Context, segId));
    }

    // P6 split B — threshold one above perc → excluded.
    [Fact]
    public async Task Parity_ThresholdInclusive_88Out()
    {
        await using var db = await TestDb.CreateAsync();
        var segId = await SeedSegmentAsync(db.Context, BuildSegment(6, 8));
        await SeedActivityRawAsync(db.Context, ThresholdTrack());
        await db.Service.AnalyzeSegmentMatchesAsync(segId, matchThresholdPercent: 88);
        Assert.Empty(await EffortsAsync(db.Context, segId));
    }

    // P7 split A — 100-pt segment, 1 tight hit per zone → rejected.
    [Fact]
    public async Task Parity_ZoneHit100Boundary_100ptRejected()
    {
        await using var db = await TestDb.CreateAsync();
        var seg100 = await SeedSegmentAsync(db.Context, BuildLinearSegment(100, 11.000));
        await SeedActivityRawAsync(db.Context, ZoneHitTrack(11.000, 47.099)); // span clears the min-span gate
        await db.Service.AnalyzeSegmentMatchesAsync(seg100, matchThresholdPercent: 0);
        Assert.Empty(await EffortsAsync(db.Context, seg100)); // n>=100 needs 2 hits/zone → rejected
    }

    // P7 split B — 99-pt segment, same 1 tight hit per zone → accepted.
    [Fact]
    public async Task Parity_ZoneHit100Boundary_99ptAccepted()
    {
        await using var db = await TestDb.CreateAsync();
        var seg99 = await SeedSegmentAsync(db.Context, BuildLinearSegment(99, 15.000));
        await SeedActivityRawAsync(db.Context, ZoneHitTrack(15.000, 47.098)); // span clears the min-span gate
        await db.Service.AnalyzeSegmentMatchesAsync(seg99, matchThresholdPercent: 0);
        Assert.Single(await EffortsAsync(db.Context, seg99));  // n<100 → 1 hit/zone accepted
    }

    // Mirrors pin P8: an effort whose exit stays far from the (displaced) end pin has
    // Offset_exit > 0.005 → the engine's verbatim quality-guard DELETE removes it. Both → 0 rows.
    [Fact]
    public async Task Parity_OffsetGuard_DeletesFarExitEffort()
    {
        await using var db = await TestDb.CreateAsync();
        var segPts = new (double lat, double lng)[10];
        for (int i = 0; i < 9; i++) segPts[i] = (47.000 + i * 0.001, 11.000);
        segPts[9] = (47.009, 11.006);                    // end pin displaced far in lng
        var segId = await SeedSegmentCustomAsync(db.Context, segPts, nullDistance: true);

        var rows = new (int, double, double, double?)[9];
        for (int i = 0; i < 9; i++) rows[i] = (i + 1, 47.000 + i * 0.001, 11.000, (double?)null);
        await SeedActivityRawAsync(db.Context, rows);

        await db.Service.AnalyzeSegmentMatchesAsync(segId);
        Assert.Empty(await EffortsAsync(db.Context, segId));
    }

    // Mirrors pin P11: a pre-seeded friend-track effort on an own segment gets aggregates (any-owner
    // S9.1) but never offsets (own-only S8.1) — exercises the engine aggregates path on a non-own row.
    [Fact]
    public async Task Parity_Aggregates_FriendEffortFilled()
    {
        await using var db = await TestDb.CreateAsync();
        var segPts = new (double lat, double lng)[8];
        for (int i = 0; i < 8; i++) segPts[i] = (47.000 + i * 0.001, 11.000);
        var segId = await SeedSegmentCustomAsync(db.Context, segPts);

        await SeedUserAsync(db.Context, "friend-user");
        var friendTrack = await SeedFriendAggregateTrackAsync(db.Context);
        await SeedGuessedEffortAsync(db.Context, segId, new TrackId(friendTrack), entryGuessed: 0, exitGuessed: 5);   // friend track's dense ordinals 0..5

        await db.Service.AnalyzeSegmentMatchesAsync(segId);

        var effort = Assert.Single(await EffortsAsync(db.Context, segId));
        Assert.Equal(new TrackId(friendTrack), effort.TrackId);
        Assert.Equal(500.0, effort.EffortDistance!.Value);   // MAX−MIN over friend window
        Assert.NotNull(effort.EffortTime);
        Assert.Null(effort.GpsIdEntryCalculated);            // offsets never ran on the friend row
    }

    // -------------------------------------------------------------------------
    // Engine-specific behaviour pins
    // -------------------------------------------------------------------------

    // S1 own-scope: a friend-owned track overlapping the own segment's bbox is never a candidate.
    [Fact]
    public async Task Engine_Candidates_OwnTracksOnly()
    {
        await using var db = await TestDb.CreateAsync();
        var segId = await SeedSegmentAsync(db.Context, BuildSegment(6, 8));

        await SeedUserAsync(db.Context, "friend-user");
        var rows = new GpsRow[8];
        for (int i = 0; i < 8; i++) rows[i] = new GpsRow(300 + i, 47.006 + i * 0.001, 11.000);
        await SeedTrackAsync(db.Context, rows, userId: "friend-user");   // would match if considered

        await db.Service.AnalyzeSegmentMatchesAsync(segId);
        Assert.Empty(await EffortsAsync(db.Context, segId));
    }

    // S8.1 + S9.1: own analysis fills the friend effort's aggregates but leaves its calculated ids
    // NULL, while the own effort gets calculated ids (offsets are own-scoped).
    [Fact]
    public async Task Engine_Offsets_OwnEffortsOnly_FriendCalcStaysNull()
    {
        await using var db = await TestDb.CreateAsync();
        var segId = await SeedSegmentAsync(db.Context, BuildSegment(6, 8));
        var ownTrack = await SeedActivityAsync(db.Context, BuildTrack(0.0));

        await SeedUserAsync(db.Context, "friend-user");
        var friendTrack = await SeedFriendAggregateTrackAsync(db.Context);
        await SeedGuessedEffortAsync(db.Context, segId, new TrackId(friendTrack), entryGuessed: 0, exitGuessed: 5);   // friend track's dense ordinals 0..5

        await db.Service.AnalyzeSegmentMatchesAsync(segId);

        var efforts = await EffortsAsync(db.Context, segId);
        var own = Assert.Single(efforts, e => e.TrackId == new TrackId(ownTrack));
        var friend = Assert.Single(efforts, e => e.TrackId == new TrackId(friendTrack));
        Assert.NotNull(own.GpsIdEntryCalculated);            // own effort offsets ran
        Assert.Null(friend.GpsIdEntryCalculated);            // friend effort offsets skipped
        Assert.NotNull(friend.EffortDistance);               // but aggregates filled (any owner)
    }

    // DeleteFilter is own-scoped: a friend effort survives own re-analysis; the own effort is not
    // duplicated across runs.
    [Fact]
    public async Task Engine_ReAnalyze_PreservesFriendEfforts()
    {
        await using var db = await TestDb.CreateAsync();
        var segId = await SeedSegmentAsync(db.Context, BuildSegment(6, 8));
        var ownTrack = await SeedActivityAsync(db.Context, BuildTrack(0.0));

        await SeedUserAsync(db.Context, "friend-user");
        var friendTrack = await SeedFriendAggregateTrackAsync(db.Context);
        await SeedGuessedEffortAsync(db.Context, segId, new TrackId(friendTrack), entryGuessed: 0, exitGuessed: 5);   // friend track's dense ordinals 0..5

        await db.Service.AnalyzeSegmentMatchesAsync(segId);
        await db.Service.AnalyzeSegmentMatchesAsync(segId);

        var efforts = await EffortsAsync(db.Context, segId);
        Assert.Contains(efforts, e => e.TrackId == new TrackId(friendTrack));   // friend row survived both runs
        Assert.Single(efforts, e => e.TrackId == new TrackId(ownTrack));        // own row inserted once, not doubled
    }

    // S7.3: inserted rows' autoincrement GPSinSegment_ID order matches (track_id, gps_id_entry).
    [Fact]
    public async Task Engine_Insert_OrderedByTrackThenEntry()
    {
        await using var db = await TestDb.CreateAsync();
        var segId = await SeedSegmentAsync(db.Context, BuildSegment(6, 8, 11.000));
        // Two tracks, each with two crossings (ordinal gap > 300 via filler) → four efforts.
        await SeedActivityRawAsync(db.Context, TwoCrossingsWithGap(11.000, fillerCount: 300));
        await SeedActivityRawAsync(db.Context, TwoCrossingsWithGap(11.000, fillerCount: 300));

        await db.Service.AnalyzeSegmentMatchesAsync(segId);

        var byId = await db.Context.GpsInSegments.AsNoTracking()
            .Where(e => e.SegmentId == segId)
            .OrderBy(e => e.GpsInSegmentId)
            .ToListAsync();
        Assert.Equal(4, byId.Count);
        var expected = byId.OrderBy(e => e.TrackId).ThenBy(e => e.GpsIdEntryGuessed).ToList();
        Assert.Equal(expected.Select(e => e.GpsInSegmentId), byId.Select(e => e.GpsInSegmentId));
    }

    // Teardown contract: the engine leaves no pipeline temp tables on the connection after a run.
    [Fact]
    public async Task Engine_NoTempTables_AfterAnalyze()
    {
        await using var db = await TestDb.CreateAsync();
        var segId = await SeedSegmentAsync(db.Context, BuildSegment(6, 8));
        await SeedActivityAsync(db.Context, BuildTrack(0.0));
        await db.Service.AnalyzeSegmentMatchesAsync(segId);

        await db.Context.Database.OpenConnectionAsync();
        try
        {
            var conn = (SqliteConnection)db.Context.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_temp_master WHERE name LIKE 'tmp_%'";
            Assert.Equal(0L, Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }
        finally
        {
            await db.Context.Database.CloseConnectionAsync();
        }
    }

    // Shape branch fills the effort_* aggregates via IEffortMath.
    [Fact]
    public async Task Engine_ShapeSegment_AggregatesFilled()
    {
        await using var db = await TestDb.CreateAsync();
        await SeedLapTrackAsync(db.Context);
        var segId = await SeedLapGateSegmentAsync(db.Context);

        await db.Service.AnalyzeSegmentMatchesAsync(segId);

        var efforts = await EffortsAsync(db.Context, segId);
        Assert.Equal(2, efforts.Count);   // two laps
        Assert.All(efforts, e =>
        {
            Assert.NotNull(e.EffortTime);
            Assert.NotNull(e.EffortDistance);
            Assert.True(e.EffortDistance!.Value > 0);
        });
    }

    // MatchEngineService null-geometry arm: a segment with no Segment_GPS returns cleanly, 0 rows.
    [Fact]
    public async Task Engine_SegmentWithoutGeometry_NoEfforts()
    {
        await using var db = await TestDb.CreateAsync();
        var seg = new Segment { UserId = "test-user", SegmentDistance = 100 };
        db.Context.Segments.Add(seg);
        await db.Context.SaveChangesAsync();
        await SeedActivityAsync(db.Context, BuildTrack(0.0));

        await db.Service.AnalyzeSegmentMatchesAsync(seg.SegmentId);
        Assert.Empty(await EffortsAsync(db.Context, seg.SegmentId));
    }

    // S1 fallback arm: an own track lacking an Activity_Bbox row still matches (bbox derived from decode).
    [Fact]
    public async Task Engine_BboxFallback_TrackWithoutBboxRow_Matches()
    {
        await using var db = await TestDb.CreateAsync();
        var segId = await SeedSegmentAsync(db.Context, BuildSegment(6, 8));
        await SeedActivityRawNoBboxAsync(db.Context, new[]
        {
            (1, 47.006, 11.000, (double?)null), (2, 47.007, 11.000, (double?)null),
            (3, 47.008, 11.000, (double?)null), (4, 47.009, 11.000, (double?)null),
            (5, 47.010, 11.000, (double?)null), (6, 47.011, 11.000, (double?)null),
            (7, 47.012, 11.000, (double?)null), (8, 47.013, 11.000, (double?)null),
        });
        await db.Service.AnalyzeSegmentMatchesAsync(segId);
        Assert.Single(await EffortsAsync(db.Context, segId));
    }

    // S8.11/12: EffortMath sees a null decode → calculated ids fall back to guessed, offsets stay NULL.
    [Fact]
    public async Task EffortMath_DecodeNull_FallbackGuessedIdsNullOffsets()
    {
        await using var db = await TestDb.CreateAsync(effortReaderOverride: new NullTrackReader());
        var segId = await SeedSegmentAsync(db.Context, BuildSegment(6, 8));
        await SeedActivityRawAsync(db.Context, DecodeNullTrack());

        await db.Service.AnalyzeSegmentMatchesAsync(segId);

        var effort = Assert.Single(await EffortsAsync(db.Context, segId));
        Assert.Equal(effort.GpsIdEntryGuessed, effort.GpsIdEntryCalculated);
        Assert.Equal(effort.GpsIdExitGuessed, effort.GpsIdExitCalculated);
        Assert.Null(effort.OffsetEntry);
        Assert.Null(effort.OffsetExit);
    }

    // S9.10: EffortMath sees a null decode → the effort_* aggregates stay NULL.
    [Fact]
    public async Task EffortMath_DecodeNull_AggregatesSkipped()
    {
        await using var db = await TestDb.CreateAsync(effortReaderOverride: new NullTrackReader());
        var segId = await SeedSegmentAsync(db.Context, BuildSegment(6, 8));
        await SeedActivityRawAsync(db.Context, DecodeNullTrack());

        await db.Service.AnalyzeSegmentMatchesAsync(segId);

        var effort = Assert.Single(await EffortsAsync(db.Context, segId));
        Assert.Null(effort.EffortDistance);
        Assert.Null(effort.EffortTime);
    }

    // -------------------------------------------------------------------------
    // Seed helpers (mirror SegmentAnalysisPipelineTests / SegmentAnalysisGateTests)
    // -------------------------------------------------------------------------

    // 7/8-match track: hits every segment lat except the middle 47.009 → perc 87 (P1/P6 geometry).
    private static (int, double, double, double?)[] ThresholdTrack() => new[]
    {
        (1, 47.006, 11.000, (double?)null), (2, 47.007, 11.000, (double?)null),
        (3, 47.008, 11.000, (double?)null), (4, 47.010, 11.000, (double?)null),
        (5, 47.011, 11.000, (double?)null), (6, 47.012, 11.000, (double?)null),
        (7, 47.013, 11.000, (double?)null),
    };

    // Full-coverage own track (8 pts) with a NULL distance channel — the match inserts one effort.
    private static (int, double, double, double?)[] DecodeNullTrack() => new[]
    {
        (1, 47.006, 11.000, (double?)null), (2, 47.007, 11.000, (double?)null),
        (3, 47.008, 11.000, (double?)null), (4, 47.009, 11.000, (double?)null),
        (5, 47.010, 11.000, (double?)null), (6, 47.011, 11.000, (double?)null),
        (7, 47.012, 11.000, (double?)null), (8, 47.013, 11.000, (double?)null),
    };

    private static (double lat, double lng)[] BuildTrack(double lngOffset)
    {
        var pts = new (double lat, double lng)[20];
        for (int i = 0; i < 20; i++)
            pts[i] = (47.000 + i * 0.001, 11.000 + lngOffset);
        return pts;
    }

    private static (double lat, double lng)[] BuildSegment(int startIdx, int count, double lng = 11.000)
    {
        var pts = new (double lat, double lng)[count];
        for (int i = 0; i < count; i++)
            pts[i] = (47.000 + (startIdx + i) * 0.001, lng);
        return pts;
    }

    private static (double lat, double lng)[] BuildLinearSegment(int count, double lng)
    {
        var pts = new (double lat, double lng)[count];
        for (int i = 0; i < count; i++)
            pts[i] = (47.000 + i * 0.001, lng);
        return pts;
    }

    private static IEnumerable<(int, double, double, double?)> BuildTraversalRaw(int startId, double lng)
    {
        for (int i = 0; i < 8; i++)
            yield return (startId + i, 47.006 + i * 0.001, lng, (double?)null);
    }

    // Two 8-point crossings of the segment separated by `fillerCount` unmatched points parked far away (in
    // latitude, so they never hit a zone but still occupy ordinals). The matched points therefore have an
    // ordinal gap of fillerCount + 1 between the crossings — the dense-ordinal analogue of the old GPS_ID
    // jump. gpsIds are ignored on seed (the blob re-ramps to 0..n-1); order is what matters.
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
    // span (16) comfortably clears the min-span gate (>= max(5, n*0.1)); acceptance is then governed by the
    // zone-hit 100-boundary alone. Replaces the pre-BlobStore 2-point tracks whose GPS_ID gap faked the span.
    private static (int, double, double, double?)[] ZoneHitTrack(double lng, double endLat)
    {
        var list = new List<(int, double, double, double?)>();
        int id = 1;
        list.Add((id++, 47.000, lng, (double?)null));                                     // start-zone hit
        for (int i = 0; i < 15; i++) list.Add((id++, 48.500 + i * 0.00001, lng, (double?)null)); // filler
        list.Add((id++, endLat, lng, (double?)null));                                     // end-zone hit
        return list.ToArray();
    }

    private static async Task<int> SeedActivityAsync(RouteBaseDbContext ctx, (double lat, double lng)[] points)
    {
        var activity = new Activity
        {
            StartGpsLat = points[0].lat, StartGpsLong = points[0].lng,
            EndGpsLat = points[^1].lat, EndGpsLong = points[^1].lng, UserId = "test-user",
        };
        ctx.Activities.Add(activity);
        await ctx.SaveChangesAsync();
        TrackId trackId = activity.TrackId;

        var gps = points.Select(p => new GpsData { GpsLat = p.lat, GpsLong = p.lng }).ToList();
        await TrackSeed.SeedTrackAsync(ctx, trackId, gps);

        await ctx.Database.ExecuteSqlRawAsync(
            @"INSERT OR IGNORE INTO Activity_Bbox (track_ID, min_lat, max_lat, min_long, max_long)
              VALUES ({0}, {1}, {2}, {3}, {4})",
            trackId.Value, points.Min(p => p.lat), points.Max(p => p.lat),
            points.Min(p => p.lng), points.Max(p => p.lng));
        return trackId.Value;
    }

    private static async Task<int> SeedActivityWithDistancesAsync(RouteBaseDbContext ctx, (double lat, double lng)[] points)
    {
        var activity = new Activity
        {
            StartGpsLat = points[0].lat, StartGpsLong = points[0].lng,
            EndGpsLat = points[^1].lat, EndGpsLong = points[^1].lng, UserId = "test-user",
        };
        ctx.Activities.Add(activity);
        await ctx.SaveChangesAsync();
        TrackId trackId = activity.TrackId;

        var gps = new List<GpsData>();
        double cumDist = 0;
        var prev = points[0];
        foreach (var (lat, lng) in points)
        {
            cumDist += HaversineMeters(prev.lat, prev.lng, lat, lng);
            gps.Add(new GpsData { GpsLat = lat, GpsLong = lng, Distance = cumDist });
            prev = (lat, lng);
        }
        await TrackSeed.SeedTrackAsync(ctx, trackId, gps);

        await ctx.Database.ExecuteSqlRawAsync(
            @"INSERT OR IGNORE INTO Activity_Bbox (track_ID, min_lat, max_lat, min_long, max_long)
              VALUES ({0}, {1}, {2}, {3}, {4})",
            trackId.Value, points.Min(p => p.lat), points.Max(p => p.lat),
            points.Min(p => p.lng), points.Max(p => p.lng));
        return trackId.Value;
    }

    private static async Task<int> SeedActivityRawAsync(RouteBaseDbContext ctx, (int gpsId, double lat, double lng, double? dist)[] pts)
    {
        TrackId trackId = new TrackId(await InsertRawTrackAsync(ctx, pts));
        await ctx.Database.ExecuteSqlRawAsync(
            @"INSERT OR IGNORE INTO Activity_Bbox (track_ID, min_lat, max_lat, min_long, max_long)
              VALUES ({0}, {1}, {2}, {3}, {4})",
            trackId.Value, pts.Min(p => p.lat), pts.Max(p => p.lat),
            pts.Min(p => p.lng), pts.Max(p => p.lng));
        return trackId.Value;
    }

    // Same as SeedActivityRawAsync but writes NO Activity_Bbox row (exercises the S1 decode fallback).
    private static Task<int> SeedActivityRawNoBboxAsync(RouteBaseDbContext ctx, (int gpsId, double lat, double lng, double? dist)[] pts)
        => InsertRawTrackAsync(ctx, pts);

    private static async Task<int> InsertRawTrackAsync(RouteBaseDbContext ctx, (int gpsId, double lat, double lng, double? dist)[] pts)
    {
        var activity = new Activity
        {
            StartGpsLat = pts[0].lat, StartGpsLong = pts[0].lng,
            EndGpsLat = pts[^1].lat, EndGpsLong = pts[^1].lng, UserId = "test-user",
        };
        ctx.Activities.Add(activity);
        await ctx.SaveChangesAsync();
        TrackId trackId = activity.TrackId;

        // Points seeded in array order (ascending original GPS_ID) → dense ordinals 0..n-1 in the blob.
        var gps = pts.Select(p => new GpsData { GpsLat = p.lat, GpsLong = p.lng, Distance = p.dist }).ToList();
        await TrackSeed.SeedTrackAsync(ctx, trackId, gps);
        return trackId.Value;
    }

    private static async Task<SegmentId> SeedSegmentAsync(RouteBaseDbContext ctx, (double lat, double lng)[] points)
    {
        double dist = 0;
        for (int i = 1; i < points.Length; i++)
            dist += HaversineMeters(points[i - 1].lat, points[i - 1].lng, points[i].lat, points[i].lng);

        var segment = new Segment { UserId = "test-user", SegmentDistance = dist };
        ctx.Segments.Add(segment);
        await ctx.SaveChangesAsync();
        var segId = segment.SegmentId;

        foreach (var (lat, lng) in points)
            ctx.SegmentGps.Add(new SegmentGps { SegmentId = segId, GpsLat = lat, GpsLong = lng });
        await ctx.SaveChangesAsync();
        return segId;
    }

    // Polyline segment from explicit points; nullDistance leaves SegmentDistance NULL (S8.8 branch).
    private static async Task<SegmentId> SeedSegmentCustomAsync(
        RouteBaseDbContext ctx, (double lat, double lng)[] points, bool nullDistance = false)
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
        ctx.Segments.Add(segment);
        await ctx.SaveChangesAsync();
        var segId = segment.SegmentId;

        foreach (var (lat, lng) in points)
            ctx.SegmentGps.Add(new SegmentGps { SegmentId = segId, GpsLat = lat, GpsLong = lng });
        await ctx.SaveChangesAsync();
        return segId;
    }

    // Full-channel GPS row for the effort-math scenarios. Nulls stay SQL NULL.
    private sealed record GpsRow(
        int GpsId, double Lat, double Lng, double? Dist = null, string? DateTimeUtc = null,
        double? Speed = null, double? Altitude = null, double? HeartRate = null,
        double? Cadence = null, double? Power = null);

    // Seeds an activity + all-channel GPS rows (explicit GPS_IDs) for the given owner. Datetime is raw
    // 19-char TEXT so julianday() parses it as whole seconds (bypasses EF fractional formatting).
    private static async Task<int> SeedTrackAsync(RouteBaseDbContext ctx, GpsRow[] pts, string userId = "test-user")
    {
        var activity = new Activity
        {
            StartGpsLat = pts[0].Lat, StartGpsLong = pts[0].Lng,
            EndGpsLat = pts[^1].Lat, EndGpsLong = pts[^1].Lng, UserId = userId,
        };
        ctx.Activities.Add(activity);
        await ctx.SaveChangesAsync();
        TrackId trackId = activity.TrackId;

        // Points in array order (ascending original GPS_ID) → dense ordinals in the blob. Datetimes are
        // whole seconds so the codec's 1-s time quantization round-trips exactly (julianday parity holds).
        var gps = pts.Select(p => new GpsData
        {
            GpsLat = p.Lat, GpsLong = p.Lng, Distance = p.Dist,
            DateTimeUtc = p.DateTimeUtc == null
                ? null
                : DateTime.SpecifyKind(DateTime.Parse(p.DateTimeUtc, CultureInfo.InvariantCulture), DateTimeKind.Utc),
            Speed = p.Speed, Altitude = p.Altitude, HeartRate = p.HeartRate, Cadence = p.Cadence, Power = p.Power,
        }).ToList();
        await TrackSeed.SeedTrackAsync(ctx, trackId, gps);

        await ctx.Database.ExecuteSqlRawAsync(
            @"INSERT OR IGNORE INTO Activity_Bbox (track_ID, min_lat, max_lat, min_long, max_long)
              VALUES ({0}, {1}, {2}, {3}, {4})",
            trackId.Value, pts.Min(p => p.Lat), pts.Max(p => p.Lat),
            pts.Min(p => p.Lng), pts.Max(p => p.Lng));
        return trackId.Value;
    }

    // Friend-owned track (6 pts, span-500 distance + minute-spaced datetimes) for the friend-effort
    // aggregate pins — explicit GPS_IDs 100..105.
    private static Task<int> SeedFriendAggregateTrackAsync(RouteBaseDbContext ctx)
    {
        var baseUtc = new DateTime(2021, 6, 15, 10, 0, 0, DateTimeKind.Utc);
        var rows = new GpsRow[6];
        for (int i = 0; i < 6; i++)
            rows[i] = new GpsRow(100 + i, 47.001 + i * 0.001, 11.000,
                Dist: i * 100.0,
                DateTimeUtc: baseUtc.AddSeconds(i * 60).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        return SeedTrackAsync(ctx, rows, userId: "friend-user");
    }

    private static async Task SeedUserAsync(RouteBaseDbContext ctx, string userId)
    {
        ctx.Users.Add(new AppUser
        {
            UserId = userId, DisplayName = userId, Email = $"{userId}@test.com",
            IsAdmin = false, CreatedAt = DateTime.UtcNow.ToString("O"), LastLogin = DateTime.UtcNow.ToString("O"),
        });
        await ctx.SaveChangesAsync();
    }

    // Pre-seeds a GPSinSegment effort with a guessed window only (calculated/offsets/aggregates NULL).
    private static async Task SeedGuessedEffortAsync(
        RouteBaseDbContext ctx, SegmentId segId, TrackId trackId, int entryGuessed, int exitGuessed)
    {
        ctx.GpsInSegments.Add(new GpsInSegment
        {
            SegmentId = segId, TrackId = trackId,
            GpsIdEntryGuessed = entryGuessed, GpsIdExitGuessed = exitGuessed,
        });
        await ctx.SaveChangesAsync();
    }

    // -------------------------------------------------------------------------
    // Shape-segment fixture (lap gate: one gate as start + finish, one zone) — mirrors
    // ShapeSegmentMatcherPinTests; a 3-crossing track yields exactly two laps.
    // -------------------------------------------------------------------------

    private static readonly double[] LapTrackLats =
        { 47.000, 47.003, 47.007, 47.010, 47.007, 47.003, 47.000, 47.003, 47.007, 47.010 };

    private static async Task<int> SeedLapTrackAsync(RouteBaseDbContext ctx)
    {
        var baseTime = new DateTime(2025, 6, 1, 8, 0, 0, DateTimeKind.Utc);
        var activity = new Activity
        {
            StartGpsLat = LapTrackLats[0], StartGpsLong = 11.000,
            EndGpsLat = LapTrackLats[^1], EndGpsLong = 11.000, UserId = "test-user", Visibility = "private",
        };
        ctx.Activities.Add(activity);
        await ctx.SaveChangesAsync();
        TrackId trackId = activity.TrackId;

        var gps = new List<GpsData>();
        double cumDist = 0;
        for (int i = 0; i < LapTrackLats.Length; i++)
        {
            if (i > 0) cumDist += HaversineMeters(LapTrackLats[i - 1], 11.000, LapTrackLats[i], 11.000);
            gps.Add(new GpsData
            {
                GpsLat = LapTrackLats[i], GpsLong = 11.000,
                Distance = cumDist, DateTimeUtc = baseTime.AddSeconds(i * 30),
            });
        }
        await TrackSeed.SeedTrackAsync(ctx, trackId, gps);

        await ctx.Database.ExecuteSqlRawAsync(
            @"INSERT OR IGNORE INTO Activity_Bbox (track_ID, min_lat, max_lat, min_long, max_long)
              VALUES ({0}, 47.000, 47.010, 11.000, 11.000)", trackId.Value);
        return trackId.Value;
    }

    private static async Task<SegmentId> SeedLapGateSegmentAsync(RouteBaseDbContext ctx)
    {
        var rules = new RuleDocument
        {
            Shapes = new()
            {
                new ShapeDef
                {
                    No = 1, Kind = "zone",
                    Geom = new RectGeom { South = 46.999, West = 10.999, North = 47.011, East = 11.001 },
                },
                new ShapeDef
                {
                    No = 2, Kind = "gate",
                    Geom = new LineGeom
                    {
                        Lat1 = 47.005, Lng1 = 10.999, Lat2 = 47.005, Lng2 = 11.001,
                        Direction = CrossDirection.Any,
                    },
                },
            },
            Extent = new GatesExtent { StartGates = { 2 }, FinishGates = { 2 } },
            Membership = { new PassesAllRule { Zones = { 1 } } },
        };

        var segment = new Segment
        {
            UserId = "test-user", SegmentKind = "shapes", MatchRulesJson = rules.Serialize(),
        };
        ctx.Segments.Add(segment);
        await ctx.SaveChangesAsync();
        return segment.SegmentId;
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
