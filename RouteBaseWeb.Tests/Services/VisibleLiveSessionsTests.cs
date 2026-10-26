using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Lifecycle;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Pins GetActiveSessionsVisibleToAsync — the friends' live-picker source. Paused counts as active
/// (API-06): the hub lets viewers join and stay attached through a pause, so a paused friend must not
/// vanish from the picker mid-ride and silently "reappear" on resume. Private and terminal sessions
/// stay out.
/// </summary>
public class VisibleLiveSessionsTests : IAsyncLifetime
{
    private const string Friend = "friend-1";

    private RouteBaseDbContext _context = null!;
    private RecordingService _service = null!;

    public async Task InitializeAsync()
    {
        var conn = $"Data Source=file:visible_live_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();
        _context.Users.Add(new AppUser { UserId = Friend, DisplayName = "F", Email = "f@t.com", IsAdmin = false });
        await _context.SaveChangesAsync();
        _service = new RecordingService(
            _context, null!, null!, null!, null!, null!, NullLogger<RecordingService>.Instance, TimeProvider.System);
    }

    public Task DisposeAsync() => _context.DisposeAsync().AsTask();

    private async Task SeedSessionAsync(string id, TrackId trackId, string status, string visibility)
    {
        _context.Activities.Add(new Activity
        {
            TrackId = trackId, UserId = Friend, ActivityType = "Ride",
            Source = "recording", Lifecycle = LifecycleStates.Released,
        });
        _context.RecordingSessions.Add(new RecordingSession
        {
            SessionId = id, TrackId = trackId, UserId = Friend, Status = status,
            Visibility = visibility, ActivityType = "Ride", StartedAt = DateTime.UtcNow,
        });
        await _context.SaveChangesAsync();
    }

    [Fact]
    public async Task RecordingAndPausedFriendSessions_AreBothVisible()
    {
        await SeedSessionAsync("s-rec", new TrackId(1), "recording", "friends");

        var visible = await _service.GetActiveSessionsVisibleToAsync([Friend]);
        Assert.Equal(["s-rec"], visible.Select(s => s.SessionId), StringComparer.Ordinal);
    }

    [Fact]
    public async Task PausedFriendSession_StaysInThePicker()
    {
        await SeedSessionAsync("s-paused", new TrackId(2), "paused", "friends");

        var visible = await _service.GetActiveSessionsVisibleToAsync([Friend]);

        Assert.Equal(["s-paused"], visible.Select(s => s.SessionId), StringComparer.Ordinal);
    }

    [Fact]
    public async Task PrivateAndTerminalSessions_StayOut()
    {
        await SeedSessionAsync("s-private", new TrackId(3), "recording", "private");
        await SeedSessionAsync("s-done", new TrackId(4), "finalized", "friends");

        var visible = await _service.GetActiveSessionsVisibleToAsync([Friend]);

        Assert.Empty(visible);
    }
}
