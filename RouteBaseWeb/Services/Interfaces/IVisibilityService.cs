using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services.Interfaces;

/// <summary>
/// Filters EF Core queries to only include records the current user can access.
///
/// Visible* returns own data + friends' non-private data + public data.
/// Owned* stays owner-only (used for writes/deletes).
///
/// For raw SQL paths, call GetCurrentUserId() to get the userId for WHERE clauses.
/// For per-row authorization of a single already-loaded item, use CanViewItemAsync.
/// </summary>
public interface IVisibilityService
{
    // --- Read filters (expands when friends feature is added) ---
    IQueryable<Activity> VisibleActivities(IQueryable<Activity> query);
    IQueryable<Segment> VisibleSegments(IQueryable<Segment> query);
    IQueryable<MultiDayTour> VisibleTours(IQueryable<MultiDayTour> query);

    // --- Write/delete filters (always owner-only) ---
    IQueryable<Activity> OwnedActivities(IQueryable<Activity> query);
    IQueryable<Segment> OwnedSegments(IQueryable<Segment> query);
    IQueryable<MultiDayTour> OwnedTours(IQueryable<MultiDayTour> query);
    IQueryable<AppCredentials> OwnedCredentials(IQueryable<AppCredentials> query);

    /// <summary>Convenience pass-through for raw SQL paths that need the userId in a WHERE clause.</summary>
    string GetCurrentUserId();

    /// <summary>Returns [self, ...acceptedFriendIds] — the full set of user IDs whose data the current user can see.</summary>
    Task<List<string>> GetVisibleUserIdsAsync();

    /// <summary>Per-item authorization: may the current user view an item owned by
    /// <paramref name="ownerUserId"/> with per-row <paramref name="visibility"/>
    /// (private|friends|public)? Unknown values deny (treated as private).</summary>
    Task<bool> CanViewItemAsync(string ownerUserId, string? visibility);

    /// <summary>Per-item authorization for an activity by track id, including the review gate.
    /// Owner sees it in any lifecycle state (the trash + review slices need their own access); a non-owner
    /// needs <c>Lifecycle == "released"</c> AND the <see cref="CanViewItemAsync"/> visibility rules.
    /// Unknown track → false. Encodes the gate <see cref="VisibleActivities"/> applies in bulk
    /// (INC-03 / AS-01) so per-item callers don't re-remember it.</summary>
    Task<bool> CanViewActivityAsync(TrackId trackId);
}
