using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services.Segments;

/// <summary>
/// Rebuilds the <c>GPSinSegment</c> efforts for shape-defined segments.
///
/// The caller (the analysis pipeline) owns scope/visibility resolution and has already
/// deleted the stale efforts before calling this — the strategy only computes fresh
/// matches for the given segments against the given user's activities and inserts them.
///
/// This is the clean seam that keeps shape matching out of the polyline pipeline.
/// </summary>
public interface ISegmentMatchStrategy
{
    /// <param name="segmentIds">Shape-kind segment ids to (re)match.</param>
    /// <param name="userId">The current user — always allowed to match their own tracks.</param>
    /// <param name="friendIds">
    /// The current accepted-friend ids, used to re-authorise each segment's stored owner scope against the
    /// friends-visibility rule on every run (so a revoked friendship drops those tracks).
    /// </param>
    Task MatchAsync(
        IReadOnlyList<SegmentId> segmentIds, string userId, IReadOnlyList<string> friendIds, CancellationToken ct = default);

    /// <summary>
    /// Track-restricted twin of <see cref="MatchAsync"/> for the incremental path (a new import, a
    /// finalized recording, an approved auto-recording): re-match ONE track against the given shape
    /// segments, touching only that track's rows.
    ///
    /// <see cref="MatchAsync"/> cannot serve this — it rebuilds a whole segment from every visible owner's
    /// tracks, so calling it per-import would wipe and re-insert every other track's efforts (and every
    /// other user's) on each ride. This is the shape-side mirror of the polyline pipeline's
    /// track-restricted adapters, and the same rule document, owner scope and visibility re-authorisation
    /// still decide the outcome.
    /// </summary>
    /// <returns>The segments whose rows for THIS track actually changed (deleted, inserted, or both) —
    /// never the input list. The caller gates its per-segment aggregate + heatmap-stale work on this
    /// (SD-01), which is what stops one match from thrashing every badge the user owns. Empty = the track
    /// joined nothing and nothing was removed, so there is no follow-up work at all.</returns>
    Task<IReadOnlyList<SegmentId>> MatchTrackAsync(
        TrackId trackId, IReadOnlyList<SegmentId> segmentIds, string userId, IReadOnlyList<string> friendIds,
        CancellationToken ct = default);
}
