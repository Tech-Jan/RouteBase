using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Requests;
using RouteBaseWeb.Models.Sync;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// LiveSessionDirectory is the gate between "a session id in the URL" and "a rider drawn on the
/// LiveView map", so the pins that matter are the ones about *not* showing what the viewer may not see.
/// TestVisibilityService grants own + public and denies everything else.
/// </summary>
public class LiveSessionDirectoryTests
{
    private const string Me = "me";
    private const string Friend = "friend-1";

    private static RecordingSession Session(
        string id, string userId, string visibility = "friends",
        string status = "recording", int minutesAgo = 10)
        => new()
        {
            SessionId = id,
            UserId = userId,
            Visibility = visibility,
            Status = status,
            ActivityType = "Ride",
            StartedAt = DateTime.UtcNow.AddMinutes(-minutesAgo),
            TrackId = new TrackId(7),
            PointCount = 3,
        };

    private static LiveSessionDirectory Directory(
        List<RecordingSession> sessions, RecordingSession? ownActive = null, params string[] friendIds)
        => new(
            new FakeRecordingService(sessions, ownActive),
            new FakeFriendshipService(friendIds),
            new TestVisibilityService(Me));

    [Fact]
    public async Task ResolveAsync_drops_a_session_the_viewer_may_not_watch()
    {
        var mine = Session("mine", Me);
        var friendsPrivate = Session("hidden", Friend, visibility: "friends"); // test double denies
        var dir = Directory([mine, friendsPrivate]);

        var resolved = await dir.ResolveAsync(["mine", "hidden"]);

        Assert.Equal(["mine"], resolved.Select(s => s.SessionId), StringComparer.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_keeps_a_public_session_of_someone_else()
    {
        var dir = Directory([Session("pub", Friend, visibility: "public")]);

        var resolved = await dir.ResolveAsync(["pub"]);

        Assert.Single(resolved);
        Assert.False(resolved[0].IsSelf);
    }

    [Fact]
    public async Task ResolveAsync_ignores_unknown_ids_instead_of_failing()
    {
        var dir = Directory([Session("mine", Me)]);

        var resolved = await dir.ResolveAsync(["mine", "no-such-session"]);

        Assert.Equal(["mine"], resolved.Select(s => s.SessionId), StringComparer.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_orders_by_start_time_and_caps_at_the_watch_limit()
    {
        // 10 own sessions, newest first on input — only the 8 earliest-started may be watched.
        var sessions = Enumerable.Range(1, 10)
            .Select(i => Session($"s{i}", Me, minutesAgo: i))
            .ToList();
        var dir = Directory(sessions);

        var resolved = await dir.ResolveAsync(sessions.Select(s => s.SessionId));

        Assert.Equal(LiveSessionDirectory.MaxWatchable, resolved.Count);
        Assert.Equal(resolved.OrderBy(s => s.StartedAt).Select(s => s.SessionId), resolved.Select(s => s.SessionId), StringComparer.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_keeps_a_finished_session_so_its_track_still_renders()
    {
        var dir = Directory([Session("done", Me, status: "finalized")]);

        var resolved = await dir.ResolveAsync(["done"]);

        Assert.Equal("finalized", resolved[0].Status);
    }

    [Fact]
    public async Task GetVisibleAsync_returns_own_active_session_plus_live_friends()
    {
        var mine = Session("mine", Me, minutesAgo: 5);
        var theirs = Session("theirs", Friend, minutesAgo: 30);
        var dir = Directory([mine, theirs], ownActive: mine, friendIds: Friend);

        var live = await dir.GetVisibleAsync();

        Assert.Equal(["theirs", "mine"], live.Select(s => s.SessionId), StringComparer.Ordinal); // earliest start first
        Assert.Equal("You", live.Single(s => s.IsSelf).DisplayName);
        Assert.Equal("Friend One", live.Single(s => !s.IsSelf).DisplayName);
    }

    [Fact]
    public async Task ResolveAsync_labels_a_strangers_public_session_Rider_never_the_internal_user_id()
    {
        // SVC-13: the owner of an admitted PUBLIC session need not be a friend, so their name is absent
        // from the friend map — the fallback must never render the raw internal account id (authentik sub).
        var dir = Directory([Session("pub", "authentik-sub-8f3c", visibility: "public")]);

        var resolved = await dir.ResolveAsync(["pub"]);

        Assert.Equal("Rider", resolved[0].DisplayName);
    }

    [Fact]
    public void LiveSessionDto_stamps_StartedAt_as_UTC_so_the_browser_cannot_read_it_as_local()
    {
        // CC2-04→AR-02: SQLite hands back Kind=Unspecified. The field is a DateTimeOffset now, so the
        // wire always carries an offset; what could still go wrong is From() converting Unspecified as
        // server-LOCAL time (the implicit-cast trap). A zero offset from an Unspecified input is the
        // thing to assert — and the serialized form must say so explicitly.
        var info = new LiveSessionInfo(
            "s1", "You", true, "Ride",
            new DateTime(2026, 7, 12, 10, 0, 0, DateTimeKind.Unspecified),
            "recording", 7, 3);

        var dto = RouteBaseWeb.Models.Recording.LiveSessionDto.From(info);

        Assert.Equal(TimeSpan.Zero, dto.StartedAt.Offset);
        Assert.EndsWith("+00:00\"", System.Text.Json.JsonSerializer.Serialize(dto.StartedAt), StringComparison.Ordinal); // "…T10:00:00+00:00"
    }

    // ── Doubles ──────────────────────────────────────────────────────────

    private sealed class FakeRecordingService(List<RecordingSession> sessions, RecordingSession? ownActive)
        : IRecordingService
    {
        public Task<RecordingSession?> GetSessionAsync(string sessionId)
            => Task.FromResult(sessions.FirstOrDefault(s => s.SessionId == sessionId));

        public Task<RecordingSession?> GetLiveShareSessionAsync(TrackId trackId, string ownerUserId, CancellationToken ct)
            => Task.FromResult(sessions.FirstOrDefault(s => s.TrackId == trackId && s.UserId == ownerUserId));

        public Task<List<ExternalGpsPoint>> GetLivePointsAsync(RecordingSession session, long afterSeq) => throw new NotImplementedException();

        public Task<RecordingSession?> GetActiveSessionAsync(string userId) => Task.FromResult(ownActive);

        public Task<List<RecordingSession>> GetActiveSessionsVisibleToAsync(List<string> visibleUserIds)
            => Task.FromResult(sessions
                .Where(s => s.Status == "recording" && visibleUserIds.Contains(s.UserId) && s.Visibility != "private")
                .ToList());

        public Task<RecordingSession?> StartSessionAsync(StartRecordingRequest request, string userId) => throw new NotImplementedException();
        public Task<List<ExternalGpsPoint>?> AppendPointsAsync(string sessionId, RecordingPointsBatch batch, string userId) => throw new NotImplementedException();
        public Task<RecordingSession?> StopSessionAsync(string sessionId, string userId) => throw new NotImplementedException();
        public Task<RecordingSession?> PauseSessionAsync(string sessionId, string userId) => throw new NotImplementedException();
        public Task<RecordingSession?> ResumeSessionAsync(string sessionId, string userId) => throw new NotImplementedException();
        public Task<RecordingSession?> RetypeSessionAsync(string sessionId, string activityType, string userId) => throw new NotImplementedException();
        public Task<RecordingSession?> AbandonSessionAsync(string sessionId, string userId) => throw new NotImplementedException();
        public Task<List<RecentSessionView>> GetRecentSessionsWithReviewAsync(string userId, int limit = 20) => throw new NotImplementedException();
        public Task<List<ExternalGpsPoint>> GetPointsAfterSeqAsync(string sessionId, long afterSeq) => throw new NotImplementedException();
        public Task<bool> RetryFinalizeFailedAsync(string sessionId, string userId) => throw new NotImplementedException();
        public Task<bool> MarkFinalizeFailedAsync(string sessionId, string userId) => throw new NotImplementedException();
        public Task<bool> DeleteAbandonedSessionAsync(string sessionId, string userId) => throw new NotImplementedException();
        public Task<bool> HasSessionForTrackAsync(TrackId trackId, string userId, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class FakeFriendshipService(string[] friendIds) : IFriendshipService
    {
        public Task<List<FriendListItem>> GetFriendsAsync(string userId)
            => Task.FromResult(friendIds
                .Select(id => new FriendListItem { UserId = id, DisplayName = "Friend One" })
                .ToList());

        public Task<List<string>> GetAcceptedFriendIdsAsync(string userId) => Task.FromResult(friendIds.ToList());
        public Task<List<FriendRequestItem>> GetPendingRequestsAsync(string userId) => throw new NotImplementedException();
        public Task<int> GetPendingRequestCountAsync(string userId) => throw new NotImplementedException();
        public Task SendRequestAsync(string requesterId, string addresseeId) => throw new NotImplementedException();
        public Task AcceptRequestAsync(string currentUserId, int friendshipId) => throw new NotImplementedException();
        public Task DeclineRequestAsync(string currentUserId, int friendshipId) => throw new NotImplementedException();
        public Task RemoveFriendAsync(string currentUserId, int friendshipId) => throw new NotImplementedException();
        public Task<List<UserSearchResult>> SearchUsersAsync(string query, string excludeUserId) => throw new NotImplementedException();
    }
}
