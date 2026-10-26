using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Pins the B7 tour delete (tour ROW only — activities keep living, lose the link; owner-scoped;
/// per-user cache evicted) and the GetAll list contract: owner-scoped, StartDate-descending,
/// activities included, cached per user with staleness bounded only by the TTL.
/// </summary>
public class MultiDayTourServiceTests : IAsyncLifetime
{
    private const string Me = "me";
    private const string Other = "other";
    private RouteBaseDbContext _context = null!;
    private MemoryCache _cache = null!;

    public async Task InitializeAsync()
    {
        var conn = $"Data Source=file:tours_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.OpenConnectionAsync();
        await _context.Database.EnsureCreatedAsync();
        _cache = new MemoryCache(new MemoryCacheOptions());

        foreach (var id in new[] { Me, Other })
            _context.Users.Add(new AppUser { UserId = id, DisplayName = id, Email = $"{id}@t.com", IsAdmin = false });
        await _context.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        _cache.Dispose();
        await _context.DisposeAsync();
    }

    private MultiDayTourService ServiceFor(string userId) =>
        new(_context, _cache, new TestVisibilityService(userId));

    private async Task<int> SeedTourWithActivitiesAsync(string owner, params int[] trackIds)
        => await SeedTourAsync(owner, "Alps", null, trackIds);

    private async Task<int> SeedTourAsync(string owner, string name, DateTime? start, params int[] trackIds)
    {
        var tour = new MultiDayTour { MultiDayTourName = name, StartDate = start, UserId = owner };
        _context.MultiDayTours.Add(tour);
        await _context.SaveChangesAsync();
        foreach (var id in trackIds)
            _context.Activities.Add(new Activity
            {
                TrackId = new TrackId(id), UserId = owner, ActivityType = "Ride", MultiDayId = tour.MultiDayId,
            });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return tour.MultiDayId;
    }

    [Fact]
    public async Task DeleteTour_RemovesRowOnly_ActivitiesKeepLivingUnlinked()
    {
        var tourId = await SeedTourWithActivitiesAsync(Me, 1, 2);

        Assert.True(await ServiceFor(Me).DeleteTourAsync(tourId));

        Assert.Equal(0, await _context.MultiDayTours.AsNoTracking().CountAsync(t => t.MultiDayId == tourId));
        var activities = await _context.Activities.AsNoTracking()
            .Where(a => a.TrackId == new TrackId(1) || a.TrackId == new TrackId(2)).ToListAsync();
        Assert.Equal(2, activities.Count);              // activities keep living…
        Assert.All(activities, a => Assert.Null(a.MultiDayId)); // …they just lose the tour link
    }

    [Fact]
    public async Task DeleteTour_ForeignOrMissing_FalseAndUntouched()
    {
        var tourId = await SeedTourWithActivitiesAsync(Other, 3);

        Assert.False(await ServiceFor(Me).DeleteTourAsync(tourId)); // foreign
        Assert.False(await ServiceFor(Me).DeleteTourAsync(99999));  // missing

        Assert.Equal(1, await _context.MultiDayTours.AsNoTracking().CountAsync(t => t.MultiDayId == tourId));
        Assert.Equal(tourId, (await _context.Activities.AsNoTracking().SingleAsync(a => a.TrackId == new TrackId(3))).MultiDayId);
    }

    [Fact]
    public async Task GetAllTours_OwnerScoped_ExcludesForeignTours()
    {
        var mine = await SeedTourAsync(Me, "Mine", null);
        await SeedTourAsync(Other, "Theirs", null);

        var tours = await ServiceFor(Me).GetAllMultiDayToursAsync();

        var tour = Assert.Single(tours);
        Assert.Equal(mine, tour.MultiDayId);
        Assert.Equal(Me, tour.UserId);
    }

    [Fact]
    public async Task GetAllTours_OrdersByStartDateDescending_AndIncludesActivities()
    {
        await SeedTourAsync(Me, "Middle", new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Unspecified), 1);
        await SeedTourAsync(Me, "Newest", new DateTime(2026, 1, 20, 0, 0, 0, DateTimeKind.Unspecified), 2, 3);
        await SeedTourAsync(Me, "Oldest", new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Unspecified));

        var tours = await ServiceFor(Me).GetAllMultiDayToursAsync();

        Assert.Equal(new[] { "Newest", "Middle", "Oldest" }, tours.Select(t => t.MultiDayTourName), StringComparer.Ordinal);
        Assert.Equal(new[] { new TrackId(2), new TrackId(3) }, tours[0].Activities.Select(a => a.TrackId).OrderBy(id => id.Value));
        Assert.Equal(new[] { 1 }, tours[1].Activities.Select(a => a.TrackId.Value));
        Assert.Empty(tours[2].Activities);
    }

    [Fact]
    public async Task GetAllTours_SecondCallWithinTtl_ServesCacheDespiteNewTour()
    {
        await SeedTourAsync(Me, "First", null);
        var service = ServiceFor(Me);

        Assert.Single(await service.GetAllMultiDayToursAsync()); // primes the cache

        // A tour added without going through DeleteTourAsync's eviction stays invisible
        // until the TTL expires — that staleness bound IS the documented contract.
        await SeedTourAsync(Me, "Second", null);
        Assert.Single(await service.GetAllMultiDayToursAsync());
    }

    [Fact]
    public async Task GetAllTours_CacheIsPerUser_NoCrossUserLeak()
    {
        await SeedTourAsync(Me, "Mine", null);
        await SeedTourAsync(Other, "Theirs", null);

        Assert.Equal(Me, (await ServiceFor(Me).GetAllMultiDayToursAsync()).Single().UserId);

        // Same shared IMemoryCache: Other must get their own list, not Me's cached one.
        Assert.Equal(Other, (await ServiceFor(Other).GetAllMultiDayToursAsync()).Single().UserId);
    }

    [Fact]
    public async Task DeleteTour_EvictsOwnersTourListCache()
    {
        var tourId = await SeedTourWithActivitiesAsync(Me);
        var service = ServiceFor(Me);

        Assert.Single(await service.GetAllMultiDayToursAsync()); // primes the cache
        Assert.True(await service.DeleteTourAsync(tourId));
        Assert.Empty(await service.GetAllMultiDayToursAsync());  // stale cache would still return 1
    }
}
