using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Geometry;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Segments;
using RouteBaseWeb.Services.Segments.Rules;
using RouteBaseWeb.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Pipeline;

/// <summary>
/// SB-01 pins: <see cref="ShapeSegmentMatcher.PreviewExactTrackIdsAsync"/> caps the candidate set it
/// will confirm (<see cref="ShapeSegmentMatcher.MaxExactPreviewCandidates"/>) and flags the result
/// truncated, so one broad-shape preview can't force unbounded GPS decode outside the queue's admission
/// control. Candidates are seeded as Activity_Bbox rows only — truncation is decided on the stage-1
/// candidate count before any track is decoded, so no blobs are needed.
/// </summary>
public class ShapeSegmentMatcherPreviewCapTests : IAsyncLifetime
{
    private const string Me = "me";

    private RouteBaseDbContext _context = null!;
    private ShapeSegmentMatcher _matcher = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>()
            .UseSqlite($"Data Source=file:previewcap_{Guid.NewGuid():N}?mode=memory&cache=shared")
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

        _matcher = new ShapeSegmentMatcher(_context, NullLogger<ShapeSegmentMatcher>.Instance, new DirectTrackReader(_context));
    }

    public async Task DisposeAsync()
    {
        await _context.Database.EnsureDeletedAsync();
        await _context.DisposeAsync();
    }

    // A zone + lap gate covering lat 47.000..47.010 @ lng 11.000 — every seeded bbox overlaps it, so
    // stage-1 CouldMatch keeps every candidate.
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

    private async Task SeedCandidatesAsync(int count)
    {
        for (int i = 0; i < count; i++)
            _context.Activities.Add(new Activity
            {
                UserId = Me, Visibility = "private",
                StartGpsLat = 47.000, StartGpsLong = 11.000, EndGpsLat = 47.010, EndGpsLong = 11.000,
            });
        await _context.SaveChangesAsync();

        var ids = await _context.Activities.Select(a => a.TrackId).ToListAsync();
        foreach (var id in ids)
            await _context.Database.ExecuteSqlRawAsync(
                @"INSERT OR IGNORE INTO Activity_Bbox (track_ID, min_lat, max_lat, min_long, max_long)
                  VALUES ({0}, 47.000, 47.010, 11.000, 11.000)", id.Value);
    }

    [Fact]
    public async Task PreviewExact_CandidatesOverCap_TruncatesAndFlags()
    {
        await SeedCandidatesAsync(ShapeSegmentMatcher.MaxExactPreviewCandidates + 1);

        var result = await _matcher.PreviewExactTrackIdsAsync(CoveringDoc(), Me, Array.Empty<string>());

        Assert.True(result.Truncated);
        // No blobs seeded → no confirmed matches, but the cap still bounds the work.
        Assert.True(result.TrackIds.Count <= ShapeSegmentMatcher.MaxExactPreviewCandidates);
    }

    [Fact]
    public async Task PreviewExact_CandidatesUnderCap_NoTruncation()
    {
        await SeedCandidatesAsync(3);

        var result = await _matcher.PreviewExactTrackIdsAsync(CoveringDoc(), Me, Array.Empty<string>());

        Assert.False(result.Truncated);
    }
}
