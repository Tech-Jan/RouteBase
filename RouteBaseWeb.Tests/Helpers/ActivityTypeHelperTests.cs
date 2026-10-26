using RouteBaseWeb.Helpers;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RouteBaseWeb.Tests.Helpers;

/// <summary>
/// Characterization tests for <see cref="ActivityTypeHelper"/> — pin the type-group mapping
/// (substring check order, incl. "trail_hike"→run), the four-way partition of
/// <see cref="ActivityTypeHelper.ApplyTypeGroupFilter"/>, and the exact-match fallback, BEFORE the
/// QW-08 keyword-dedup refactor. Named in-memory SQLite so <c>ToLower().Contains</c> translates to the
/// same SQL production runs against.
/// </summary>
public class ActivityTypeHelperTests : IAsyncLifetime
{
    private RouteBaseDbContext _context = null!;
    private int _nextTrackId = 1;

    public async Task InitializeAsync()
    {
        var dbName = $"acttype_test_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>()
            .UseSqlite($"Data Source=file:{dbName}?mode=memory&cache=shared")
            .Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();
        _context.Users.Add(new AppUser
        {
            UserId = "u", DisplayName = "u", Email = "u@test.com", IsAdmin = false,
        });
        await _context.SaveChangesAsync();
    }

    public Task DisposeAsync() => _context.DisposeAsync().AsTask();

    private async Task AddAsync(string? type)
    {
        _context.Activities.Add(new Activity
        {
            TrackId          = new TrackId(_nextTrackId++),
            UserId           = "u",
            StartDateTimeUtc = DateTime.UtcNow,
            ActivityType     = type,
        });
        await _context.SaveChangesAsync();
    }

    // ─── GetTypeGroup: mapping + check order ───

    [Theory]
    [InlineData("Cycling", "bike")]
    [InlineData("MountainBiking", "bike")]
    [InlineData("TrailRun", "run")]
    [InlineData("trail_hike", "run")]   // "trail" (run) wins over "hik" (walk) by check order
    [InlineData("DogWalk", "walk")]
    [InlineData("Hiking", "walk")]
    [InlineData("Yoga", "other")]
    [InlineData(null, "other")]
    [InlineData("", "other")]
    public void GetTypeGroup_MapsToCanonicalGroup(string? activityType, string expected)
        => Assert.Equal(expected, ActivityTypeHelper.GetTypeGroup(activityType));

    // ─── ApplyTypeGroupFilter: partitions all rows, matching GetTypeGroup ───

    // NOTE: the per-group filters are independent substring OR-chains, so they can OVERLAP —
    // "trail_hike" matches BOTH the run filter ("trail") and the walk filter ("hik"). GetTypeGroup's
    // ordered if/return resolves it to "run", but the walk filter still returns it. This test pins that
    // exact (non-partitioning) behavior; the QW-08 refactor must preserve it verbatim.
    [Fact]
    public async Task ApplyTypeGroupFilter_ReturnsExactPerGroupMembership()
    {
        string?[] types = ["Cycling", "MountainBiking", "TrailRun", "trail_hike", "DogWalk", "Hiking", "Yoga", null];
        foreach (var t in types)
            await AddAsync(t);

        // Ordered by TrackId (insertion order of the seed array above).
        Assert.Equal(["Cycling", "MountainBiking"], await GroupTypesAsync("bike"));
        Assert.Equal(["TrailRun", "trail_hike"], await GroupTypesAsync("run"));
        Assert.Equal(["trail_hike", "DogWalk", "Hiking"], await GroupTypesAsync("walk"));
        // "other" = null OR matches none of the 8 group substrings.
        Assert.Equal(["Yoga", null], await GroupTypesAsync("other"));

        // Each row's GetTypeGroup verdict is a subset of the matching filter's result.
        foreach (var a in await _context.Activities.ToListAsync())
        {
            var group = ActivityTypeHelper.GetTypeGroup(a.ActivityType);
            Assert.Contains(a.TrackId.Value, await GroupIdsAsync(group));
        }
    }

    [Fact]
    public async Task ApplyTypeGroupFilter_UnknownValue_FallsBackToExactMatch()
    {
        await AddAsync("Yoga");
        await AddAsync("Yoga2");
        await AddAsync("Cycling");

        var ids = await GroupIdsAsync("Yoga");

        var tids = ids.Select(i => new TrackId(i)).ToList();
        var types = await _context.Activities
            .Where(a => tids.Contains(a.TrackId))
            .Select(a => a.ActivityType)
            .ToListAsync();
        Assert.Equal(["Yoga"], types);
    }

    private async Task<List<int>> GroupIdsAsync(string group)
        => (await ActivityTypeHelper
            .ApplyTypeGroupFilter(_context.Activities.AsQueryable(), group)
            .Select(a => a.TrackId)
            .OrderBy(id => id)
            .ToListAsync()).Select(t => t.Value).ToList();

    private async Task<List<string?>> GroupTypesAsync(string group)
        => await ActivityTypeHelper
            .ApplyTypeGroupFilter(_context.Activities.AsQueryable(), group)
            .OrderBy(a => a.TrackId)
            .Select(a => a.ActivityType)
            .ToListAsync();
}
