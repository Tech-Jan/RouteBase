using RouteBaseWeb.Api;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Recording;
using RouteBaseWeb.Models.Requests;
using RouteBaseWeb.Models.Sync;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Sharing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// The anonymous live-recording surface's authority gate. Only a LIVE grant may read it; it re-resolves the
/// session from the grant's track id (never a route param); and every poll registers a public viewer so the
/// phone keeps its fast upload cadence. A non-live token (activity/segment) must read nothing.
/// </summary>
public class ShareRecordingReadControllerTests
{
    private const int TrackId = 55;

    private static ShareGrant LiveGrant() =>
        new(TokenId: 1, ShareKind.LiveRecording, TrackId, "owner", DateTime.UtcNow.AddHours(1));

    private static ShareRecordingReadController Controller(
        IShareLinkAccess access, IRecordingService recording, ILiveViewerRegistry viewers) =>
        new(access, recording, viewers)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

    [Fact]
    public async Task Live_WithALiveGrant_ReturnsStatusAndPoints_AndTouchesWatchedBySessionId()
    {
        var session = MakeSession("recording", pointCount: 2);
        var recording = new FakeRecording(session, [Point(0), Point(1)]);
        var viewers = new SpyViewers();

        var result = await Controller(new FakeAccess(LiveGrant()), recording, viewers)
            .Live(after: -1, CancellationToken.None);

        var body = Assert.IsType<ShareLiveUpdateDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("recording", body.Status);
        Assert.Equal(2, body.PointCount);
        Assert.Equal(2, body.Points.Count);
        Assert.Equal(session.SessionId, viewers.Touched);   // registered interest by SESSION id, not track id
    }

