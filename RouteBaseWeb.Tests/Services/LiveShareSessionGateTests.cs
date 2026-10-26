using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Sync;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Lifecycle;
using RouteBaseWeb.Services.Tracks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Pins the live-share READ gate (GetLiveShareSessionAsync) — the invariant ShareLinkService.MintAsync
/// documents: a live link CAN be minted before release, but serves nothing unless the Activity shell is
/// RELEASED and both shell and session belong to the grant's owner. Manual recordings are Released from
/// the start (live viewing works mid-ride); trashing the ride must cut the link off; an auto recording
/// awaiting review serves nothing. Also pins the finalized read: an INCREMENTAL poll gets the frozen final
/// track too — the old `[]` permanently lost the ride's finish for any watcher whose stop+finalize landed
/// inside one poll gap.
/// </summary>
public class LiveShareSessionGateTests : IAsyncLifetime
{
    private const string Owner = "owner";
    private const int TrackId = 500;

    private RouteBaseDbContext _context = null!;

    public async Task InitializeAsync()
    {
        var conn = $"Data Source=file:live_share_gate_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();
        _context.Users.Add(new AppUser { UserId = Owner, DisplayName = Owner, Email = "o@t.com", IsAdmin = false });
        _context.Users.Add(new AppUser { UserId = "somebody-else", DisplayName = "x", Email = "x@t.com", IsAdmin = false });
        await _context.SaveChangesAsync();
    }

    public Task DisposeAsync() => _context.DisposeAsync().AsTask();

    private RecordingService Service(ITrackReader? reader = null) => new(
        _context, null!, null!, reader!, null!, null!, NullLogger<RecordingService>.Instance,
        TimeProvider.System);

    private async Task SeedAsync(string lifecycle, string sessionStatus,
        string activityOwner = Owner, string sessionOwner = Owner)
    {
        _context.Activities.Add(new Activity
        {
            TrackId = new TrackId(TrackId), UserId = activityOwner, ActivityType = "Ride",
            Source = "recording", Lifecycle = lifecycle,
        });
        _context.RecordingSessions.Add(new RecordingSession
        {
            SessionId = "sess-live", TrackId = new TrackId(TrackId), UserId = sessionOwner, Status = sessionStatus,
            ActivityType = "Ride", StartedAt = DateTime.UtcNow.AddMinutes(-30),
        });
        await _context.SaveChangesAsync();
    }

    [Fact]
    public async Task ReleasedShell_ServesTheSession_MidRide()
    {
        await SeedAsync(LifecycleStates.Released, "recording");

        var session = await Service().GetLiveShareSessionAsync(new TrackId(TrackId), Owner, CancellationToken.None);

        Assert.NotNull(session);
        Assert.Equal("sess-live", session!.SessionId);
    }

    [Fact]
    public async Task TrashedShell_ServesNothing()
    {
        // The activity-kind share 404s the moment the ride is trashed (VisibleActivities); the live kind
        // must not be the one surface that keeps serving the track until token expiry.
        await SeedAsync(LifecycleStates.Trashed, "finalized");

        Assert.Null(await Service().GetLiveShareSessionAsync(new TrackId(TrackId), Owner, CancellationToken.None));
    }

    [Fact]
    public async Task AwaitingReviewAutoShell_ServesNothing()
    {
        // "Serves nothing until the activity is Released" — an auto-detected ride nobody approved yet.
        await SeedAsync(LifecycleStates.InWork, "recording");

        Assert.Null(await Service().GetLiveShareSessionAsync(new TrackId(TrackId), Owner, CancellationToken.None));
    }

    [Fact]
    public async Task SessionOwnedByAnotherUser_ServesNothing()
    {
        await SeedAsync(LifecycleStates.Released, "recording", sessionOwner: "somebody-else");

        Assert.Null(await Service().GetLiveShareSessionAsync(new TrackId(TrackId), Owner, CancellationToken.None));
    }

    [Fact]
    public async Task ShellOwnedByAnotherUser_ServesNothing()
    {
        await SeedAsync(LifecycleStates.Released, "recording", activityOwner: "somebody-else",
            sessionOwner: "somebody-else");

        Assert.Null(await Service().GetLiveShareSessionAsync(new TrackId(TrackId), Owner, CancellationToken.None));
    }

    [Fact]
    public async Task FinalizedSession_IncrementalPoll_StillServesTheFrozenFinalTrack()
    {
        // The API-04 pin: stop+finalize inside one poll gap. The viewer's next poll is incremental
        // (afterSeq >= 0) against a now-finalized session — it must get the frozen final track, not [].
        await SeedAsync(LifecycleStates.Released, "finalized");
        var session = _context.RecordingSessions.Single();
        session.StoppedAt = new DateTime(2026, 7, 22, 10, 0, 0, DateTimeKind.Utc);
        var blob = DecodedTrackExtensions.FromExternalPoints(
        [
            new ExternalGpsPoint { Lat = 47.0, Lng = 8.0, Seq = 0, TimestampUtc = DateTime.UtcNow },
            new ExternalGpsPoint { Lat = 47.1, Lng = 8.1, Seq = 1, TimestampUtc = DateTime.UtcNow },
            new ExternalGpsPoint { Lat = 47.2, Lng = 8.2, Seq = 2, TimestampUtc = DateTime.UtcNow },
        ]);

        var points = await Service(new FakeTrackReader(blob)).GetLivePointsAsync(session, afterSeq: 950);

        Assert.Equal(3, points.Count);
        Assert.Equal(new long?[] { 0, 1, 2 }, points.Select(p => p.Seq)); // synthetic blob indexes, not staging seqs
        Assert.All(points, p => Assert.Equal(session.StoppedAt, p.TimestampUtc));
    }

    private sealed class FakeTrackReader(DecodedTrack? track) : ITrackReader
    {
        public Task<DecodedTrack?> GetTrackAsync(TrackId trackId, CancellationToken ct = default)
            => Task.FromResult(track);
    }
}
