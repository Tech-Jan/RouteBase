using RouteBaseWeb.Api;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Requests;
using RouteBaseWeb.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// SA-09 pin: CreateSegment persists the segment even when the analysis queue is full, and reports the
/// outcome via an additive <c>analysisQueued</c> response field (client can then prompt Re-analyze) —
/// previously a full queue silently produced a segment with no efforts and no signal.
/// </summary>
public class SegmentsApiControllerQueueTests : IAsyncLifetime
{
    private const string User = "u1";
    private RouteBaseDbContext _context = null!;

    public async Task InitializeAsync()
    {
        _context = new RouteBaseDbContext(new DbContextOptionsBuilder<RouteBaseDbContext>()
            .UseSqlite($"Data Source=file:segqueue_{Guid.NewGuid():N}?mode=memory&cache=shared").Options);
        await _context.Database.EnsureCreatedAsync();
        _context.Users.Add(new AppUser
        {
            UserId = User, DisplayName = User, Email = "u@t.com", IsAdmin = false,
            CreatedAt = DateTime.UtcNow.ToString("O"), LastLogin = DateTime.UtcNow.ToString("O"),
        });
        await _context.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.Database.EnsureDeletedAsync();
        await _context.DisposeAsync();
    }

    private SegmentsApiController NewController(OperationQueue queue)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var crud = SegmentServices.Create(
            _context, cache, new TestVisibilityService(User), new UserCacheInvalidator(),
            // Real settings double, not null!: SegmentCrud reads DefaultVisibility when creating.
            new TestProfileSettingsService(), null!, null!, null!, null!).Crud;
        return new SegmentsApiController(
            crud, null!, null!, null!, null!, queue, new TestCurrentUserService(User), null!, null!,
            NullLogger<SegmentsApiController>.Instance, null!);
    }

    private static CreateSegmentRequest ValidRequest() => new()
    {
        SegmentName = "S", ActivityType = "Run",
        GpsPoints = new List<SegmentGpsPoint>
        {
            new() { Lat = 47.000, Lng = 11.000 },
            new() { Lat = 47.001, Lng = 11.000 },
        },
    };

    private static bool QueuedFlag(IActionResult result)
    {
        // Both create endpoints now return StatusCode(201, <named DTO>) — CreatedSegmentResponse or
        // ShapeSegmentResponse — so read AnalysisQueued reflectively to cover either shape.
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(201, objectResult.StatusCode);
        var value = objectResult.Value!;
        return (bool)value.GetType().GetProperty("AnalysisQueued")!.GetValue(value)!;
    }

    [Fact]
    public async Task CreateSegment_QueueFull_Returns201WithQueuedFalse()
    {
        var queue = new OperationQueue(TimeProvider.System);
        // Fill the bounded (capacity 100) channel so the controller's enqueue fails.
        for (int i = 0; i < 100; i++)
            queue.TryEnqueue(new OperationQueue.WorkItem
            {
                Type = OperationQueue.OperationType.SingleSegmentAnalysis, UserId = "filler"
            });

        var result = await NewController(queue).CreateSegment(ValidRequest());

        Assert.False(QueuedFlag(result));
        Assert.Equal(1, await _context.Segments.CountAsync());   // still persisted
    }

    [Fact]
    public async Task CreateSegment_QueueHasRoom_Returns201WithQueuedTrue()
    {
        var result = await NewController(new OperationQueue(TimeProvider.System)).CreateSegment(ValidRequest());

        Assert.True(QueuedFlag(result));
    }
}
