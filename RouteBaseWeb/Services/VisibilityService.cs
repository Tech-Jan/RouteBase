using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Lifecycle;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services;

/// <summary>
/// Scoped service that filters EF Core queries to the current user's visible data.
///
/// Visible* methods return own data + friends' non-private data + all public data.
/// Owned* methods always return only the current user's own rows (used for writes/deletes).
/// </summary>
public class VisibilityService : IVisibilityService
{
    private readonly ICurrentUserService _currentUser;
    private readonly IFriendshipService _friendshipService;
    private readonly RouteBaseDbContext _db;

    public VisibilityService(
        ICurrentUserService currentUser, IFriendshipService friendshipService, RouteBaseDbContext db)
    {
        _currentUser       = currentUser;
        _friendshipService = friendshipService;
        _db                = db;
    }

    public string GetCurrentUserId() => _currentUser.GetUserId();

    /// <summary>
    /// Owner-scoping helper (self + accepted friends). NOT a per-item authorization check — callers
    /// must still enforce per-row Visibility (see the private-live-recording incident, INC-03).
    /// </summary>
    public async Task<List<string>> GetVisibleUserIdsAsync()
    {
        var userId    = _currentUser.GetUserId();
        var friendIds = await _friendshipService.GetAcceptedFriendIdsAsync(userId);
        var result    = new List<string>(friendIds.Count + 1) { userId };
        result.AddRange(friendIds);
        return result;
    }

    /// <summary>Per-item authorization for a single already-loaded item (see INC-03).
    /// Owner always passes; otherwise public → all, friends → accepted friends only,
    /// anything else (incl. "private" and unrecognized values) → deny.</summary>
    public async Task<bool> CanViewItemAsync(string ownerUserId, string? visibility)
    {
        var userId = _currentUser.GetUserId();
        if (ownerUserId == userId) return true;
        return visibility switch
        {
            Visibilities.Public  => true,
            Visibilities.Friends => (await _friendshipService.GetAcceptedFriendIdsAsync(userId)).Contains(ownerUserId),
            _                    => false, // "private" + anything unrecognized
        };
    }

    /// <summary>Per-item activity authorization by track id — the lifecycle gate encoded once
    /// (INC-03 / AS-01), the same single-row check per-item callers used to hand-roll. Owner passes in any
    /// lifecycle state (the trash + review slices need access to their own rows); a non-owner needs
    /// <c>Lifecycle == "released"</c> AND the <see cref="CanViewItemAsync"/> rules; unknown → false.</summary>
    public async Task<bool> CanViewActivityAsync(TrackId trackId)
    {
        var act = await _db.Activities.AsNoTracking()
            .Where(a => a.TrackId == trackId)
            .Select(a => new { a.UserId, a.Visibility, a.Lifecycle })
            .FirstOrDefaultAsync();
        if (act is null) return false;
        if (act.UserId == _currentUser.GetUserId()) return true; // owner: any lifecycle state
        return act.Lifecycle == LifecycleStates.Released
            && await CanViewItemAsync(act.UserId ?? "", act.Visibility);
    }

    // --- Read filters ---
    // Friends membership is a correlated EXISTS subquery over Friendship rather than a
    // pre-fetched ID list: keeps the whole filter async-safe (no sync-over-async) and lets
    // SQLite use the Friendship indexes. The subquery composes into the caller's query.

    public IQueryable<Activity> VisibleActivities(IQueryable<Activity> query)
    {
        var userId = _currentUser.GetUserId();
        return query
            // THE gate (ActivityLifecycle invariant 2): only a released activity is a real one. Awaiting
            // review, parked, trashed, merged-away — all the same here, all invisible to every list / stats /
            // map / GPS / segment path that funnels through this method. One clause, not a growing list of
            // states to exclude: that growth is exactly how review_state ended up meaning three things.
            // The trash + review slices reach the others via OwnedActivities (which has no such filter).
            .Where(a => a.Lifecycle == LifecycleStates.Released)
            .Where(a =>
                a.UserId == userId
                || a.Visibility == Visibilities.Public
                || (a.Visibility == Visibilities.Friends && _db.Friendships.Any(f =>
                       f.Status == "accepted"
                       && ((f.RequesterId == userId && f.AddresseeId == a.UserId)
                           || (f.AddresseeId == userId && f.RequesterId == a.UserId)))));
    }

    public IQueryable<Segment> VisibleSegments(IQueryable<Segment> query)
    {
        var userId = _currentUser.GetUserId();
        return query.Where(s =>
            s.UserId == userId
            || s.Visibility == Visibilities.Public
            || (s.Visibility == Visibilities.Friends && _db.Friendships.Any(f =>
                   f.Status == "accepted"
                   && ((f.RequesterId == userId && f.AddresseeId == s.UserId)
                       || (f.AddresseeId == userId && f.RequesterId == s.UserId)))));
    }

    public IQueryable<MultiDayTour> VisibleTours(IQueryable<MultiDayTour> query)
        => query.Where(t => t.UserId == _currentUser.GetUserId());

    // --- Write/delete filters (always owner-only) ---

    public IQueryable<Activity> OwnedActivities(IQueryable<Activity> query)
        => query.Where(a => a.UserId == _currentUser.GetUserId());

    public IQueryable<Segment> OwnedSegments(IQueryable<Segment> query)
        => query.Where(s => s.UserId == _currentUser.GetUserId());

    public IQueryable<MultiDayTour> OwnedTours(IQueryable<MultiDayTour> query)
        => query.Where(t => t.UserId == _currentUser.GetUserId());

    public IQueryable<AppCredentials> OwnedCredentials(IQueryable<AppCredentials> query)
        => query.Where(c => c.UserId == _currentUser.GetUserId());
}
