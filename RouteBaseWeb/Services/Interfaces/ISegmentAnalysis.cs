using RouteBaseWeb.Models;
namespace RouteBaseWeb.Services.Interfaces;

/// <summary>
/// Segment GPS-matching analysis: the analyze gate, per-segment analyze runs (polyline pipeline or
/// shape matcher), and the "Analyze All" clean-slate wipe. Match-threshold constants live on
/// <see cref="SegmentAnalysis"/>.
/// </summary>
public interface ISegmentAnalysis
{
    /// <summary>Analyze gate: segment visible to the caller, and owned when shape-defined (shape re-analysis rebuilds ALL users' efforts, so only the owner may trigger it).</summary>
    Task<bool> CanAnalyzeSegmentAsync(SegmentId segmentId);
    Task AnalyzeSegmentMatchesAsync(SegmentId segmentId, string? userId = null, int matchThresholdPercent = SegmentAnalysis.DefaultMatchThresholdPercent, CancellationToken ct = default);

    /// <summary>
    /// Incremental per-import match (track-outer / segment-inner): matches one newly-imported track
    /// against every own polyline segment whose padded bbox overlaps it, touching only that track's
    /// rows. Produces the same GPSinSegment rows a full analyze-all would for that track (cand-01).
    /// Caller must have upserted Activity_Bbox first (Seam-2 invariant).
    /// </summary>
    Task MatchNewTrackAsync(TrackId trackId, string? userId = null, int matchThresholdPercent = SegmentAnalysis.DefaultMatchThresholdPercent, CancellationToken ct = default);

    /// <summary>
    /// Batch incremental match for a whole import drain: same per-track semantics as
    /// <see cref="MatchNewTrackAsync"/>, but aggregates candidate-segment bboxes once for the batch and
    /// isolates per-track failures (one bad track can't fail the import). Caller supplies the same
    /// threshold analyze-all uses so the produced rows are equivalent.
    /// </summary>
    Task MatchNewTracksAsync(IReadOnlyList<int> trackIds, string? userId = null, int matchThresholdPercent = SegmentAnalysis.DefaultMatchThresholdPercent, CancellationToken ct = default);

    /// <summary>Remove one track from ALL segments: captures the affected segment ids, deletes every
    /// GPSinSegment row for the track, invalidates per-segment caches and flips affected done-heatmaps
    /// stale. For edited tracks run BEFORE <see cref="MatchNewTrackAsync"/> (a crop can shrink the bbox out
    /// of a previously-matched segment, which incremental re-match would never revisit). Keeps
    /// SegmentEffortExclusion rows (pair-level user intent survives an edit of the same track id).</summary>
    Task<IReadOnlyList<SegmentId>> RemoveTrackFromSegmentsAsync(TrackId trackId, string? userId = null, CancellationToken ct = default);

    /// <summary>
    /// Resolves the effective match threshold (%): <paramref name="explicitPercent"/> if given
    /// (e.g. the Statistics re-analyze button), clamped; else the user's configured ProfileSettings
    /// default (falls back to <see cref="SegmentAnalysis.DefaultMatchThresholdPercent"/>), clamped.
    /// Owns the ProfileSettings key + Min/Max/Default clamp constants (CR5-04 threshold seam).
    /// </summary>
    Task<int> GetEffectiveMatchThresholdAsync(string userId, int? explicitPercent);

    Task ClearAllSegmentMatchesAsync(string? userId = null, CancellationToken ct = default);

    /// <summary>
    /// Deletes own-segment efforts for the user's segments that are NOT in
    /// <paramref name="processedSegmentIds"/>. The "Analyze All" safety-net wipe, run AFTER a clean full
    /// pass (SB-02) instead of before the loop, so a cancel/crash mid-run never zeroes a not-yet-reached
    /// segment's efforts — it covers only the narrow "segment vanished mid-run" case the old upfront wipe
    /// targeted. Processed segments were already delete-then-rebuilt by their own per-segment analyze.
    /// </summary>
    Task ClearOrphanedSegmentMatchesAsync(string userId, IReadOnlyList<SegmentId> processedSegmentIds, CancellationToken ct = default);
}
