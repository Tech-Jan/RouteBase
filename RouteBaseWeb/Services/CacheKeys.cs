using RouteBaseWeb.Models;
namespace RouteBaseWeb.Services;

/// <summary>
/// Central repository of cache key generators.
/// All user-scoped keys are prefixed with userId to prevent cross-user cache leaks.
/// </summary>
public static class CacheKeys
{
    // --- Segment effort / performance keys ---

    public static string SegmentEfforts(string userId, SegmentId segmentId) =>
        $"{userId}_seg_efforts_{segmentId}";

    public static string SegmentEffortsGps(string userId, SegmentId segmentId) =>
        $"{userId}_seg_efforts_{segmentId}_gps";

    public static string SegmentEffortTotals(string userId, SegmentId segmentId) =>
        $"{userId}_seg_effort_totals_{segmentId}";

    public static string SegmentPerformance(string userId, SegmentId segmentId) =>
        $"{userId}_seg_perf_{segmentId}";

    // Per-effort performance data — chunked chart loading caches each effort separately so the
    // server never materializes a whole-segment performance list (which can be 100s of MB). The
    // point budget is part of the key: budget 0 = full-res entries; distinct budgets = distinct
    // entries (5-min TTL bounds the growth).
    public static string SegmentPerformanceEffort(string userId, SegmentId segmentId, GpsInSegmentId effortId, int maxPts) =>
        $"{userId}_seg_perf_{segmentId}_eff_{effortId}_{maxPts}";

    // --- Share-path segment keys ---
    //
    // Keyed by the GRANT OWNER, never by the viewer. The content is a pure function of (segment, owner):
    // identical for every live token on that segment, so this is one namespace per shared segment rather
    // than one per token.
    //
    // Deliberately a SEPARATE family from the SegmentEfforts/SegmentPerformance keys above, not a reuse
    // of them under a widened identity. Those entries are computed under the owner's FULL visibility and
    // therefore contain their friends' efforts; every SegmentEfforts read checks its cache BEFORE the
    // visibility gate, so a share request landing on one would return third-party efforts on a cache hit,
    // before any predicate ran. Sharing a namespace also breaks the owner's own page, which would inherit
    // the narrowed share list until TTL.

    public static string ShareSegmentEfforts(string ownerUserId, SegmentId segmentId) =>
        $"{ownerUserId}_share_seg_efforts_{segmentId}";

    public static string ShareSegmentTotals(string ownerUserId, SegmentId segmentId) =>
        $"{ownerUserId}_share_seg_totals_{segmentId}";

    public static string ShareSegmentPerfEffort(string ownerUserId, SegmentId segmentId, GpsInSegmentId effortId, int maxPts) =>
        $"{ownerUserId}_share_seg_perf_{segmentId}_eff_{effortId}_{maxPts}";

    // --- Segment GPS geometry keys ---

    public static string SegmentGpsSummary(string userId, SegmentId segmentId, int maxPoints) =>
        $"{userId}_seg_gps_summary_{segmentId}_{maxPoints}";

    // Reference polyline geometry is identical for every user → not user-scoped.
    public static string SegmentReference(SegmentId segmentId) =>
        $"seg_reference_{segmentId}";

    // Per-segment effort polylines for the minimap preview (shape/gate segments). Keyed by the caps so a
    // changed budget is a distinct entry; evicted by the owner's change token (InvalidateUser) on
    // analyze/delete/update, same as SegmentGpsSummary.
    public static string SegmentEffortPolylines(string userId, SegmentId segmentId, int maxEfforts, int maxPointsPerEffort) =>
        $"{userId}_seg_eff_polylines_{segmentId}_{maxEfforts}_{maxPointsPerEffort}";

    // --- Activity track keys ---

    public static string TrackCoordinates(string userId, TrackId trackId) =>
        $"{userId}_track_coords_{trackId}_all";

    public static string TrackCoordinatesDownsampled(string userId, TrackId trackId, int maxPoints) =>
        $"{userId}_track_coords_{trackId}_{maxPoints}";

    // Owner-gated coordinates live in their own namespace: a hit is served BEFORE the gate runs, so sharing a
    // key with the visible-gated read would let a trashed track cached by the trash page answer a visible read.
    public static string TrackCoordinatesOwned(string userId, TrackId trackId, int maxPoints) =>
        $"{userId}_track_coords_owned_{trackId}_{maxPoints}";

    public static string ActivitySlices(string userId, IEnumerable<int> trackIds) =>
        $"{userId}_activity_slices:{string.Join(',', trackIds.Order())}:";

    public static string BulkTracks(string userId, IEnumerable<TrackId> trackIds, int maxPointsPerTrack) =>
        $"{userId}_bulk_tracks:{string.Join(',', trackIds.Order())}:{maxPointsPerTrack}";

    // --- Profile settings keys ---

    public static string ProfileSettingsCategory(string category, string? userId) =>
        $"settings_cat:{category}:{userId ?? ""}";

    // --- Multi-day tour keys ---

    public static string AllMultiDayTours(string userId) =>
        $"{userId}_multiday_all";

    // --- Auth keys ---

    /// <summary>
    /// Maps an API-key hash to its owning userId (30 s auth-lookup cache). The <paramref name="generation"/>
    /// prefix (SVC-08) namespaces the entry: a <see cref="ApiKeyCacheGeneration.Bump"/> on revoke/regenerate
    /// moves the generation, so any entry written under the old value — including one a race re-cached for a
    /// just-revoked key — is unreachable.
    /// </summary>
    public static string ApiKeyUser(long generation, string keyHash) =>
        $"apikey:{generation}:{keyHash}";
}
