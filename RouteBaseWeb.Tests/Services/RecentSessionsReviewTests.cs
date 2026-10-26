using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using RouteBaseWeb.Services.Lifecycle;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Pins the merged Record-page read model (<see cref="RecordingService.GetRecentSessionsWithReviewAsync"/>):
/// pending auto rows surface even past the recent-N window, and non-pending rows carry IsPending=false.
/// Only the DbContext is exercised, so the import/staging/reader deps are unused here.
/// </summary>
public class RecentSessionsReviewTests : IAsyncLifetime
{
    private const string Me = "me";
    private RouteBaseDbContext _context = null!;
    private RecordingService _service = null!;

    public async Task InitializeAsync()
    {
        var conn = $"Data Source=file:recent_review_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();
        _context.Users.Add(new AppUser { UserId = Me, DisplayName = Me, Email = "me@t.com", IsAdmin = false });
        await _context.SaveChangesAsync();

        _service = new RecordingService(_context, null!, null!, null!, null!, null!, NullLogger<RecordingService>.Instance, TimeProvider.System);
    }

    public Task DisposeAsync() => _context.DisposeAsync().AsTask();

    private async Task SeedAsync(
        string sessionId, TrackId trackId, DateTime startedAt, string status,
        string source = "live-recording", string lifecycle = LifecycleStates.Released,
        string? reason = null, string type = "Run")
    {
        _context.Activities.Add(new Activity
        {
            TrackId = trackId, UserId = Me, ActivityType = type,
            Source = source, Lifecycle = lifecycle, LifecycleReason = reason, StartDateTimeUtc = startedAt,
        });
        _context.RecordingSessions.Add(new RecordingSession
        {
            SessionId = sessionId, TrackId = trackId, UserId = Me,
            Status = status, ActivityType = type, StartedAt = startedAt,
        });
        await _context.SaveChangesAsync();
    }

    [Fact]
    public async Task PendingRow_SurfacesEvenBeyondRecentWindow()
    {
        var oldPending = new DateTime(2025, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        await SeedAsync("pending-old", new TrackId(100), oldPending, "finalized",
            source: "auto-recording", lifecycle: LifecycleStates.InWork,
            reason: LifecycleReasons.AwaitingReview);

        // 25 newer confirmed sessions push the pending one well past a limit of 20.
        for (int i = 0; i < 25; i++)
            await SeedAsync($"recent-{i}", new TrackId(200 + i), oldPending.AddDays(1 + i), "finalized");

        var result = await _service.GetRecentSessionsWithReviewAsync(Me, limit: 20);

        var pending = Assert.Single(result, v => v.SessionId == "pending-old");
        Assert.True(pending.IsPending);
    }

    [Fact]
    public async Task NonPendingRows_CarryIsPendingFalse()
    {
        var t = new DateTime(2025, 2, 1, 8, 0, 0, DateTimeKind.Utc);
        await SeedAsync("live", new TrackId(1), t, "recording");
        await SeedAsync("auto-confirmed", new TrackId(2), t.AddMinutes(1), "finalized",
            source: "auto-recording", lifecycle: LifecycleStates.Released);

        var result = await _service.GetRecentSessionsWithReviewAsync(Me);

        Assert.All(result, v => Assert.False(v.IsPending));
    }

    [Fact]
    public async Task NoDuplicate_WhenPendingRowAlsoInRecentWindow()
    {
        var t = new DateTime(2025, 3, 1, 8, 0, 0, DateTimeKind.Utc);
        await SeedAsync("pending-recent", new TrackId(5), t, "finalized",
            source: "auto-recording", lifecycle: LifecycleStates.InWork,
            reason: LifecycleReasons.AwaitingReview);

        var result = await _service.GetRecentSessionsWithReviewAsync(Me, limit: 20);

        Assert.Single(result, v => v.SessionId == "pending-recent");
    }
}
