using RouteBaseWeb.Models;
using RouteBaseWeb.Models.GridFilter;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Lifecycle;
using RouteBaseWeb.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Pins the <c>restrictIds</c> id-allowlist added for the "Merge table ↔ shapes" feature: the grid query
/// AND-narrows to the given track ids (the tracks a map-drawn shape matched, resolved by the controller) and
/// still intersects with the column filters. Kept geometry-free — the id set stands in for the shape match,
/// which is covered separately by the ShapeSegmentMatcher tests.
/// </summary>
public class ActivityQueryServiceRestrictIdsTests : IAsyncLifetime
{
    private const string Me = "me";

    private RouteBaseDbContext _context = null!;
    private ActivityQueryService _svc = null!;
    private int _short, _long1, _long2; // TrackIds: one ≤5 km, two > that

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>()
            .UseSqlite($"Data Source=file:aq_restrict_{Guid.NewGuid():N}?mode=memory&cache=shared").Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();

        _context.Users.Add(new AppUser { UserId = Me, DisplayName = Me, Email = "me@t.com", IsAdmin = false });
        _short = await AddAsync(distanceMetres: 3000);
        _long1 = await AddAsync(distanceMetres: 8000);
        _long2 = await AddAsync(distanceMetres: 9000);

        _svc = new ActivityQueryService(
            _context, new TestVisibilityService(Me), new MemoryCache(new MemoryCacheOptions()), new UserCacheInvalidator());
    }

    public Task DisposeAsync() => _context.DisposeAsync().AsTask();

    private async Task<int> AddAsync(double distanceMetres)
    {
        var a = new Activity
        {
            UserId = Me, Visibility = "private", Lifecycle = LifecycleStates.Released,
            TotalDistance = distanceMetres, StartDateTimeLocal = new DateTime(2025, 6, 1, 8, 0, 0, DateTimeKind.Unspecified),
        };
        _context.Activities.Add(a);
        await _context.SaveChangesAsync();
        return a.TrackId.Value;
    }

    private async Task<List<int>> FilteredAsync(GridFilterCollection? filters = null, IReadOnlyCollection<int>? restrictIds = null) =>
        (await _svc.GetFilteredActivityIdsAsync(activityType: null, startDate: null, endDate: null, filters: filters, restrictIds: restrictIds?.Select(i => new TrackId(i)).ToList())).Select(t => t.Value).ToList();

    [Fact]
    public async Task NullRestrictIds_ReturnsAllVisible()
    {
        var ids = await FilteredAsync();
        Assert.Equal(new[] { _short, _long1, _long2 }.OrderBy(x => x), ids.OrderBy(x => x));
    }

    [Fact]
    public async Task RestrictIds_NarrowsToTheAllowlist()
    {
        var ids = await FilteredAsync(restrictIds: new[] { _short, _long2 });
        Assert.Equal(new[] { _short, _long2 }.OrderBy(x => x), ids.OrderBy(x => x));
        Assert.DoesNotContain(_long1, ids);
    }

    [Fact]
    public async Task RestrictIds_IntersectsWithColumnFilters()
    {
        // Shape matched {short, long1}; a distance ≤5 km column filter must further exclude long1 → {short}.
        var filters = new GridFilterCollection();
        filters.Items["distance"] = new ColumnFilter { Range = "0-5" }; // km → ≤5000 m
        var ids = await FilteredAsync(filters: filters, restrictIds: new[] { _short, _long1 });
        Assert.Equal(new[] { _short }, ids);
    }

    [Fact]
    public async Task EmptyRestrictIds_ReturnsNothing()
    {
        // A shape that matched no tracks → empty allowlist → empty table (not "unfiltered").
        var ids = await FilteredAsync(restrictIds: Array.Empty<int>());
        Assert.Empty(ids);
    }
}
