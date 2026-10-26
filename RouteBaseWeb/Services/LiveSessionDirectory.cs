using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;

namespace RouteBaseWeb.Services;

/// <summary>
/// One live recording the current user is allowed to watch, named for display.
/// </summary>
public sealed record LiveSessionInfo(
    string SessionId,
    string DisplayName,
    bool IsSelf,
    string ActivityType,
    DateTime StartedAt,
    string Status,
    int TrackId,
    int PointCount);

/// <summary>
/// Answers "whose live recordings may I watch, and what are they called" — the one place that
/// joins recording sessions to friend display names. Shared by the LiveView page (which resolves
/// an explicit id list) and the sessions/live endpoint (which discovers who is live right now).
/// </summary>
public interface ILiveSessionDirectory
{
    /// <summary>Every live session the current user may watch: own active one + friends' non-private recordings.</summary>
    Task<List<LiveSessionInfo>> GetVisibleAsync();

    /// <summary>
    /// Resolves an explicit id list (route / query string), silently dropping ids that do not exist or
    /// that the user may not watch — a forged id must not 403 the whole page. Ordered by start time and
    /// capped at <see cref="MaxWatchable"/>. Status is not filtered: a stopped session still renders
    /// (LiveView shows its ENDED banner).
    /// </summary>
    Task<List<LiveSessionInfo>> ResolveAsync(IEnumerable<string> sessionIds);
}

public class LiveSessionDirectory : ILiveSessionDirectory
{
    /// <summary>Bounds the SignalR groups + map layers one viewer can open at once.</summary>
    public const int MaxWatchable = 8;

    private readonly IRecordingService _recordingService;
    private readonly IFriendshipService _friendshipService;
    private readonly IVisibilityService _visibilityService;

    public LiveSessionDirectory(
        IRecordingService recordingService,
        IFriendshipService friendshipService,
        IVisibilityService visibilityService)
    {
        _recordingService = recordingService;
        _friendshipService = friendshipService;
        _visibilityService = visibilityService;
    }

    /// <inheritdoc />
    public async Task<List<LiveSessionInfo>> GetVisibleAsync()
    {
        // Sequential on purpose: these services share the request-scoped DbContext.
        var userId = _visibilityService.GetCurrentUserId();
        var own = await _recordingService.GetActiveSessionAsync(userId);
        var names = await GetFriendNamesAsync(userId);

        var sessions = new List<LiveSessionInfo>();
        if (own is not null)
            sessions.Add(ToInfo(own, userId, names));

        if (names.Count > 0)
        {
            var friendSessions = await _recordingService.GetActiveSessionsVisibleToAsync([.. names.Keys]);
            sessions.AddRange(friendSessions.Select(s => ToInfo(s, userId, names)));
        }

        return [.. sessions.OrderBy(s => s.StartedAt)];
    }

    /// <inheritdoc />
    public async Task<List<LiveSessionInfo>> ResolveAsync(IEnumerable<string> sessionIds)
    {
        var userId = _visibilityService.GetCurrentUserId();
        var names = await GetFriendNamesAsync(userId);

        var resolved = new List<LiveSessionInfo>();
        // Cap WATCHABLE results, not scanned ids: stale/foreign ids in a shared LiveView URL must not
        // consume the cap and silently drop the viewable sessions behind them. The scan itself stays
        // bounded too (each id costs two queries), at 4× so a URL with a few dead ids still resolves.
        foreach (var id in sessionIds.Distinct(StringComparer.Ordinal).Take(MaxWatchable * 4))
        {
            if (resolved.Count >= MaxWatchable) break;

            var session = await _recordingService.GetSessionAsync(id);
            if (session is null) continue;
            if (!await _visibilityService.CanViewItemAsync(session.UserId, session.Visibility)) continue;

            resolved.Add(ToInfo(session, userId, names));
        }

        return [.. resolved.OrderBy(s => s.StartedAt)];
    }

    private async Task<Dictionary<string, string>> GetFriendNamesAsync(string userId)
    {
        var friends = await _friendshipService.GetFriendsAsync(userId);
        // DisplayName → UserId, never Email — same PI-10 rule as OwnerLabels (FriendListItem, not AppUser).
        return friends.ToDictionary(f => f.UserId, f => f.DisplayName ?? f.UserId, StringComparer.Ordinal);
    }

    private static LiveSessionInfo ToInfo(
        Models.RecordingSession s, string currentUserId, Dictionary<string, string> friendNames)
    {
        var isSelf = s.UserId == currentUserId;
        return new LiveSessionInfo(
            s.SessionId,
            // Not in the friend map = a stranger's PUBLIC session (ResolveAsync admits those). "Rider",
            // never the raw internal user id — that leaked the authentik sub as a display name (SVC-13);
            // same fallback the public share view uses.
            isSelf ? "You" : friendNames.GetValueOrDefault(s.UserId, "Rider"),
            isSelf,
            s.ActivityType,
            s.StartedAt,
            s.Status,
            s.TrackId.Value,
            s.PointCount);
    }
}
