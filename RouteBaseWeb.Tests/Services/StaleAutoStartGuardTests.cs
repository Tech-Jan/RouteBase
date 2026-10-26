using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Requests;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Tracks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using RouteBaseWeb.Services.Lifecycle;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Pins the stale-auto-start guard (GpsRecorderGapFixes WS1): a start that finds an ACTIVE auto
/// session with no appends for 30+ min stops it and proceeds, instead of 409ing — the 409 made the
/// phone reconnect INTO the dead session and stitch an hours-long hole into one activity. Fresh
/// sessions and manual recordings keep the single-active-session conflict contract unchanged.
/// Finalization is unresolvable here by design (empty provider): the guard must treat a finalize
/// failure as non-fatal and leave the session 'stopped' for the cleanup sweep (RF-02).
/// </summary>
public class StaleAutoStartGuardTests : IAsyncLifetime
{
    private const string Me = "me";
    private RouteBaseDbContext _context = null!;
    private ServiceProvider _provider = null!;
    private RecordingService _service = null!;

    public async Task InitializeAsync()
    {
        var conn = $"Data Source=file:stale_start_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();
        _context.Users.Add(new AppUser { UserId = Me, DisplayName = Me, Email = "me@t.com", IsAdmin = false });
        await _context.SaveChangesAsync();

        // Deliberately empty: FinalizeAsync must fail non-fatally inside the guard.
        _provider = new ServiceCollection().AddSingleton(TimeProvider.System).BuildServiceProvider();
        _service = new RecordingService(
            _context, null!, null!, null!, null!,
            new RecordingFinalizationCoordinator(new SingleFlight(), _provider.GetRequiredService<IServiceScopeFactory>()),
            NullLogger<RecordingService>.Instance, TimeProvider.System);
    }

    public Task DisposeAsync()
    {
        _provider.Dispose();
        return _context.DisposeAsync().AsTask();
    }

    private async Task SeedActiveAsync(string sessionId, TrackId trackId, string source, DateTime? lastPointAt,
        DateTime? startedAt = null)
    {
        _context.Activities.Add(new Activity
        {
            TrackId = trackId, UserId = Me, ActivityType = "Ride",
            Source = source,
            Lifecycle = source == "auto-recording" ? LifecycleStates.InWork : LifecycleStates.Released,
            LifecycleReason = source == "auto-recording" ? LifecycleReasons.AwaitingReview : null,
        });
        _context.RecordingSessions.Add(new RecordingSession
        {
            SessionId = sessionId, TrackId = trackId, UserId = Me, Status = "recording",
            ActivityType = "Ride", StartedAt = startedAt ?? DateTime.UtcNow.AddHours(-3),
            LastPointAt = lastPointAt,
        });
        await _context.SaveChangesAsync();
    }

    private static StartRecordingRequest Request() => new()
    {
        ActivityType = "Ride", TrackName = "Auto ride", Visibility = "private",
        ClientVersion = "test-1.4.0", AutoDetected = true,
    };

    [Fact]
    public async Task StaleAutoSession_IsStoppedAndNewStartProceeds()
    {
        // The 2026-07-11 case: last append 2 h ago, phone starts fresh after its state was wiped.
        await SeedActiveAsync("stale-auto", new TrackId(100), "auto-recording", DateTime.UtcNow.AddHours(-2));

        var created = await _service.StartSessionAsync(Request(), Me);

        Assert.NotNull(created);
        Assert.NotEqual("stale-auto", created!.SessionId, StringComparer.Ordinal);
        var old = await _context.RecordingSessions.FindAsync("stale-auto");
        Assert.Equal("stopped", old!.Status);   // finalize failed (by design) → sweep retries within ~1 h
        Assert.NotNull(old.StoppedAt);
    }

    [Fact]
    public async Task FreshAutoSession_StillConflicts()
    {
        await SeedActiveAsync("fresh-auto", new TrackId(101), "auto-recording", DateTime.UtcNow.AddMinutes(-5));

        var created = await _service.StartSessionAsync(Request(), Me);

        Assert.Null(created);   // 409 path: the phone should reconnect, not fork the session
        Assert.Equal("recording", (await _context.RecordingSessions.FindAsync("fresh-auto"))!.Status);
    }

    [Fact]
    public async Task StaleManualSession_StillConflicts()
    {
        await SeedActiveAsync("stale-manual", new TrackId(102), "live-recording", DateTime.UtcNow.AddHours(-3));

        var created = await _service.StartSessionAsync(Request(), Me);

        Assert.Null(created);   // manual recordings are never rescued away from under the user
        Assert.Equal("recording", (await _context.RecordingSessions.FindAsync("stale-manual"))!.Status);
    }

    [Fact]
    public async Task StaleAutoSessionWithoutPoints_FallsBackToStartedAt()
    {
        // 0-point session (fix never acquired): LastPointAt null → staleness judged from StartedAt.
        await SeedActiveAsync("stale-empty", new TrackId(103), "auto-recording", lastPointAt: null,
            startedAt: DateTime.UtcNow.AddHours(-2));

        var created = await _service.StartSessionAsync(Request(), Me);

        Assert.NotNull(created);
        Assert.Equal("stopped", (await _context.RecordingSessions.FindAsync("stale-empty"))!.Status);
    }
}
