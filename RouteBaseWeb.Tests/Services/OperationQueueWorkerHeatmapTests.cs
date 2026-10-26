using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Heatmaps;
using RouteBaseWeb.Services.Queue;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// HM-10: a heatmap job cancelled while still queued must demote its `pending` Heatmap row to
/// `failed` — Attach flips the row to pending BEFORE enqueueing, so silently skipping the item
/// would wedge the heatmap in pending forever (every future Attach 409s on the pending guard).
/// </summary>
public sealed class OperationQueueWorkerHeatmapTests : IAsyncLifetime
{
    private RouteBaseDbContext _context = null!;
    private DbContextOptions<RouteBaseDbContext> _options = null!;

    public async Task InitializeAsync()
    {
        var connectionString = $"Data Source=file:hmworker_{Guid.NewGuid():N}?mode=memory&cache=shared";
        _options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(connectionString).Options;
        _context = new RouteBaseDbContext(_options);
        await _context.Database.EnsureCreatedAsync();

        _context.Users.Add(new AppUser
        {
            UserId = "test-user", DisplayName = "Test User", Email = "t@t.t", IsAdmin = false,
            CreatedAt = DateTime.UtcNow.ToString("O"), LastLogin = DateTime.UtcNow.ToString("O"),
        });
        await _context.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.Database.EnsureDeletedAsync();
        await _context.DisposeAsync();
    }

    private ServiceProvider BuildProvider()
    {
        // Minimal DI graph: the worker's cancelled-while-queued arm resolves only IHeatmapLifecycle (B2).
        var services = new ServiceCollection().AddSingleton(TimeProvider.System);
        services.AddScoped(_ => new RouteBaseDbContext(_options));
        services.AddScoped<IHeatmapLifecycle>(sp =>
        {
            var context = sp.GetRequiredService<RouteBaseDbContext>();
            var tileStore = new HeatmapTilePackStore(
                new HeatmapStorageOptions { Root = Path.Combine(Path.GetTempPath(), $"hmwrk_{Guid.NewGuid():N}") },
                new HeatmapTileIndexCache(new MemoryCache(new MemoryCacheOptions())), TimeProvider.System);
            return new HeatmapLifecycle(
                context, new TestVisibilityService(), tileStore, NullLogger<HeatmapLifecycle>.Instance);
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task CancelledWhileQueued_HeatmapRowDemotedToFailed()
    {
        var seg = new Segment { UserId = "test-user", SegmentKind = "shapes", SegmentDistance = 100 };
        _context.Segments.Add(seg);
        await _context.SaveChangesAsync();
        _context.Heatmaps.Add(new Heatmap { SegmentId = seg.SegmentId, Status = "pending" });
        await _context.SaveChangesAsync();

        await using var provider = BuildProvider();
        var queue = new OperationQueue(TimeProvider.System);
        Assert.True(queue.TryEnqueue(new OperationQueue.WorkItem
        {
            Type = OperationQueue.OperationType.HeatmapGeneration,
            UserId = "test-user",
            Payload = new HeatmapGenerationPayload("test-user", seg.SegmentId.Value),
        }));
        queue.CancelForUser("test-user"); // cancelled while still queued — worker must clean up

        var heatmapProgress = new RouteBaseWeb.Services.Heatmaps.HeatmapProgress(TimeProvider.System);
        var worker = new OperationQueueWorker(
            queue,
            new IOperationHandler[]
            {
                new HeatmapGenerationHandler(provider.GetRequiredService<IServiceScopeFactory>(),
                    heatmapProgress, NullLogger<HeatmapGenerationHandler>.Instance),
            },
            NullLogger<OperationQueueWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            string status = "pending";
            while (DateTime.UtcNow < deadline)
            {
                status = await _context.Heatmaps.AsNoTracking()
                    .Where(h => h.SegmentId == seg.SegmentId)
                    .Select(h => h.Status)
                    .FirstAsync();
                if (status != "pending") break;
                await Task.Delay(50);
            }

            Assert.Equal("failed", status);
            var error = await _context.Heatmaps.AsNoTracking()
                .Where(h => h.SegmentId == seg.SegmentId)
                .Select(h => h.Error)
                .FirstAsync();
            Assert.Equal("Cancelled by user", error);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }
}
