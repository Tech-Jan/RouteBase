using RouteBaseWeb.Models;
using RouteBaseWeb.Models.GridFilter;

namespace RouteBaseWeb.Services.Interfaces;

/// <summary>
/// Read-only queries on Activity metadata. Does not load raw GPS rows directly —
/// use <see cref="IActivityGpsService"/> for GPS coordinate access.
/// </summary>
public interface IActivityQueryService
{
    Task<List<Activity>> GetAllActivitiesAsync();

    /// <summary>
    /// Get activity metadata by ID without loading GPS data. Use when only
    /// Activity table columns + MultiDayTour are needed.
    /// </summary>
    Task<Activity?> GetActivityMetadataByIdAsync(int id);

    /// <summary>
    /// Metadata for an activity the caller OWNS, at any lifecycle state — the visible-gated read above sees
    /// only released activities, so an awaiting-review or trashed ride reads null through it. Ownership is
    /// the whole gate here; the caller decides which states it will serve (twin of
    /// <see cref="IActivityGpsService.GetOwnedTrackCoordinatesAsync"/>).
    /// </summary>
    Task<Activity?> GetOwnedActivityMetadataByIdAsync(int id);

    /// <summary>Ownership check (not visibility): true only when the current user owns the activity.
    /// Use to gate writes that a mere viewer must not perform.</summary>
    Task<bool> IsActivityOwnedAsync(TrackId trackId);

    /// <summary>Visibility existence check: true when the activity exists and the current user may see
    /// it (own / public / friends). A single indexed <c>EXISTS</c> — use to guard read endpoints
    /// instead of loading a full <see cref="GetActivityMetadataByIdAsync"/> row just to null-check it.</summary>
    Task<bool> IsActivityVisibleAsync(TrackId trackId);

    /// <summary>
    /// Lightweight projection — only the 6 columns needed for map-overlay responses.
    /// Results are cached. Prefer this when full Activity rows are not required.
    /// </summary>
    Task<Dictionary<int, ActivityMapSlice>> GetActivitySlicesByIdsAsync(List<int> trackIds, CancellationToken ct = default);

    /// <summary>
    /// Start/end coordinates for the given activity IDs, read straight from the Activity
    /// table (no GPS join). Cheap enough to call before loading track GPS so the map camera
    /// can be framed up front. Visibility-scoped to the current user.
    /// </summary>
    Task<List<ActivityEndpoint>> GetActivityEndpointsAsync(List<int> trackIds, CancellationToken ct = default);

    Task<List<string>> GetActivityTypesAsync();

    /// <summary>Track IDs whose stored bounding box (<c>Activity_Bbox</c>) contains the given point.
    /// A coarse spatial pre-filter — <b>not</b> visibility-scoped; callers intersect with their own
    /// candidate/owned set. Keeps the point-in-bbox SQL beside Activities' other bbox knowledge.</summary>
    Task<List<int>> GetTrackIdsContainingPointAsync(double lat, double lng, CancellationToken ct = default);

    /// <summary>
    /// Count of all activities visible to the current user, with no filters applied — the "grand total"
    /// denominator for the grid's row count. A single cheap <c>COUNT</c>.
    /// </summary>
    Task<int> GetVisibleActivityCountAsync();

    /// <summary>
    /// Photo counts for a page of activities, as one batched <c>GROUP BY</c> over the indexed
    /// <c>Photos.track_id</c> — never per-row. The display value for the grid's "photos" column.
    /// Tracks with no photos are absent from the dictionary (read a missing key as 0). Visibility is
    /// inherited from the activity, so callers pass ids they already scoped; no extra photo predicate.
    /// </summary>
    Task<Dictionary<TrackId, int>> GetPhotoCountsAsync(IReadOnlyCollection<TrackId> trackIds, CancellationToken ct = default);

    /// <summary>
    /// Server-side paged, filtered, and sorted query for the activities grid.
    /// Returns the page of activities and the total matching count.
    /// When <paramref name="activityTypes"/> is non-empty, the grid is filtered to those exact raw
    /// activity types (multi-select). Otherwise <paramref name="activityType"/> applies the canonical
    /// type-group filter (e.g. "bike"/"run"/"walk").
    /// <paramref name="knownCount"/>: the total matching count when the caller has already computed it over
    /// byte-identical predicates (e.g. <see cref="GetFilteredActivityIdsAsync"/> materialized the full id set,
    /// so its <c>.Count</c> is the same number). Passing it skips the internal <c>COUNT</c>; null re-queries.
    /// </summary>
    Task<(List<Activity> Items, int TotalCount)> GetPagedActivitiesAsync(
        string? activityType,
        DateTime? startDate,
        DateTime? endDate,
        string sortColumn,
        string sortDirection,
        int page,
        int pageSize,
        string? ownerUserId = null,
        IReadOnlyList<string>? activityTypes = null,
        GridFilterCollection? filters = null,
        IReadOnlyList<string>? ownerUserIds = null,
        bool ownerMatchNone = false,
        IReadOnlyCollection<TrackId>? restrictIds = null,
        int? knownCount = null);

    /// <summary>
    /// Returns all TrackIds matching the given filters — no GPS or metadata loaded.
    /// Used to populate the "all activities" map scope without fetching full rows.
    /// See <see cref="GetPagedActivitiesAsync"/> for how <paramref name="activityTypes"/> and
    /// <paramref name="activityType"/> interact.
    /// </summary>
    Task<List<TrackId>> GetFilteredActivityIdsAsync(
        string? activityType,
        DateTime? startDate,
        DateTime? endDate,
        string? ownerUserId = null,
        IReadOnlyList<string>? activityTypes = null,
        GridFilterCollection? filters = null,
        IReadOnlyList<string>? ownerUserIds = null,
        bool ownerMatchNone = false,
        IReadOnlyCollection<TrackId>? restrictIds = null);

    /// <summary>Get activities that have GPS start/end coordinates stored on the Activity row.</summary>
    Task<List<Activity>> GetActivitiesWithGpsCoordinatesAsync(int limit = 100);

}
