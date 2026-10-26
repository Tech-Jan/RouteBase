using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Geometry;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Segments;
using RouteBaseWeb.Services.Tracks;
using RouteBaseWeb.Tests.Helpers;
using RouteBaseWeb.Services.Segments.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Pipeline;

/// <summary>
/// TE-06 characterization pins P1/P2: freeze the exact GPSinSegment effort rows the shape matcher
/// writes today (pre-TrackReader swap). These literals are the parity contract for TE-04 — the
/// migration to <c>ITrackReader</c> must reproduce them byte-for-byte. Do NOT edit the asserted
/// values when the swap lands; a diff here is a real behaviour change.
///
/// Fixture: a lap-gate segment (one gate, listed as both start and finish) plus a mandatory zone
/// covering the corridor. A single track crosses the gate three times, producing exactly two laps.
/// Timestamps are 30 s apart so the 15 s gate debounce never collapses a crossing — this exercises
/// the epoch-seconds time path that TE-04 must preserve (SQL <c>ToSeconds</c> → <c>TimeEpochS</c>).
/// </summary>
public class ShapeSegmentMatcherPinTests : IAsyncLifetime
{
    private const string Me = "me";
    private static readonly DateTime BaseTime = new(2025, 6, 1, 8, 0, 0, DateTimeKind.Utc);

    private RouteBaseDbContext _context = null!;
    private ShapeSegmentMatcher _matcher = null!;

    // Track latitudes (lng fixed at 11.000) crossing the gate at lat 47.005 three times:
    // north (seg1) → south (seg4) → north (seg7) → two laps.
    private static readonly double[] TrackLats =
        { 47.000, 47.003, 47.007, 47.010, 47.007, 47.003, 47.000, 47.003, 47.007, 47.010 };

    public async Task InitializeAsync()
    {
        var dbName = $"testdb_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>()
            .UseSqlite($"Data Source=file:{dbName}?mode=memory&cache=shared")
            .Options;

        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();

        var schema = new DbSchemaManager(_context);
        await schema.EnsureActivityBboxTableAsync();
        await schema.EnsureTrackContentTablesAsync();
        await schema.EnsureIndexesAsync();

        _context.Users.Add(new AppUser
        {
            UserId = Me, DisplayName = Me, Email = $"{Me}@test.com", IsAdmin = false,
            CreatedAt = DateTime.UtcNow.ToString("O"), LastLogin = DateTime.UtcNow.ToString("O"),
        });
        await _context.SaveChangesAsync();

        _matcher = new ShapeSegmentMatcher(_context, NullLogger<ShapeSegmentMatcher>.Instance, new DirectTrackReader(_context));
    }

