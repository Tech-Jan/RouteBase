using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Heatmaps;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Heatmaps;

/// <summary>
/// B2: <see cref="HeatmapLifecycle"/> owns the Heatmap row's attach/fail/get-visible writes (moved
/// verbatim from SegmentCrud). SH-02: FailPendingAsync demotes only a still-pending row so a queue-full
/// attach can't wedge it in pending; attach 409s an in-flight render; the activity-type CSV is sanitized.
/// Real in-memory SQLite (matches the pipeline test harness).
/// </summary>
public class HeatmapLifecycleTests : IAsyncLifetime
{
    private RouteBaseDbContext _context = null!;
    private IHeatmapLifecycle _service = null!;

    public async Task InitializeAsync()
    {
        var connectionString = $"Data Source=file:testdb_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(connectionString).Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();

        _context.Users.Add(new AppUser
        {
            UserId      = "test-user",
            DisplayName = "Test User",
            Email       = "test@test.com",
            IsAdmin     = false,
            CreatedAt   = DateTime.UtcNow.ToString("O"),
            LastLogin   = DateTime.UtcNow.ToString("O"),
        });
        await _context.SaveChangesAsync();

        var tileStore = new HeatmapTilePackStore(
            new HeatmapStorageOptions { Root = Path.Combine(Path.GetTempPath(), $"hmlife_{Guid.NewGuid():N}") },
            new HeatmapTileIndexCache(new MemoryCache(new MemoryCacheOptions())), TimeProvider.System);
        _service = new HeatmapLifecycle(
            _context, new TestVisibilityService(), tileStore, NullLogger<HeatmapLifecycle>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _context.Database.EnsureDeletedAsync();
        await _context.DisposeAsync();
    }

    private async Task<SegmentId> SeedHeatmapAsync(string status)
    {
        var seg = new Segment { UserId = "test-user", SegmentKind = "shapes", SegmentDistance = 100 };
        _context.Segments.Add(seg);
        await _context.SaveChangesAsync();
        _context.Heatmaps.Add(new Heatmap { SegmentId = seg.SegmentId, Status = status });
        await _context.SaveChangesAsync();
        return seg.SegmentId;
    }

    [Fact]
    public async Task FailPendingAsync_PendingRow_SetsFailedWithError()
    {
        var segmentId = await SeedHeatmapAsync("pending");

        await _service.FailPendingAsync(segmentId, "Operation queue is full — try again later.");

        var hm = await _context.Heatmaps.AsNoTracking().FirstAsync(h => h.SegmentId == segmentId);
        Assert.Equal("failed", hm.Status);
        Assert.Equal("Operation queue is full — try again later.", hm.Error);
    }

    [Fact]
    public async Task FailPendingAsync_RunningRow_LeavesStatusUntouched()
    {
        var segmentId = await SeedHeatmapAsync("running");

        await _service.FailPendingAsync(segmentId, "should be ignored");

        var hm = await _context.Heatmaps.AsNoTracking().FirstAsync(h => h.SegmentId == segmentId);
        Assert.Equal("running", hm.Status);
        Assert.Null(hm.Error);
    }

    [Fact]
    public async Task AttachAsync_PendingRow_ReturnsConflict()
    {
        // HM: an in-flight render must not be disturbed — attach returns 409 while pending/running.
        var segmentId = await SeedHeatmapAsync("pending");

        var result = await _service.AttachAsync(segmentId, []);

        Assert.Equal(HeatmapAttachResult.Conflict, result);
    }

    [Fact]
    public async Task AttachAsync_RunningRow_ReturnsConflict()
    {
        var segmentId = await SeedHeatmapAsync("running");

        var result = await _service.AttachAsync(segmentId, []);

        Assert.Equal(HeatmapAttachResult.Conflict, result);
    }

    [Fact]
    public async Task AttachAsync_SanitizesActivityTypes()
    {
        // HM-15: comma-containing entries can't round-trip the CSV column; whitespace trimmed,
        // over-long entries dropped, duplicates collapsed.
        var seg = new Segment { UserId = "test-user", SegmentKind = "shapes", SegmentDistance = 100 };
        _context.Segments.Add(seg);
        await _context.SaveChangesAsync();

        var result = await _service.AttachAsync(seg.SegmentId,
            new[] { "Run,Ride", "  Walk ", new string('x', 60), "Run", "Run" });

        Assert.Equal(HeatmapAttachResult.Attached, result);
        var hm = await _context.Heatmaps.AsNoTracking().FirstAsync(h => h.SegmentId == seg.SegmentId);
        Assert.Equal("Walk,Run", hm.ActivityTypes);
    }

    [Theory]
    [InlineData("done")]
    [InlineData("stale")]
    public async Task WithdrawAsync_ServableRow_GoesOfflineWithCuratedReason(string status)
    {
        // SEC-01: stale tiles are still served, so a widened audience needs failed — the state that is not.
        var segmentId = await SeedHeatmapAsync(status);

        await _service.WithdrawAsync(segmentId);

        var hm = await _context.Heatmaps.AsNoTracking().FirstAsync(h => h.SegmentId == segmentId);
        Assert.Equal("failed", hm.Status);
        Assert.Equal(HeatmapPaintScope.WidenedMessage, hm.Error);
    }

    [Fact]
    public async Task WithdrawAsync_RunningRow_LeavesStatusUntouched()
    {
        // The render re-checks the audience before it commits; flipping it here would only race that.
        var segmentId = await SeedHeatmapAsync("running");

        await _service.WithdrawAsync(segmentId);

        var hm = await _context.Heatmaps.AsNoTracking().FirstAsync(h => h.SegmentId == segmentId);
        Assert.Equal("running", hm.Status);
    }
}
