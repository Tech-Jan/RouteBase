using RouteBaseWeb.Models;
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
/// Non-spatial track-filter tests for <see cref="ShapeSegmentMatcher"/> (Phase 4): date range,
/// activity type, and doc-authored owner scope. Clones the owner-scope harness (real in-memory
/// shared-cache SQLite) so the candidate SQL — the date/type/owner predicates + the unchanged
/// visibility clause — runs exactly as in production. All tracks share one corridor inside a single
/// mandatory rectangle, so a match is governed purely by the track filters, not geometry.
/// </summary>
public class ShapeSegmentTrackFilterTests : IAsyncLifetime
{
    private const string Me       = "me";
    private const string Friend   = "friend";
    private const string Stranger = "stranger";

    private RouteBaseDbContext _context = null!;
    private ShapeSegmentMatcher _matcher = null!;

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

        foreach (var id in new[] { Me, Friend, Stranger })
        {
            _context.Users.Add(new AppUser
            {
                UserId = id, DisplayName = id, Email = $"{id}@test.com", IsAdmin = false,
                CreatedAt = DateTime.UtcNow.ToString("O"), LastLogin = DateTime.UtcNow.ToString("O"),
            });
        }
        await _context.SaveChangesAsync();

        _matcher = new ShapeSegmentMatcher(_context, NullLogger<ShapeSegmentMatcher>.Instance, new DirectTrackReader(_context));
    }

    public async Task DisposeAsync()
    {
        await _context.Database.EnsureDeletedAsync();
        await _context.DisposeAsync();
    }

    // ---------------------------------------------------------------- date range

    [Fact]
    public async Task DateRange_FromOnly_ExcludesEarlierTracks()
    {
        var before = await SeedTrackAsync(Me, "friends", new DateTime(2025, 5, 31, 12, 0, 0, DateTimeKind.Unspecified));
        var onOrAfter = await SeedTrackAsync(Me, "friends", new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Unspecified));
        var segId = await SeedShapeSegmentAsync(new DateRangeFilter { From = "2025-06-01" });

        await MatchAsync(segId);

        Assert.Equal(new[] { onOrAfter }, await MatchedTracksAsync(segId));
        Assert.DoesNotContain(before, await MatchedTracksAsync(segId));
    }

    [Fact]
    public async Task DateRange_ToOnly_Boundaries()
    {
        // to = 2025-06-15 → end-day inclusive (matches the activities grid's AddDays(1)).
        var endOfDay = await SeedTrackAsync(Me, "friends", new DateTime(2025, 6, 15, 23, 59, 59, DateTimeKind.Unspecified));
        var twoDaysAfter = await SeedTrackAsync(Me, "friends", new DateTime(2025, 6, 17, 0, 0, 0, DateTimeKind.Unspecified));
        var segId = await SeedShapeSegmentAsync(new DateRangeFilter { To = "2025-06-15" });

        await MatchAsync(segId);

        var matched = await MatchedTracksAsync(segId);
        Assert.Contains(endOfDay, matched);
        Assert.DoesNotContain(twoDaysAfter, matched);
    }

    [Fact]
    public async Task DateRange_ExactFromMidnight_Matches()
    {
        var atFrom = await SeedTrackAsync(Me, "friends", new DateTime(2025, 6, 15, 0, 0, 0, DateTimeKind.Unspecified));
        var segId = await SeedShapeSegmentAsync(new DateRangeFilter { From = "2025-06-15", To = "2025-06-15" });

        await MatchAsync(segId);

        Assert.Equal(new[] { atFrom }, await MatchedTracksAsync(segId));
    }

    [Fact]
    public async Task DateRange_IsoTSeparatorStorageFormat_StillMatches()
    {
        // Some importer paths may have stored ISO-8601 with a 'T' separator; datetime()
        // normalisation in DateRangeSqlFilter must treat both formats identically.
        var isoT = await SeedTrackAsync(Me, "friends", new DateTime(2025, 6, 10, 8, 30, 0, DateTimeKind.Unspecified));
        var isoOut = await SeedTrackAsync(Me, "friends", new DateTime(2025, 7, 10, 8, 30, 0, DateTimeKind.Unspecified));
        await _context.Database.ExecuteSqlRawAsync(
            "UPDATE Activity SET start_datetime_loc = '2025-06-10T08:30:00' WHERE track_ID = {0}", isoT);
        await _context.Database.ExecuteSqlRawAsync(
            "UPDATE Activity SET start_datetime_loc = '2025-07-10T08:30:00' WHERE track_ID = {0}", isoOut);
        var segId = await SeedShapeSegmentAsync(new DateRangeFilter { From = "2025-06-01", To = "2025-06-30" });

        await MatchAsync(segId);

        var matched = await MatchedTracksAsync(segId);
        Assert.Contains(isoT, matched);
        Assert.DoesNotContain(isoOut, matched);
    }

    [Fact]
    public async Task DateRange_NullStart_ExcludedWhenFilterPresent_IncludedOtherwise()
    {
        var nullDate = await SeedTrackAsync(Me, "friends", startLocal: null);

        var withFilter = await SeedShapeSegmentAsync(new DateRangeFilter { From = "2020-01-01" });
        await MatchAsync(withFilter);
        Assert.DoesNotContain(nullDate, await MatchedTracksAsync(withFilter));

        var noFilter = await SeedShapeSegmentAsync();
        await MatchAsync(noFilter);
        Assert.Contains(nullDate, await MatchedTracksAsync(noFilter));
    }

    // ---------------------------------------------------------------- activity type

    [Fact]
    public async Task ActivityType_InList_MatchesListed_ExcludesOthers()
    {
        var run   = await SeedTrackAsync(Me, "friends", activityType: "Run");
        var trail = await SeedTrackAsync(Me, "friends", activityType: "TrailRun");
        var ride  = await SeedTrackAsync(Me, "friends", activityType: "Ride");
        var segId = await SeedShapeSegmentAsync(new ActivityTypeFilter { Types = { "Run", "TrailRun" } });

        await MatchAsync(segId);

        var matched = await MatchedTracksAsync(segId);
        Assert.Contains(run, matched);
        Assert.Contains(trail, matched);
        Assert.DoesNotContain(ride, matched);
    }

    [Fact]
    public async Task ActivityType_NullType_ExcludedWhenFilterPresent()
    {
        var nullType = await SeedTrackAsync(Me, "friends", activityType: null);
        var segId = await SeedShapeSegmentAsync(new ActivityTypeFilter { Types = { "Run" } });

        await MatchAsync(segId);

        Assert.DoesNotContain(nullType, await MatchedTracksAsync(segId));
    }

    // ---------------------------------------------------------------- owner from doc

    [Fact]
    public async Task OwnerFromDoc_WinsOverConflictingColumn()
    {
        await SeedTrackAsync(Me, "friends");
        await SeedTrackAsync(Friend, "friends");
        // Column says both; document owner filter says only Me → the doc wins.
        var segId = await SeedShapeSegmentAsync(
            matchOwnerIds: $"{Me},{Friend}", new OwnerFilter { UserIds = { Me } });

        await _matcher.MatchAsync([segId], Me, friendIds: new[] { Friend });

        Assert.Equal(new[] { Me }, await MatchedOwnersAsync(segId));
    }

    [Fact]
    public async Task OwnerColumnFallback_WhenDocHasNoOwnerFilter()
    {
        await SeedTrackAsync(Me, "friends");
        await SeedTrackAsync(Friend, "friends");
        var segId = await SeedShapeSegmentAsync(matchOwnerIds: $"{Me},{Friend}");

        await _matcher.MatchAsync([segId], Me, friendIds: new[] { Friend });

        Assert.Equal(new[] { Friend, Me }, await MatchedOwnersAsync(segId));
    }

    [Fact]
    public async Task OwnerFromDoc_EmptyUserIds_MatchesNobody()
    {
        await SeedTrackAsync(Me, "friends");
        var segId = await SeedShapeSegmentAsync(new OwnerFilter());

        await _matcher.MatchAsync([segId], Me, friendIds: Array.Empty<string>());

        Assert.Empty(await MatchedOwnersAsync(segId));
    }

    [Fact]
    public async Task OwnerFromDoc_VisibilityStillEnforced()
    {
        await SeedTrackAsync(Me, "friends");
        await SeedTrackAsync(Friend, "friends");    // friends-visibility, friendship revoked → dropped
        await SeedTrackAsync(Stranger, "public");   // public stranger → matched even without friendship
        var segId = await SeedShapeSegmentAsync(new OwnerFilter { UserIds = { Me, Friend, Stranger } });

        await _matcher.MatchAsync([segId], Me, friendIds: Array.Empty<string>());

        Assert.Equal(new[] { Me, Stranger }, await MatchedOwnersAsync(segId));
    }

    // ---------------------------------------------------------------- combined

    [Fact]
    public async Task Combined_DateTypeOwner_AreAndedTogether()
    {
        var all   = await SeedTrackAsync(Me, "friends", new DateTime(2025, 6, 10, 0, 0, 0, DateTimeKind.Unspecified), "Run");
        var badDate = await SeedTrackAsync(Me, "friends", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Unspecified), "Run");
        var badType = await SeedTrackAsync(Me, "friends", new DateTime(2025, 6, 10, 0, 0, 0, DateTimeKind.Unspecified), "Ride");
        var badOwner = await SeedTrackAsync(Friend, "friends", new DateTime(2025, 6, 10, 0, 0, 0, DateTimeKind.Unspecified), "Run");

        var segId = await SeedShapeSegmentAsync(
            new DateRangeFilter { From = "2025-01-01", To = "2025-12-31" },
            new ActivityTypeFilter { Types = { "Run" } },
            new OwnerFilter { UserIds = { Me } });

        await _matcher.MatchAsync([segId], Me, friendIds: new[] { Friend });

        var matched = await MatchedTracksAsync(segId);
        Assert.Equal(new[] { all }, matched);
        Assert.DoesNotContain(badDate, matched);
        Assert.DoesNotContain(badType, matched);
        Assert.DoesNotContain(badOwner, matched);
    }

    // ---------------------------------------------------------------- helpers
    // All tracks share one corridor (lat 47.000..47.009, lng 11.000) inside the segment rectangle.

    private async Task<int> SeedTrackAsync(
        string userId, string visibility, DateTime? startLocal = null, string? activityType = null)
    {
        var activity = new Activity
        {
            StartGpsLat = 47.000, StartGpsLong = 11.000, EndGpsLat = 47.009, EndGpsLong = 11.000,
            UserId = userId, Visibility = visibility,
            StartDateTimeLocal = startLocal, ActivityType = activityType,
        };
        _context.Activities.Add(activity);
        await _context.SaveChangesAsync();
        TrackId trackId = activity.TrackId;

        var pts = new List<GpsData>();
        for (int i = 0; i < 10; i++)
            pts.Add(new GpsData { GpsLat = 47.000 + i * 0.001, GpsLong = 11.000 });
        await TrackSeed.SeedTrackAsync(_context, trackId, pts);

        await _context.Database.ExecuteSqlRawAsync(
            @"INSERT OR IGNORE INTO Activity_Bbox (track_ID, min_lat, max_lat, min_long, max_long)
              VALUES ({0}, 47.000, 47.009, 11.000, 11.000)", trackId.Value);

        return trackId.Value;
    }

    private Task<SegmentId> SeedShapeSegmentAsync(params TrackFilter[] trackFilters) =>
        SeedShapeSegmentAsync(matchOwnerIds: null, trackFilters);

    private async Task<SegmentId> SeedShapeSegmentAsync(string? matchOwnerIds, params TrackFilter[] trackFilters)
    {
        var rules = new RuleDocument
        {
            Shapes = new()
            {
                new ShapeDef { No = 1, Geom = new RectGeom { South = 46.999, West = 10.999, North = 47.010, East = 11.001 } },
            },
            Membership = { new PassesAllRule { Zones = { 1 } } },
            TrackFilters = trackFilters.ToList(),
        };

        var segment = new Segment
        {
            UserId = Me, SegmentKind = "shapes", MatchRulesJson = rules.Serialize(), MatchOwnerIds = matchOwnerIds,
        };
        _context.Segments.Add(segment);
        await _context.SaveChangesAsync();

        return segment.SegmentId;
    }

    // Default run context: current user Me, one friend, so date/type tests over Me's own tracks match freely.
    private Task MatchAsync(SegmentId segId) => _matcher.MatchAsync([segId], Me, friendIds: new[] { Friend });

    private async Task<int[]> MatchedTracksAsync(SegmentId segId) =>
        (await _context.GpsInSegments
            .Where(e => e.SegmentId == segId)
            .Select(e => e.TrackId)
            .Distinct()
            .OrderBy(t => t)
            .ToArrayAsync()).Select(t => t.Value).ToArray();

    private async Task<string[]> MatchedOwnersAsync(SegmentId segId)
    {
        var trackIds = await _context.GpsInSegments
            .Where(e => e.SegmentId == segId)
            .Select(e => e.TrackId)
            .ToListAsync();

        return await _context.Activities
            .Where(a => trackIds.Contains(a.TrackId))
            .Select(a => a.UserId!)
            .Distinct()
            .OrderBy(u => u)
            .ToArrayAsync();
    }
}
