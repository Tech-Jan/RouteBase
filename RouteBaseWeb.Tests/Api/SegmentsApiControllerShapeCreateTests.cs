using System.Text.Json;
using RouteBaseWeb.Api;
using RouteBaseWeb.Helpers.Grid;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Requests;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Segments.Rules;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// <see cref="SegmentsApiController.CreateShapeSegment"/>/<see cref="SegmentsApiController.UpdateShapeSegment"/>
/// had zero test coverage (test-quality audit finding #3): the polyline <c>CreateSegment</c> sibling's
/// <c>{analysisQueued}</c> anonymous-DTO contract is pinned (<c>SegmentsApiControllerQueueTests</c>), but
/// grep confirmed nothing ever called these two shape-segment actions at all — not the persistence, not the
/// rules-validation 400s, not the same queue-outcome reporting.
/// </summary>
public class SegmentsApiControllerShapeCreateTests : IAsyncLifetime
{
    private const string User = "u1";
    private RouteBaseDbContext _context = null!;

    public async Task InitializeAsync()
    {
        _context = new RouteBaseDbContext(new DbContextOptionsBuilder<RouteBaseDbContext>()
            .UseSqlite($"Data Source=file:segshapecreate_{Guid.NewGuid():N}?mode=memory&cache=shared").Options);
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
        var visibility = new TestVisibilityService(User);
        var crud = SegmentServices.Create(
            // Real settings double, not null!: SegmentCrud reads DefaultVisibility when creating.
            _context, cache, visibility, new UserCacheInvalidator(),
            new TestProfileSettingsService(), null!, null!, null!, null!).Crud;
        // Owner = null on every request below, so ResolveAsync never reaches IUserService — null! is safe.
        var ownerScope = new OwnerScopeService(new TestCurrentUserService(User), visibility, null!);
        return new SegmentsApiController(
            crud, null!, null!, null!, null!, queue, new TestCurrentUserService(User), null!, ownerScope,
            NullLogger<SegmentsApiController>.Instance, null!);
    }

    private static JsonElement ValidRules()
    {
        var doc = new RuleDocument
        {
            Shapes = { new ShapeDef { No = 1, Kind = "zone",
                Geom = new RectGeom { South = 46.9, West = 10.9, North = 47.1, East = 11.1 } } },
            Membership = { new PassesAllRule { Zones = { 1 } } },
        };
        return JsonDocument.Parse(doc.Serialize()).RootElement;
    }

    private static CreateShapeSegmentRequest ValidRequest(string name = "S") => new()
    {
        SegmentName = name, ActivityType = "Run", Rules = ValidRules(),
    };

    private static bool QueuedFlag(object dto) =>
        (bool)dto.GetType().GetProperty("AnalysisQueued")!.GetValue(dto)!;

    // ---- CreateShapeSegment ----

