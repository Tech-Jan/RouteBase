using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using RouteBaseWeb.Hubs;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Requests;
using RouteBaseWeb.Models.Sync;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using Xunit;

namespace RouteBaseWeb.Tests.Hubs;

/// <summary>
/// Pins <see cref="RecordingHub.JoinSession"/> against the add-after-disconnect leak (CONC-P2): a join
/// suspended at its DB awaits whose connection drops must not leave a dead connection id in the
/// <see cref="LiveViewerRegistry"/> — that would report the session as watched forever and pin the
/// recording phone at the fast upload cadence for the rest of the ride (battery).
/// </summary>
public class RecordingHubTests
{
    private const string Session = "sess-1";

    private static RecordingHub Hub(LiveViewerRegistry registry, string connectionId, bool aborted)
    {
        var session = new RecordingSession { UserId = "owner", Visibility = "public" };
        // TestVisibilityService admits a "public" item regardless of owner — the join clears the
        // visibility gate so the test isolates the connection-abort branch.
        return new RecordingHub(new StubRecording(session), new TestVisibilityService(), registry)
        {
            Context = new FakeHubCallerContext(connectionId, aborted),
            Groups  = new NoOpGroups(),
        };
    }

    [Fact]
    public async Task JoinSession_ConnectionAbortedDuringJoin_LeavesNoDeadViewer()
    {
        var registry = new LiveViewerRegistry(() => DateTime.UtcNow);

        var result = await Hub(registry, "dead-conn", aborted: true).JoinSession(Session);

        Assert.False(result.Ok);
        Assert.False(registry.IsWatched(Session)); // dead connection must not linger as "watched"
    }

    [Fact]
    public async Task JoinSession_LiveConnection_RegistersTheViewer()
    {
        var registry = new LiveViewerRegistry(() => DateTime.UtcNow);

        var result = await Hub(registry, "live-conn", aborted: false).JoinSession(Session);

        Assert.True(result.Ok);
        Assert.True(registry.IsWatched(Session)); // the happy path still counts the viewer
    }

    // ── Fakes ─────────────────────────────────────────────────────────────

    private sealed class FakeHubCallerContext(string connectionId, bool aborted) : HubCallerContext
    {
        public override string ConnectionId { get; } = connectionId;
        public override CancellationToken ConnectionAborted { get; } =
            aborted ? new CancellationToken(canceled: true) : CancellationToken.None;
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal User => new();
        public override IDictionary<object, object?> Items => throw new NotSupportedException();
        public override IFeatureCollection Features => new FeatureCollection();
        public override void Abort() => throw new NotSupportedException();
    }

    private sealed class NoOpGroups : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class StubRecording(RecordingSession session) : IRecordingService
    {
        public Task<RecordingSession?> GetSessionAsync(string sessionId) => Task.FromResult<RecordingSession?>(session);

        // Unused by JoinSession.
        public Task<RecordingSession?> StartSessionAsync(StartRecordingRequest r, string u) => throw new NotImplementedException();
        public Task<List<ExternalGpsPoint>?> AppendPointsAsync(string s, RecordingPointsBatch b, string u) => throw new NotImplementedException();
        public Task<RecordingSession?> StopSessionAsync(string s, string u) => throw new NotImplementedException();
        public Task<RecordingSession?> PauseSessionAsync(string s, string u) => throw new NotImplementedException();
        public Task<RecordingSession?> ResumeSessionAsync(string s, string u) => throw new NotImplementedException();
        public Task<RecordingSession?> RetypeSessionAsync(string s, string a, string u) => throw new NotImplementedException();
        public Task<RecordingSession?> AbandonSessionAsync(string s, string u) => throw new NotImplementedException();
        public Task<RecordingSession?> GetActiveSessionAsync(string u) => throw new NotImplementedException();
        public Task<List<RecordingSession>> GetActiveSessionsVisibleToAsync(List<string> ids) => throw new NotImplementedException();
        public Task<RecordingSession?> GetLiveShareSessionAsync(TrackId t, string o, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> HasSessionForTrackAsync(TrackId t, string u, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<ExternalGpsPoint>> GetLivePointsAsync(RecordingSession s, long afterSeq) => throw new NotImplementedException();
        public Task<List<RecentSessionView>> GetRecentSessionsWithReviewAsync(string u, int limit = 20) => throw new NotImplementedException();
        public Task<List<ExternalGpsPoint>> GetPointsAfterSeqAsync(string s, long a) => throw new NotImplementedException();
        public Task<bool> RetryFinalizeFailedAsync(string s, string u) => throw new NotImplementedException();
        public Task<bool> MarkFinalizeFailedAsync(string s, string u) => throw new NotImplementedException();
        public Task<bool> DeleteAbandonedSessionAsync(string s, string u) => throw new NotImplementedException();
    }
}
