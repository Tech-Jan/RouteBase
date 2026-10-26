using RouteBaseWeb.Api;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Tracks;
using RouteBaseWeb.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using RouteBaseWeb.Services.Lifecycle;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// Pins <see cref="ReviewApiController"/>: owner-scoped pending GPS (non-owner → 404), bulk approve
/// releases the ride, trash soft-rejects + abandons the session, restore returns it to the queue, and
/// empty batches no-op. Real SQLite + blob reader so the GPS path decodes an actual track.
/// </summary>
public class ReviewApiControllerTests : IAsyncLifetime
{
    private const string Me = "me";
    private const string Other = "other";

    private string _conn = null!;
    private RouteBaseDbContext _context = null!;
    private ServiceProvider _provider = null!;
    private ContentTrackReader _reader = null!;
    private UserCacheInvalidator _invalidator = null!;

    public async Task InitializeAsync()
    {
        _conn = $"Data Source=file:reviewapi_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(_conn).Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();

        var schema = new DbSchemaManager(_context);
        await schema.EnsureTrackContentTablesAsync();
        await schema.EnsureRecordingStagingTableAsync();
        await schema.EnsureActivityBboxTableAsync();

        foreach (var id in new[] { Me, Other })
            _context.Users.Add(new AppUser { UserId = id, DisplayName = id, Email = $"{id}@t.com", IsAdmin = false });
        await _context.SaveChangesAsync();

        _provider = new ServiceCollection().AddSingleton(TimeProvider.System)
            .AddDbContext<RouteBaseDbContext>(o => o.UseSqlite(_conn))
            .AddScoped<ITrackObjectStore, TrackObjectStore>()
            .AddScoped<TrackContentStore>()
            .AddScoped<RecordingStagingStore>()
            .BuildServiceProvider();
        _reader = new ContentTrackReader(
            new TrackReaderCache(64L * 1024 * 1024), new SingleFlight(),
            _provider.GetRequiredService<IServiceScopeFactory>());
        _invalidator = new UserCacheInvalidator();
    }

    public Task DisposeAsync()
    {
        _provider.Dispose();
        return _context.DisposeAsync().AsTask();
    }

    private ReviewApiController ControllerFor(string userId)
    {
        var vis       = new TestVisibilityService(userId);
        var lifecycle = TestLifecycle.For(_context, vis, _invalidator);
        var command   = new ActivityCommandService(
            _context, vis, _invalidator, NullLogger<ActivityCommandService>.Instance, lifecycle);
        var review = new AutoReviewService(
            _context, vis, lifecycle, command, new CapturingDeriveQueue(), new TestProfileSettingsService(),
            NullLogger<AutoReviewService>.Instance, TimeProvider.System);
        return new ReviewApiController(review, vis, _context, _reader, NullLogger<ReviewApiController>.Instance);
    }

    private async Task<int> AddPendingAsync(TrackId trackId, string owner)
    {
        _context.Activities.Add(new Activity
        {
            TrackId = trackId, UserId = owner, ActivityType = "Ride",
            Source = "auto-recording", Lifecycle = LifecycleStates.InWork,
            LifecycleReason = LifecycleReasons.AwaitingReview,
        });
        _context.RecordingSessions.Add(new RecordingSession
        {
            SessionId = $"s{trackId}", TrackId = trackId, UserId = owner,
            Status = "finalized", ActivityType = "Ride", StartedAt = DateTime.UtcNow.AddHours(-1),
            PointCount = 10,   // RF-05: restore requires a genuinely-recorded (PointCount>0) session
        });
        await _context.SaveChangesAsync();
        return trackId.Value;
    }

    private Task SeedGpsAsync(TrackId trackId) => TrackSeed.SeedTrackAsync(_context, trackId,
        new[]
        {
            (47.0, 11.0, new DateTime(2025, 1, 1, 8, 0, 0, DateTimeKind.Utc)),
            (47.001, 11.0, new DateTime(2025, 1, 1, 8, 0, 1, DateTimeKind.Utc)),
            (47.002, 11.0, new DateTime(2025, 1, 1, 8, 0, 2, DateTimeKind.Utc)),
        });

