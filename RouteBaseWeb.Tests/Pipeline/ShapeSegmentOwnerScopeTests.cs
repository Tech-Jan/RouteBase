using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Lifecycle;
using RouteBaseWeb.Services.Segments;
using RouteBaseWeb.Services.Tracks;
using RouteBaseWeb.Tests.Helpers;
using RouteBaseWeb.Services.Segments.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Pipeline;

/// <summary>
/// Owner-scope + re-authorisation tests for <see cref="ShapeSegmentMatcher"/>.
///
/// A shape segment stores the owners (<c>match_owner_ids</c>) whose tracks it matches; null means the
/// owner's own tracks only (legacy). On every run the matcher re-applies the visibility rule (own / public /
/// friends-with-friends-visibility) so a revoked friendship or a private/non-friend owner is dropped even
/// though the stored owner list is unchanged.
///
/// Uses a real in-memory SQLite database so the raw candidate SQL (IN-lists + visibility clause) runs exactly
/// as in production. All tracks share one corridor inside a single mandatory rectangle, so whether a track
/// matches is governed purely by the owner scope + visibility, not geometry.
/// </summary>
public class ShapeSegmentOwnerScopeTests : IAsyncLifetime
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

    // -------------------------------------------------------------------------

    [Fact]
    public async Task NullScope_MatchesOnlyOwnTracks()
    {
        await SeedTrackAsync(Me, "friends");
        await SeedTrackAsync(Friend, "friends");
        var segId = await SeedShapeSegmentAsync(matchOwnerIds: null);

        // Even though "friend" is a current friend, a null scope is own-only.
        await _matcher.MatchAsync([segId], Me, friendIds: new[] { Friend });

        Assert.Equal(new[] { Me }, await MatchedOwnersAsync(segId));
    }

    [Fact]
    public async Task FriendInScope_WithFriendsVisibility_IsMatched()
    {
        var meTrack     = await SeedTrackAsync(Me, "friends");
        var friendTrack = await SeedTrackAsync(Friend, "friends");
        var segId       = await SeedShapeSegmentAsync(matchOwnerIds: $"{Me},{Friend}");

        await _matcher.MatchAsync([segId], Me, friendIds: new[] { Friend });

        // MatchedOwnersAsync sorts alphabetically (OrderBy u => u): "friend" < "me".
        Assert.Equal(new[] { Friend, Me }, await MatchedOwnersAsync(segId));
    }

    [Fact]
    public async Task FriendInScope_AfterFriendshipRevoked_IsDropped()
    {
        await SeedTrackAsync(Me, "friends");
        await SeedTrackAsync(Friend, "friends");
        var segId = await SeedShapeSegmentAsync(matchOwnerIds: $"{Me},{Friend}");

        // Friendship revoked → friendIds no longer contains "friend"; their friends-visibility track is dropped.
        await _matcher.MatchAsync([segId], Me, friendIds: Array.Empty<string>());

        Assert.Equal(new[] { Me }, await MatchedOwnersAsync(segId));
    }

    [Fact]
    public async Task NonFriendPrivateOwnerInScope_IsExcluded()
    {
        await SeedTrackAsync(Me, "friends");
        await SeedTrackAsync(Stranger, "private");
        var segId = await SeedShapeSegmentAsync(matchOwnerIds: $"{Me},{Stranger}");

        await _matcher.MatchAsync([segId], Me, friendIds: Array.Empty<string>());

        Assert.Equal(new[] { Me }, await MatchedOwnersAsync(segId));
    }

    [Fact]
    public async Task PublicOwnerInScope_IsMatched_EvenWithoutFriendship()
    {
        await SeedTrackAsync(Me, "friends");
        await SeedTrackAsync(Stranger, "public");
        var segId = await SeedShapeSegmentAsync(matchOwnerIds: $"{Me},{Stranger}");

        await _matcher.MatchAsync([segId], Me, friendIds: Array.Empty<string>());

        Assert.Equal(new[] { Me, Stranger }, await MatchedOwnersAsync(segId));
    }

    // -------------------------------------------------------------------------
    // Lifecycle gate: a non-owner's non-released track must NOT be matched, even when its stored
    // visibility column is still friends/public. VisibleActivities hides non-released rows from every
    // other read path (VisibilityService.cs), and the candidate query claims to mirror it — but omitted
    // the lifecycle filter, leaking the existence + geo-correlation of a friend's trashed / awaiting-review
    // activity through the shape preview. The gate applies to the cross-user branch only; the owner's own
    // tracks stay owner-scoped (see OwnNonReleasedTrack_StillMatched).

    [Fact]
    public async Task FriendInScope_TrashedTrack_IsExcluded()
    {
        await SeedTrackAsync(Me, "friends");
        await SeedTrackAsync(Friend, "friends", lifecycle: LifecycleStates.Trashed);
        var segId = await SeedShapeSegmentAsync(matchOwnerIds: $"{Me},{Friend}");

        await _matcher.MatchAsync([segId], Me, friendIds: new[] { Friend });

        // The friend's trashed track is invisible everywhere else; it must not ride into the segment.
        Assert.Equal(new[] { Me }, await MatchedOwnersAsync(segId));
    }

    [Fact]
    public async Task PublicOwnerInScope_InWorkTrack_IsExcluded()
    {
        await SeedTrackAsync(Me, "friends");
        await SeedTrackAsync(Stranger, "public", lifecycle: LifecycleStates.InWork);
        var segId = await SeedShapeSegmentAsync(matchOwnerIds: $"{Me},{Stranger}");

        await _matcher.MatchAsync([segId], Me, friendIds: Array.Empty<string>());

        Assert.Equal(new[] { Me }, await MatchedOwnersAsync(segId));
    }

    [Fact]
    public async Task OwnNonReleasedTrack_StillMatched()
    {
        // The lifecycle gate is scoped to the cross-user branch: the caller's OWN non-released tracks stay
        // owner-scoped exactly as the polyline pipeline treats them (MatchEngineService is own-only, no
        // lifecycle filter). Only pins that the fix did not narrow owner-side matching.
        await SeedTrackAsync(Me, "friends", lifecycle: LifecycleStates.InWork);
        var segId = await SeedShapeSegmentAsync(matchOwnerIds: Me);

        await _matcher.MatchAsync([segId], Me, friendIds: Array.Empty<string>());

        Assert.Equal(new[] { Me }, await MatchedOwnersAsync(segId));
    }

    // -------------------------------------------------------------------------
    // Helpers — all tracks share one corridor (lat 47.000..47.009, lng 11.000) inside the segment rectangle.

    private async Task<int> SeedTrackAsync(string userId, string visibility, string lifecycle = LifecycleStates.Released)
    {
        var activity = new Activity
        {
            StartGpsLat = 47.000, StartGpsLong = 11.000, EndGpsLat = 47.009, EndGpsLong = 11.000,
            UserId = userId, Visibility = visibility, Lifecycle = lifecycle,
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

    private async Task<SegmentId> SeedShapeSegmentAsync(string? matchOwnerIds)
    {
        // One must-pass rectangle covering the whole corridor, whole-track extent.
        var rules = new RuleDocument
        {
            Shapes = new()
            {
                new ShapeDef { No = 1, Geom = new RectGeom { South = 46.999, West = 10.999, North = 47.010, East = 11.001 } },
            },
            Membership = { new PassesAllRule { Zones = { 1 } } },
        };

        var segment = new Segment
        {
            UserId = Me, SegmentKind = "shapes", MatchRulesJson = rules.Serialize(), MatchOwnerIds = matchOwnerIds,
        };
        _context.Segments.Add(segment);
        await _context.SaveChangesAsync();

        return segment.SegmentId;
    }

    // The owner user-ids whose tracks ended up matched into the segment, sorted for stable assertions.
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
