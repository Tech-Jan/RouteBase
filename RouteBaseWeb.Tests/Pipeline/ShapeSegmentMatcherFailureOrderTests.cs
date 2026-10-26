using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Geometry;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Segments;
using RouteBaseWeb.Services.Segments.Rules;
using RouteBaseWeb.Services.Tracks;
using RouteBaseWeb.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Pipeline;

/// <summary>
/// SC-01 pin: <see cref="ShapeSegmentMatcher.MatchAsync"/> now computes the full match set BEFORE it
/// deletes the segment's stored efforts, so a candidate/confirm step that throws leaves prior efforts
/// intact (pre-fix it wiped first, so a throwing re-analysis permanently zeroed the segment). Driven by
/// an <see cref="ITrackReader"/> that throws during Stage-2 confirmation.
/// </summary>
public class ShapeSegmentMatcherFailureOrderTests : IAsyncLifetime
{
    private const string Me = "me";

    private RouteBaseDbContext _context = null!;

    private sealed class ThrowingTrackReader : ITrackReader
    {
        public Task<DecodedTrack?> GetTrackAsync(TrackId trackId, CancellationToken ct = default) =>
            throw new InvalidOperationException("simulated decode failure");
    }

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>()
            .UseSqlite($"Data Source=file:failorder_{Guid.NewGuid():N}?mode=memory&cache=shared")
            .Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();

        var schema = new DbSchemaManager(_context);
        await schema.EnsureActivityBboxTableAsync();
        await schema.EnsureTrackContentTablesAsync();
        await schema.EnsureIndexesAsync();

        _context.Users.Add(new AppUser
        {
            UserId = Me, DisplayName = Me, Email = $"{Me}@t.com", IsAdmin = false,
            CreatedAt = DateTime.UtcNow.ToString("O"), LastLogin = DateTime.UtcNow.ToString("O"),
        });
        await _context.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.Database.EnsureDeletedAsync();
        await _context.DisposeAsync();
    }

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

    [Fact]
    public async Task MatchAsync_QueryFailure_PreservesExistingEfforts()
    {
        // Candidate activity (bbox overlaps the shape) so Stage 1 keeps it and Stage 2 tries to decode it.
        var activity = new Activity
        {
            UserId = Me, Visibility = "private",
            StartGpsLat = 47.000, StartGpsLong = 11.000, EndGpsLat = 47.010, EndGpsLong = 11.000,
        };
        _context.Activities.Add(activity);
        await _context.SaveChangesAsync();
        await _context.Database.ExecuteSqlRawAsync(
            @"INSERT OR IGNORE INTO Activity_Bbox (track_ID, min_lat, max_lat, min_long, max_long)
              VALUES ({0}, 47.000, 47.010, 11.000, 11.000)", activity.TrackId.Value);

        var seg = new Segment { UserId = Me, SegmentKind = "shapes", MatchRulesJson = CoveringDoc().Serialize() };
        _context.Segments.Add(seg);
        await _context.SaveChangesAsync();

        // Pre-existing effort on the segment.
        _context.GpsInSegments.Add(new GpsInSegment { TrackId = activity.TrackId, SegmentId = seg.SegmentId });
        await _context.SaveChangesAsync();

        var matcher = new ShapeSegmentMatcher(_context, NullLogger<ShapeSegmentMatcher>.Instance, new ThrowingTrackReader());

        await Assert.ThrowsAnyAsync<Exception>(
            () => matcher.MatchAsync(new[] { seg.SegmentId }, Me, Array.Empty<string>()));

        // Delete now runs only after a successful confirm → the prior effort survives the throw.
        var surviving = await _context.GpsInSegments.CountAsync(g => g.SegmentId == seg.SegmentId);
        Assert.Equal(1, surviving);
    }

    // matcher control-flow guards that had no coverage.

    private ShapeSegmentMatcher Matcher() =>
        new(_context, NullLogger<ShapeSegmentMatcher>.Instance, new DirectTrackReader(_context));

    // Mut 61 (`if (meta == null) continue;` removed): a segment id that resolves to no row is skipped, not
    // dereferenced. The mutant NREs on meta.MatchRulesJson.
    [Fact]
    public async Task MatchAsync_UnknownSegment_IsSkippedNotDereferenced()
    {
        await Matcher().MatchAsync(new[] { new SegmentId(987654) }, Me, Array.Empty<string>());

        Assert.Equal(0, await _context.GpsInSegments.CountAsync());
    }

    // Mut 69 (`continue;` after the no-usable-document warning removed): a segment whose rules JSON does not
    // compile is skipped. The mutant falls through to `evaluator.TrackFilters` and NREs on the null evaluator.
    [Fact]
    public async Task MatchAsync_UncompilableRules_AreSkippedNotDereferenced()
    {
        var seg = new Segment { UserId = Me, SegmentKind = "shapes", MatchRulesJson = "{ not valid json" };
        _context.Segments.Add(seg);
        await _context.SaveChangesAsync();

        await Matcher().MatchAsync(new[] { seg.SegmentId }, Me, Array.Empty<string>());

        Assert.Equal(0, await _context.GpsInSegments.CountAsync(g => g.SegmentId == seg.SegmentId));
    }

    // Mut 91 (DELETE statement removed) + mut 92 (DELETE SQL blanked to ""): a re-analysis with no surviving
    // matches still clears the segment's stale efforts. There are no candidate bboxes, so `matches` is empty
    // and only the unconditional DELETE runs — the pre-existing effort must be gone afterwards.
    [Fact]
    public async Task MatchAsync_NoMatches_ClearsStaleEfforts()
    {
        var activity = new Activity { UserId = Me, Visibility = "private" };
        _context.Activities.Add(activity);
        await _context.SaveChangesAsync();

        var seg = new Segment { UserId = Me, SegmentKind = "shapes", MatchRulesJson = CoveringDoc().Serialize() };
        _context.Segments.Add(seg);
        await _context.SaveChangesAsync();

        _context.GpsInSegments.Add(new GpsInSegment { TrackId = activity.TrackId, SegmentId = seg.SegmentId });
        await _context.SaveChangesAsync();

        // No Activity_Bbox row for the activity → zero candidates → matches empty → DELETE-only path.
        await Matcher().MatchAsync(new[] { seg.SegmentId }, Me, Array.Empty<string>());

        Assert.Equal(0, await _context.GpsInSegments.CountAsync(g => g.SegmentId == seg.SegmentId));
    }
}
