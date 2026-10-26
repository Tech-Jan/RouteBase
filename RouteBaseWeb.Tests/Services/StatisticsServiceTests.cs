using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Tests for <see cref="StatisticsService"/> — the Statistics-page summary (per-raw-type grouping,
/// overall-as-sum, Total Descent, owner/type/date filtering) and the cumulative-by-type progression
/// (ported from the old DashboardService cumulative behaviour). Named in-memory SQLite (shared cache)
/// so GroupBy/aggregate NULL semantics match production.
/// </summary>
public class StatisticsServiceTests : IAsyncLifetime
{
    private RouteBaseDbContext _context = null!;
    private StatisticsService _service = null!;
    private const string U1 = "user-1";
    private const string U2 = "user-2";
    private int _nextTrackId = 1;

    public async Task InitializeAsync()
    {
        var dbName = $"statistics_test_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>()
            .UseSqlite($"Data Source=file:{dbName}?mode=memory&cache=shared")
            .Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();
        foreach (var id in new[] { U1, U2 })
            _context.Users.Add(new AppUser { UserId = id, DisplayName = id, Email = $"{id}@test.com" });
        await _context.SaveChangesAsync();
        // Both users visible so owner-id filtering (not visibility) is what scopes the queries.
        _service = new StatisticsService(_context, new MultiUserVisibility(U1, U2));
    }

    public Task DisposeAsync() => _context.DisposeAsync().AsTask();

    private async Task AddAsync(
        string user = U1, string? type = "cycling", DateTime? local = null,
        double distance = 0, double ascent = 0, double descent = 0,
        double moving = 0, double speed = 0, string visibility = "friends")
    {
        _context.Activities.Add(new Activity
        {
            TrackId            = new TrackId(_nextTrackId++),
            UserId             = user,
            StartDateTimeLocal = local ?? new DateTime(2025, 6, 1, 10, 0, 0, DateTimeKind.Unspecified),
            StartDateTimeUtc   = local ?? new DateTime(2025, 6, 1, 10, 0, 0, DateTimeKind.Unspecified),
            ActivityType       = type,
            TotalDistance      = distance,
            TotalAscent        = ascent,
            TotalDescent       = descent,
            TimeMoving         = moving,
            TimeTotal          = moving,
            SpeedAvg           = speed,
            Visibility         = visibility,
        });
        await _context.SaveChangesAsync();
    }

    private static StatisticsFilter Filter(
        IReadOnlyList<string>? types = null, DateTime? from = null, DateTime? to = null,
        IReadOnlyList<string>? ownerIds = null, bool matchNone = false,
        IReadOnlyList<string>? visibilities = null)
        => new(types, from, to, ownerIds, matchNone, visibilities);

    // ─── Summary: grouping, overall-as-sum, Unknown bucket, Descent ───

    [Fact]
    public async Task GetSummaryAsync_GroupsByRawType_WithUnknownBucket()
    {
        await AddAsync(type: "cycling", distance: 10000, descent: 100);
        await AddAsync(type: "cycling", distance: 5000, descent: 50);
        await AddAsync(type: "running", distance: 4000, descent: 20);
        await AddAsync(type: null, distance: 1000, descent: 5); // null → "Unknown"

        var summary = await _service.GetSummaryAsync(Filter());

        Assert.Equal(new[] { "cycling", "running", "Unknown" }, summary.ByType.Select(t => t.Type), StringComparer.Ordinal);
        var cycling = summary.ByType.Single(t => t.Type == "cycling").Stats;
        Assert.Equal(2, cycling.TotalActivities);
        Assert.Equal(15000, cycling.TotalDistance);
        Assert.Equal(150, cycling.TotalDescent);
        Assert.Equal("Unknown", summary.ByType[2].Type);
    }

    [Fact]
    public async Task GetSummaryAsync_OverallSumsGroups_IncludingDescent_AndWeightedSpeed()
    {
        await AddAsync(type: "cycling", distance: 10000, ascent: 200, descent: 180, moving: 3600, speed: 6);
        await AddAsync(type: "running", distance: 4000, ascent: 80, descent: 60, moving: 1800, speed: 3);
        await AddAsync(type: "running", distance: 2000, ascent: 40, descent: 30, moving: 900, speed: 3);

        var overall = (await _service.GetSummaryAsync(Filter())).Overall;

        Assert.Equal(3, overall.TotalActivities);
        Assert.Equal(16000, overall.TotalDistance);
        Assert.Equal(320, overall.TotalAscent);
        Assert.Equal(270, overall.TotalDescent);
        Assert.Equal(6300, overall.TotalMovingTime);
        // Count-weighted mean of per-activity speeds: (6+3+3)/3 = 4
        Assert.Equal(4.0, overall.AverageSpeed, 6);
    }

    [Fact]
    public async Task GetSummaryAsync_EmptyDb_ReturnsZeroedOverall_AndNoTypes()
    {
        var summary = await _service.GetSummaryAsync(Filter());

        Assert.Empty(summary.ByType);
        Assert.Equal(0, summary.Overall.TotalActivities);
        Assert.Equal(0, summary.Overall.TotalDescent);
        Assert.Equal(0, summary.Overall.AverageSpeed);
    }

    // ─── Summary: filters ───

    [Fact]
    public async Task GetSummaryAsync_ExactTypeFilter_KeepsOnlySelectedTypes()
    {
        await AddAsync(type: "cycling", distance: 10000);
        await AddAsync(type: "running", distance: 4000);

        var summary = await _service.GetSummaryAsync(Filter(types: new[] { "cycling" }));

        Assert.Equal(new[] { "cycling" }, summary.ByType.Select(t => t.Type), StringComparer.Ordinal);
        Assert.Equal(10000, summary.Overall.TotalDistance);
    }

    [Fact]
    public async Task GetSummaryAsync_VisibilityFilter_KeepsOnlySelectedVisibilities()
    {
        await AddAsync(distance: 10000, visibility: "private");
        await AddAsync(distance: 4000, visibility: "friends");
        await AddAsync(distance: 2000, visibility: "public");

        var privateOnly = await _service.GetSummaryAsync(Filter(visibilities: new[] { "private" }));
        Assert.Equal(10000, privateOnly.Overall.TotalDistance);
        Assert.Equal(1, privateOnly.Overall.TotalActivities);

        var privateOrPublic = await _service.GetSummaryAsync(Filter(visibilities: new[] { "private", "public" }));
        Assert.Equal(12000, privateOrPublic.Overall.TotalDistance);

        var all = await _service.GetSummaryAsync(Filter());
        Assert.Equal(16000, all.Overall.TotalDistance);
    }

    [Fact]
    public async Task GetSummaryAsync_DateRange_EndDayInclusiveOnLocalDate()
    {
        await AddAsync(local: new DateTime(2025, 6, 30, 23, 30, 0, DateTimeKind.Unspecified), distance: 7000); // last moment of To day
        await AddAsync(local: new DateTime(2025, 7, 1, 0, 30, 0, DateTimeKind.Unspecified), distance: 9000);   // just outside

        var summary = await _service.GetSummaryAsync(
            Filter(from: new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Unspecified), to: new DateTime(2025, 6, 30, 0, 0, 0, DateTimeKind.Unspecified)));

        Assert.Equal(1, summary.Overall.TotalActivities);
        Assert.Equal(7000, summary.Overall.TotalDistance);
    }

    [Fact]
    public async Task GetSummaryAsync_OwnerIdsFilter_RestrictsToThoseUsers()
    {
        await AddAsync(user: U1, distance: 10000);
        await AddAsync(user: U2, distance: 5000);

        var mineOnly = await _service.GetSummaryAsync(Filter(ownerIds: new[] { U1 }));
        Assert.Equal(10000, mineOnly.Overall.TotalDistance);

        var everyone = await _service.GetSummaryAsync(Filter());
        Assert.Equal(15000, everyone.Overall.TotalDistance);
    }

    [Fact]
    public async Task GetSummaryAsync_OwnerMatchNone_ReturnsZeroed()
    {
        await AddAsync(distance: 10000);

        var summary = await _service.GetSummaryAsync(Filter(matchNone: true));

        Assert.Empty(summary.ByType);
        Assert.Equal(0, summary.Overall.TotalActivities);
        Assert.Equal(0, summary.Overall.TotalDistance);
    }

    // ─── Cumulative (ported from DashboardService) ───

    [Fact]
    public async Task GetCumulativeByTypeAsync_BucketsByLocalDatePerType()
    {
        await AddAsync(type: "cycling", local: new DateTime(2025, 3, 2, 0, 30, 0, DateTimeKind.Unspecified), distance: 5000);
        await AddAsync(type: "running", local: new DateTime(2025, 3, 2, 8, 0, 0, DateTimeKind.Unspecified), distance: 3000);
        await AddAsync(type: "cycling", local: new DateTime(2025, 3, 3, 9, 0, 0, DateTimeKind.Unspecified), distance: 2000);

        var points = await _service.GetCumulativeByTypeAsync(Filter());

        Assert.Equal(3, points.Count);
        var mar2Cycling = points.Single(p => p.Date == "2025-03-02T00:00:00" && p.Type == "cycling");
        Assert.Equal(5.0, mar2Cycling.DistanceKm);
        var mar2Running = points.Single(p => p.Date == "2025-03-02T00:00:00" && p.Type == "running");
        Assert.Equal(3.0, mar2Running.DistanceKm);
    }

    [Fact]
    public async Task GetCumulativeByTypeAsync_ExactTypeFilter()
    {
        await AddAsync(type: "cycling", distance: 5000);
        await AddAsync(type: "running", distance: 3000);

        var points = await _service.GetCumulativeByTypeAsync(Filter(types: new[] { "cycling" }));

        Assert.Single(points);
        Assert.Equal("cycling", points[0].Type);
    }

    /// <summary>Shows a fixed set of user ids as visible so tests can exercise owner-id scoping.</summary>
    private sealed class MultiUserVisibility : IVisibilityService
    {
        private readonly string[] _ids;
        public MultiUserVisibility(params string[] ids) => _ids = ids;

        public string GetCurrentUserId() => _ids[0];
        public IQueryable<Activity> VisibleActivities(IQueryable<Activity> q) => q.Where(a => _ids.Contains(a.UserId));
        public Task<List<string>> GetVisibleUserIdsAsync() => Task.FromResult(_ids.ToList());

        public IQueryable<Segment> VisibleSegments(IQueryable<Segment> q) => throw new NotImplementedException();
        public IQueryable<MultiDayTour> VisibleTours(IQueryable<MultiDayTour> q) => throw new NotImplementedException();
        public IQueryable<Activity> OwnedActivities(IQueryable<Activity> q) => throw new NotImplementedException();
        public IQueryable<Segment> OwnedSegments(IQueryable<Segment> q) => throw new NotImplementedException();
        public IQueryable<MultiDayTour> OwnedTours(IQueryable<MultiDayTour> q) => throw new NotImplementedException();
        public IQueryable<AppCredentials> OwnedCredentials(IQueryable<AppCredentials> q) => throw new NotImplementedException();
        public Task<bool> CanViewItemAsync(string ownerUserId, string? visibility) => throw new NotImplementedException();
        public Task<bool> CanViewActivityAsync(TrackId trackId) => throw new NotImplementedException();
    }
}