    public async Task DisposeAsync()
    {
        await _context.Database.EnsureDeletedAsync();
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task MatchAsync_TwoLapTrack_WritesExactEffortRows()
    {
        await SeedLapTrackAsync(nullCoordAtIndex: null);
        var segId = await SeedLapGateSegmentAsync();

        await _matcher.MatchAsync([segId], Me, friendIds: Array.Empty<string>());

        var rows = await OrderedEffortsAsync(segId);

        Assert.Equal(2, rows.Count);

        // Lap 1: samples [2..4] → ordinals 2..4 (was GPS_IDs 3..5; ordinal = id − 1).
        AssertRow(rows[0], entry: 2, exit: 4, segGps: 3, gpsIdCount: 2);
        // Lap 2: samples [5..7] → ordinals 5..7.
        AssertRow(rows[1], entry: 5, exit: 7, segGps: 3, gpsIdCount: 2);
    }

    [Fact]
    public async Task MatchAsync_NullCoordinatePoint_SkippedNotBreaking()
    {
        // Identical fixture with one NULL-lat point inserted mid-track (mid-span, GPS_ID order).
        // Today the SQL filters it before sampling; the evidence connects across it. The frozen
        // rows below pin that a filtered NULL point does not split/merge/shift the two efforts —
        // TE-04's NaN filter must reproduce this exactly.
        await SeedLapTrackAsync(nullCoordAtIndex: 4);
        var segId = await SeedLapGateSegmentAsync();

        await _matcher.MatchAsync([segId], Me, friendIds: Array.Empty<string>());

        var rows = await OrderedEffortsAsync(segId);

        Assert.Equal(2, rows.Count);

        // The NULL point took ordinal 4, shifting the later valid ordinals up by one, but the effort
        // structure (two laps, 3 inside points each, span arithmetic across the gap) is unchanged
        // (ordinal = old id − 1).
        AssertRow(rows[0], entry: 2, exit: 5, segGps: 3, gpsIdCount: 3);
        AssertRow(rows[1], entry: 6, exit: 8, segGps: 3, gpsIdCount: 2);
    }

    // preview-path and candidate-scope coverage.

    // A zone + lap gate covering the corridor lat 47.000..47.010 @ lng 11.000 — every bbox in that corridor
    // overlaps it, so Stage-1 CouldMatch keeps the candidate.
    private static RuleDocument CoveringDoc() => new()
    {
        Shapes = new()
        {
            new ShapeDef { No = 1, Kind = "zone",
                Geom = new RectGeom { South = 46.999, West = 10.999, North = 47.011, East = 11.001 } },
            new ShapeDef { No = 2, Kind = "gate",
                Geom = new LineGeom { Lat1 = 47.005, Lng1 = 10.999, Lat2 = 47.005, Lng2 = 11.001,
                    Direction = CrossDirection.Any } },
        },
        Extent = new GatesExtent { StartGates = { 2 }, FinishGates = { 2 } },
        Membership = { new PassesAllRule { Zones = { 1 } } },
    };

    private async Task SeedCandidateAsync(string ownerId, string visibility)
    {
        var activity = new Activity
        {
            UserId = ownerId, Visibility = visibility,
            StartGpsLat = 47.000, StartGpsLong = 11.000, EndGpsLat = 47.010, EndGpsLong = 11.000,
        };
        _context.Activities.Add(activity);
        await _context.SaveChangesAsync();
        await _context.Database.ExecuteSqlRawAsync(
            @"INSERT OR IGNORE INTO Activity_Bbox (track_ID, min_lat, max_lat, min_long, max_long)
              VALUES ({0}, 47.000, 47.010, 11.000, 11.000)", activity.TrackId.Value);
    }

    // Mut 128 (PreviewCandidateTrackIdsAsync body block-removed → empty list): a bbox-overlapping own track
    // is surfaced as a stage-1 candidate.
    [Fact]
    public async Task PreviewCandidateTrackIds_OwnTrack_IsACandidate()
    {
        await SeedCandidateAsync(Me, "private");

        var ids = await _matcher.PreviewCandidateTrackIdsAsync(CoveringDoc(), Me, Array.Empty<string>());

        var trackId = await _context.Activities.Select(a => a.TrackId.Value).SingleAsync();
        Assert.Contains(trackId, ids);
    }

    // Mut 203 (friend-visibility clause blanked to ""): a friend's 'friends'-visible released track, in scope
    // via a doc owner filter, is admitted ONLY through the friend clause. Blanking it silently drops the
    // friend's efforts. Friend user is seeded here; the doc owner filter puts both owners in scope so the
    // gate under test is the visibility clause, not the owner predicate.
    [Fact]
    public async Task PreviewCandidateTrackIds_FriendsVisibleTrack_AdmittedViaFriendClause()
    {
        const string Friend = "friend";
        _context.Users.Add(new AppUser
        {
            UserId = Friend, DisplayName = Friend, Email = "friend@test.com", IsAdmin = false,
            CreatedAt = DateTime.UtcNow.ToString("O"), LastLogin = DateTime.UtcNow.ToString("O"),
        });
        await _context.SaveChangesAsync();
        await SeedCandidateAsync(Friend, "friends");

        var doc = CoveringDoc();
        doc.TrackFilters.Add(new OwnerFilter { UserIds = { Me, Friend } });   // both owners in candidate scope

        var ids = await _matcher.PreviewCandidateTrackIdsAsync(doc, Me, new[] { Friend });

        var friendTrackId = await _context.Activities
            .Where(a => a.UserId == Friend).Select(a => a.TrackId.Value).SingleAsync();
        Assert.Contains(friendTrackId, ids);
    }

    // Mut 154 (`stage1.Count > Max` → `>=`): a candidate set sized EXACTLY at the cap is NOT truncated. One
    // bulk INSERT..SELECT builds the bboxes so the boundary case stays fast.
    [Fact]
    public async Task PreviewExact_CandidatesExactlyAtCap_NotTruncated()
    {
        int cap = ShapeSegmentMatcher.MaxExactPreviewCandidates;
        for (int i = 0; i < cap; i++)
            _context.Activities.Add(new Activity
            {
                UserId = Me, Visibility = "private",
                StartGpsLat = 47.000, StartGpsLong = 11.000, EndGpsLat = 47.010, EndGpsLong = 11.000,
            });
        await _context.SaveChangesAsync();
        await _context.Database.ExecuteSqlRawAsync(
            @"INSERT OR IGNORE INTO Activity_Bbox (track_ID, min_lat, max_lat, min_long, max_long)
              SELECT track_ID, 47.000, 47.010, 11.000, 11.000 FROM Activity");

        var result = await _matcher.PreviewExactTrackIdsAsync(CoveringDoc(), Me, Array.Empty<string>());

        Assert.False(result.Truncated);
    }

    // ---------------------------------------------------------------- helpers

    private static void AssertRow(GpsInSegment row, int entry, int exit, int segGps, int gpsIdCount)
    {
        Assert.Equal(entry, row.GpsIdEntryGuessed);
        Assert.Equal(exit, row.GpsIdExitGuessed);
        Assert.Equal(segGps, row.SegGpsCount);
        Assert.Equal(2, row.SegTotalCount);      // zone + gate = 2 shapes
        Assert.Equal(100, row.SegPerc);
        Assert.Equal(gpsIdCount, row.GpsIdCount);
    }

    private async Task<List<GpsInSegment>> OrderedEffortsAsync(SegmentId segId) =>
        await _context.GpsInSegments.AsNoTracking()
            .Where(e => e.SegmentId == segId)
            .OrderBy(e => e.GpsInSegmentId)
            .ToListAsync();

    private async Task<int> SeedLapTrackAsync(int? nullCoordAtIndex)
    {
        var activity = new Activity
        {
            StartGpsLat = TrackLats[0], StartGpsLong = 11.000,
            EndGpsLat = TrackLats[^1], EndGpsLong = 11.000,
            UserId = Me, Visibility = "private",
        };
        _context.Activities.Add(activity);
        await _context.SaveChangesAsync();
        TrackId trackId = activity.TrackId;

        var pts = new List<GpsData>();
        for (int i = 0; i < TrackLats.Length; i++)
        {
            // Insert the NULL-coord point just before index i so it lands mid-track in ordinal order.
            if (nullCoordAtIndex == i)
                pts.Add(new GpsData
                {
                    GpsLat = null, GpsLong = null,
                    DateTimeUtc = BaseTime.AddSeconds(i * 30 - 15),
                });

            pts.Add(new GpsData
            {
                GpsLat = TrackLats[i], GpsLong = 11.000,
                DateTimeUtc = BaseTime.AddSeconds(i * 30),
            });
        }
        await TrackSeed.SeedTrackAsync(_context, trackId, pts);

        await _context.Database.ExecuteSqlRawAsync(
            @"INSERT OR IGNORE INTO Activity_Bbox (track_ID, min_lat, max_lat, min_long, max_long)
              VALUES ({0}, 47.000, 47.010, 11.000, 11.000)", trackId.Value);

        return trackId.Value;
    }

    private async Task<SegmentId> SeedLapGateSegmentAsync()
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
            UserId = Me, SegmentKind = "shapes", MatchRulesJson = rules.Serialize(),
        };
        _context.Segments.Add(segment);
        await _context.SaveChangesAsync();

        return segment.SegmentId;
    }
}
