using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Pins <see cref="RecordingSessionCleanupService.RetryStuckFinalizationsAsync"/> (RF-02): a failed retry
/// parks the session at the terminal 'finalize_failed' status (surfaced in the /Record list for manual
/// Retry/Discard) — never 'abandoned' (which routes a real recording into the 7-day hard purge) and never
/// left 'stopped' (which would retry a permanently-broken finalize forever).
/// </summary>
public class RecordingSessionCleanupRetryTests : IAsyncLifetime
{
    private const string Me = "me";
    private string _connStr = null!;
    private RouteBaseDbContext _context = null!;

    public async Task InitializeAsync()
    {
        _connStr = $"Data Source=file:retry_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(_connStr).Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();

        _context.Users.Add(new AppUser { UserId = Me, DisplayName = Me, Email = "me@t.com", IsAdmin = false });
        await _context.SaveChangesAsync();
    }

    public Task DisposeAsync() => _context.DisposeAsync().AsTask();

    /// <summary>Fixed fake now (K2 seam): the stuck-threshold cutoff runs against this, not the wall clock.</summary>
    private static readonly DateTime T0 = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    private async Task SeedStuckStoppedAsync(string sessionId)
    {
        _context.Activities.Add(new Activity
        {
            TrackId = new TrackId(1), UserId = Me, ActivityType = "Run", Source = "live-recording",
        });
        _context.RecordingSessions.Add(new RecordingSession
        {
            SessionId = sessionId, TrackId = new TrackId(1), UserId = Me, Status = "stopped",
            StartedAt = T0.AddHours(-3), StoppedAt = T0.AddHours(-2),
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    // Coordinator whose scope resolves the given finalization stub.
    private static RecordingFinalizationCoordinator CoordinatorOver(IRecordingFinalizationService svc)
    {
        var provider = new ServiceCollection().AddSingleton(TimeProvider.System)
            .AddScoped(_ => svc)
            .BuildServiceProvider();
        return new RecordingFinalizationCoordinator(
            new SingleFlight(), provider.GetRequiredService<IServiceScopeFactory>());
    }

    [Fact]
    public async Task RetryStuck_FinalizeThrows_SessionParkedFinalizeFailed()
    {
        await SeedStuckStoppedAsync("s1");
        var coordinator = CoordinatorOver(new ThrowingFinalizationService());

        await RecordingSessionCleanupService.RetryStuckFinalizationsAsync(
            _context, coordinator, NullLogger.Instance, T0, default);

        var persisted = await _context.RecordingSessions.AsNoTracking().FirstAsync(s => s.SessionId == "s1");
        Assert.Equal("finalize_failed", persisted.Status); // NOT abandoned (no purge) and NOT retried forever
    }

    [Fact]
    public async Task RetryStuck_FinalizeSucceeds_SessionFinalized()
    {
        await SeedStuckStoppedAsync("s2");
        var coordinator = CoordinatorOver(new FinalizingStub(_connStr));

        await RecordingSessionCleanupService.RetryStuckFinalizationsAsync(
            _context, coordinator, NullLogger.Instance, T0, default);

        var persisted = await _context.RecordingSessions.AsNoTracking().FirstAsync(s => s.SessionId == "s2");
        Assert.Equal("finalized", persisted.Status);
    }

    private sealed class ThrowingFinalizationService : IRecordingFinalizationService
    {
        public Task FinalizeAsync(string sessionId) =>
            throw new InvalidOperationException("finalize failed (injected)");
        public Task<ElevationRefreshResult?> RefreshElevationAsync(TrackId trackId) => throw new NotSupportedException();
    }

    // Success stub: flips the stuck session to 'finalized' over its own context on the shared-cache DB.
    private sealed class FinalizingStub(string connStr) : IRecordingFinalizationService
    {
        public async Task FinalizeAsync(string sessionId)
        {
            var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(connStr).Options;
            await using var ctx = new RouteBaseDbContext(options);
            await ctx.Database.ExecuteSqlRawAsync(
                "UPDATE RecordingSession SET status = 'finalized' WHERE session_id = {0}", sessionId);
        }
        public Task<ElevationRefreshResult?> RefreshElevationAsync(TrackId trackId) => throw new NotSupportedException();
    }
}