    [Fact]
    public async Task Live_WithANonLiveGrant_Is404_AndNeverReadsOrTouches()
    {
        // An activity token must not read the recording surface, even for the same track id — the same
        // kind-confusion property the WidenAsync seam pins for the page.
        var recording = new FakeRecording(MakeSession("recording"), []);
        var viewers = new SpyViewers();
        var activityGrant = new ShareGrant(1, ShareKind.Activity, TrackId, "owner", DateTime.UtcNow.AddHours(1));

        var result = await Controller(new FakeAccess(activityGrant), recording, viewers)
            .Live(after: -1, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.False(recording.SessionQueried);
        Assert.Null(viewers.Touched);
    }

    [Fact]
    public async Task Live_PinsTheGrantsOwner_OnTheSessionResolve()
    {
        // The resolve must carry the grant's owner (not just the track id) — the lifecycle+owner gate in
        // GetLiveShareSessionAsync is keyed on both, so a re-keyed session can never ride an old grant.
        var recording = new FakeRecording(MakeSession("recording"), []);

        await Controller(new FakeAccess(LiveGrant()), recording, new SpyViewers())
            .Live(after: -1, CancellationToken.None);

        Assert.Equal("owner", recording.LiveShareOwnerAsked);
    }

    [Fact]
    public async Task Live_TerminalSession_ServesTheBody_ButNeverTouchesTheViewerRegistry()
    {
        // A finalized ride has no upload cadence to keep fast — stamping it would only grow the registry's
        // public-stamp map (API-05). The frozen body itself still serves.
        var recording = new FakeRecording(MakeSession("finalized", pointCount: 3), [Point(0)]);
        var viewers = new SpyViewers();

        var result = await Controller(new FakeAccess(LiveGrant()), recording, viewers)
            .Live(after: -1, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Null(viewers.Touched);
    }

    [Fact]
    public async Task Live_PausedSession_StillTouchesTheViewerRegistry()
    {
        // Paused is not terminal: the phone still uploads (heartbeats) and must keep the fast cadence for
        // the watcher who is waiting out the pause.
        var viewers = new SpyViewers();
        var session = MakeSession("paused");

        await Controller(new FakeAccess(LiveGrant()), new FakeRecording(session, []), viewers)
            .Live(after: -1, CancellationToken.None);

        Assert.Equal(session.SessionId, viewers.Touched);
    }

    [Fact]
    public async Task Live_WhenTheSessionRowIsGone_Is404()
    {
        var recording = new FakeRecording(session: null, []);

        var result = await Controller(new FakeAccess(LiveGrant()), recording, new SpyViewers())
            .Live(after: -1, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Live_AppliesNoStoreShareHeaders_EvenOnA404()
    {
        // A token-bearing 404 must carry the same referrer/no-store hygiene as a rendered share page.
        var controller = Controller(new FakeAccess(grant: null), new FakeRecording(null, []), new SpyViewers());

        await controller.Live(after: -1, CancellationToken.None);

        Assert.Equal("strict-origin", controller.Response.Headers["Referrer-Policy"].ToString());
        Assert.Equal("private, no-store, max-age=0", controller.Response.Headers.CacheControl.ToString());
    }

    // ---- helpers + fakes ----------------------------------------------------------------------------

    private static RecordingSession MakeSession(string status, int pointCount = 0) => new()
    {
        SessionId = "sess-x", TrackId = new TrackId(TrackId), UserId = "owner", Status = status,
        ActivityType = "Run", StartedAt = DateTime.UtcNow, PointCount = pointCount,
    };

    private static ExternalGpsPoint Point(long seq) =>
        new() { Lat = 47 + seq, Lng = 8 + seq, Seq = seq, TimestampUtc = DateTime.UtcNow };

    private sealed class FakeAccess(ShareGrant? grant) : IShareLinkAccess
    {
        public Task<ShareGrant?> ResolveAsync(string token, CancellationToken ct) => Task.FromResult(grant);
        public Task<ShareGrant?> CurrentAsync(CancellationToken ct) => Task.FromResult(grant);
        public async Task<T?> WidenAsync<T>(ShareKind kind, int resourceId, Func<Task<T?>> read, CancellationToken ct)
            where T : class => grant is null || !grant.Covers(kind, resourceId) ? null : await read();
    }

    private sealed class SpyViewers : ILiveViewerRegistry
    {
        public string? Touched;
        public void TouchPublic(string sessionId) => Touched = sessionId;
        public void Join(string sessionId, string connectionId) { }
        public void Leave(string sessionId, string connectionId) { }
        public void LeaveAll(string connectionId) { }
        public bool IsWatched(string sessionId) => false;
    }

    private sealed class FakeRecording(RecordingSession? session, List<ExternalGpsPoint> points) : IRecordingService
    {
        public bool SessionQueried;

        public Task<RecordingSession?> GetLiveShareSessionAsync(TrackId trackId, string ownerUserId, CancellationToken ct)
        {
            SessionQueried = true;
            LiveShareOwnerAsked = ownerUserId;
            return Task.FromResult(session);
        }

        public string? LiveShareOwnerAsked;

        public Task<List<ExternalGpsPoint>> GetLivePointsAsync(RecordingSession s, long afterSeq)
            => Task.FromResult(points);

        // Unused by the share surface — the controller only touches the two methods above.
        public Task<RecordingSession?> StartSessionAsync(StartRecordingRequest r, string u) => throw new NotImplementedException();
        public Task<List<ExternalGpsPoint>?> AppendPointsAsync(string s, RecordingPointsBatch b, string u) => throw new NotImplementedException();
        public Task<RecordingSession?> StopSessionAsync(string s, string u) => throw new NotImplementedException();
        public Task<RecordingSession?> PauseSessionAsync(string s, string u) => throw new NotImplementedException();
        public Task<RecordingSession?> ResumeSessionAsync(string s, string u) => throw new NotImplementedException();
        public Task<RecordingSession?> RetypeSessionAsync(string s, string a, string u) => throw new NotImplementedException();
        public Task<RecordingSession?> AbandonSessionAsync(string s, string u) => throw new NotImplementedException();
        public Task<RecordingSession?> GetActiveSessionAsync(string u) => throw new NotImplementedException();
        public Task<List<RecordingSession>> GetActiveSessionsVisibleToAsync(List<string> ids) => throw new NotImplementedException();
        public Task<RecordingSession?> GetSessionAsync(string s) => throw new NotImplementedException();
        public Task<bool> HasSessionForTrackAsync(TrackId t, string u, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<RecentSessionView>> GetRecentSessionsWithReviewAsync(string u, int limit = 20) => throw new NotImplementedException();
        public Task<List<ExternalGpsPoint>> GetPointsAfterSeqAsync(string s, long a) => throw new NotImplementedException();
        public Task<bool> RetryFinalizeFailedAsync(string s, string u) => throw new NotImplementedException();
        public Task<bool> MarkFinalizeFailedAsync(string s, string u) => throw new NotImplementedException();
        public Task<bool> DeleteAbandonedSessionAsync(string s, string u) => throw new NotImplementedException();
    }
}