    [Fact]
    public async Task GetPendingGps_Owner_ReturnsPoints()
    {
        await AddPendingAsync(new TrackId(1), Me);
        await SeedGpsAsync(new TrackId(1));

        var result = await ControllerFor(Me).GetPendingGps(1, default);

        var ok = Assert.IsType<OkObjectResult>(result);
        var points = Assert.IsAssignableFrom<System.Collections.IEnumerable>(ok.Value);
        Assert.NotEmpty(points.Cast<object>());
    }

    [Fact]
    public async Task GetPendingGps_NonOwner_Returns404()
    {
        await AddPendingAsync(new TrackId(2), Me);
        await SeedGpsAsync(new TrackId(2));

        var result = await ControllerFor(Other).GetPendingGps(2, default);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Approve_ReleasesTheRide()
    {
        await AddPendingAsync(new TrackId(3), Me);

        var result = await ControllerFor(Me).Approve(new ApproveRequest(new[] { 3 }, null));

        Assert.IsType<OkObjectResult>(result);
        var state = await _context.Activities.AsNoTracking()
            .Where(a => a.TrackId == new TrackId(3)).Select(a => a.Lifecycle).FirstAsync();
        Assert.Equal(LifecycleStates.Released, state);
    }

    [Fact]
    public async Task Trash_SoftDiscardsAndAbandonsSession()
    {
        // Behavior changed intentionally (review finding B): "Delete" is now a soft trash — the row is
        // kept as 'discarded' and its session is routed into the 7-day abandoned-purge (restorable), not
        // hard-deleted on the spot.
        await AddPendingAsync(new TrackId(4), Me);
        await SeedGpsAsync(new TrackId(4));

        var result = await ControllerFor(Me).Trash(new TrashRequest(new[] { 4 }));

        Assert.IsType<OkObjectResult>(result);
        Assert.True(await _context.Activities.AsNoTracking().AnyAsync(a => a.TrackId == new TrackId(4))); // row kept
        Assert.Equal(LifecycleStates.Trashed, await _context.Activities.AsNoTracking()
            .Where(a => a.TrackId == new TrackId(4)).Select(a => a.Lifecycle).FirstAsync());
        Assert.Equal("abandoned", await _context.RecordingSessions.AsNoTracking()
            .Where(s => s.TrackId == new TrackId(4)).Select(s => s.Status).FirstAsync());
    }

    [Fact]
    public async Task Restore_ReturnsTrashedToPending()
    {
        await AddPendingAsync(new TrackId(6), Me);
        await ControllerFor(Me).Trash(new TrashRequest(new[] { 6 }));

        var result = await ControllerFor(Me).Restore(new RestoreRequest(new[] { 6 }));

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(LifecycleStates.InWork, await _context.Activities.AsNoTracking()
            .Where(a => a.TrackId == new TrackId(6)).Select(a => a.Lifecycle).FirstAsync());
        // Session cleared out of the purge window so it isn't reclaimed after restore.
        Assert.Equal("finalized", await _context.RecordingSessions.AsNoTracking()
            .Where(s => s.TrackId == new TrackId(6)).Select(s => s.Status).FirstAsync());
    }

    [Fact]
    public async Task Approve_EmptyArray_NoOp()
    {
        var result = await ControllerFor(Me).Approve(new ApproveRequest(Array.Empty<int>(), null));
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Trash_EmptyArray_NoOp()
    {
        var result = await ControllerFor(Me).Trash(new TrashRequest(Array.Empty<int>()));
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Approve_TooManyIds_Returns400()
    {
        var ids = Enumerable.Range(1, 5001).ToArray();
        var result = await ControllerFor(Me).Approve(new ApproveRequest(ids, null));
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Approve_OverlongActivityType_Returns400()
    {
        var result = await ControllerFor(Me).Approve(new ApproveRequest(new[] { 1 }, new string('x', 41)));
        Assert.IsType<BadRequestObjectResult>(result);
    }
}