    [Fact]
    public async Task CreateShapeSegment_Valid_PersistsAsShapeKind_AndQueuesAnalysis()
    {
        var result = await NewController(new OperationQueue(TimeProvider.System)).CreateShapeSegment(ValidRequest("My Zone"));

        var created = Assert.IsType<ObjectResult>(result);   // StatusCode(201); GetSegment (the old CreatedAtAction target) was deleted ㉑
        Assert.Equal(201, created.StatusCode);
        Assert.True(QueuedFlag(created.Value!));

        var row = await _context.Segments.SingleAsync();
        Assert.Equal("My Zone", row.SegmentName);
        Assert.Equal("shapes", row.SegmentKind);
        Assert.Equal(User, row.UserId);
        Assert.Contains("\"kind\":\"zone\"", row.MatchRulesJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateShapeSegment_MissingName_Returns400_AndPersistsNothing()
    {
        var result = await NewController(new OperationQueue(TimeProvider.System)).CreateShapeSegment(ValidRequest(name: " "));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await _context.Segments.ToListAsync());
    }

    [Fact]
    public async Task CreateShapeSegment_MissingRules_Returns400WithTheParseError()
    {
        var request = new CreateShapeSegmentRequest { SegmentName = "S", Rules = null };

        var result = await NewController(new OperationQueue(TimeProvider.System)).CreateShapeSegment(request);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("missing or malformed", bad.Value?.ToString(), StringComparison.Ordinal);
        Assert.Empty(await _context.Segments.ToListAsync());
    }

    [Fact]
    public async Task CreateShapeSegment_QueueFull_StillPersists_ButQueuedFalse()
    {
        var queue = new OperationQueue(TimeProvider.System);
        for (int i = 0; i < 100; i++)
            queue.TryEnqueue(new OperationQueue.WorkItem
            {
                Type = OperationQueue.OperationType.SingleSegmentAnalysis, UserId = "filler",
            });

        var result = await NewController(queue).CreateShapeSegment(ValidRequest());

        var created = Assert.IsType<ObjectResult>(result);   // StatusCode(201); GetSegment (the old CreatedAtAction target) was deleted ㉑
        Assert.Equal(201, created.StatusCode);
        Assert.False(QueuedFlag(created.Value!));
        Assert.Equal(1, await _context.Segments.CountAsync());   // persisted regardless (SA-09)
    }

    // ---- UpdateShapeSegment ----

    private async Task<int> SeedShapeSegmentAsync(string name = "Original")
    {
        var doc = new RuleDocument
        {
            Shapes = { new ShapeDef { No = 1, Kind = "zone",
                Geom = new RectGeom { South = 40, West = 10, North = 41, East = 11 } } },
            Membership = { new PassesAllRule { Zones = { 1 } } },
        };
        var seg = new Segment { UserId = User, SegmentKind = "shapes", SegmentName = name, MatchRulesJson = doc.Serialize() };
        _context.Segments.Add(seg);
        await _context.SaveChangesAsync();
        return seg.SegmentId.Value;
    }

    [Fact]
    public async Task UpdateShapeSegment_Valid_OverwritesNameAndRulesInPlace_AndQueuesAnalysis()
    {
        var id = await SeedShapeSegmentAsync("Original");

        var result = await NewController(new OperationQueue(TimeProvider.System))
            .UpdateShapeSegment(id, ValidRequest("Renamed"));

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.True(QueuedFlag(ok.Value!));

        _context.ChangeTracker.Clear();
        var row = await _context.Segments.SingleAsync(s => s.SegmentId == new SegmentId(id));
        Assert.Equal("Renamed", row.SegmentName);
        Assert.Contains("46.9", row.MatchRulesJson, StringComparison.Ordinal);   // the NEW geometry, not the seeded 40..41 rect
        Assert.Equal(1, await _context.Segments.CountAsync());   // overwritten in place, not a second row
    }

    [Fact]
    public async Task UpdateShapeSegment_UnknownId_Returns404()
    {
        var result = await NewController(new OperationQueue(TimeProvider.System)).UpdateShapeSegment(999, ValidRequest());

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task UpdateShapeSegment_InvalidRules_Returns400_AndLeavesTheExistingRowUntouched()
    {
        var id = await SeedShapeSegmentAsync("Original");
        var request = new CreateShapeSegmentRequest { SegmentName = "Renamed", Rules = null };

        var result = await NewController(new OperationQueue(TimeProvider.System)).UpdateShapeSegment(id, request);

        Assert.IsType<BadRequestObjectResult>(result);
        _context.ChangeTracker.Clear();
        Assert.Equal("Original", (await _context.Segments.SingleAsync(s => s.SegmentId == new SegmentId(id))).SegmentName);
    }

    [Fact]
    public async Task UpdateShapeSegment_QueueFull_StillUpdates_ButQueuedFalse()
    {
        var id = await SeedShapeSegmentAsync();
        var queue = new OperationQueue(TimeProvider.System);
        for (int i = 0; i < 100; i++)
            queue.TryEnqueue(new OperationQueue.WorkItem
            {
                Type = OperationQueue.OperationType.SingleSegmentAnalysis, UserId = "filler",
            });

        var result = await NewController(queue).UpdateShapeSegment(id, ValidRequest("Renamed"));

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.False(QueuedFlag(ok.Value!));
        _context.ChangeTracker.Clear();
        Assert.Equal("Renamed", (await _context.Segments.SingleAsync(s => s.SegmentId == new SegmentId(id))).SegmentName);
    }
}
