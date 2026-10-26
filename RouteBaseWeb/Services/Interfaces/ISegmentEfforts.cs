using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services.Interfaces;

/// <summary>
/// Segment effort read-model: detailed effort summaries, aggregate totals, effort counts, per-point
/// performance chart data, and single-effort removal. Reads precomputed GPSinSegment aggregates
/// (filled by analysis) and slices GPS windows for the chart.
/// </summary>
public interface ISegmentEfforts
{
    Task<List<SegmentEffortDetailed>> GetSegmentEffortsDetailedAsync(SegmentId segmentId, CancellationToken ct = default);
    Task<Dictionary<SegmentId, int>> GetEffortCountsForSegmentsAsync(List<SegmentId> segmentIds);

    /// <summary>
    /// Aggregate effort totals (distance, elevation gain, elapsed time, count) per segment across
    /// ALL efforts — unfiltered. Used by the segment cards' "/ Total" readouts. Per-segment cached.
    /// </summary>
    Task<Dictionary<SegmentId, SegmentEffortTotals>> GetEffortTotalsForSegmentsAsync(List<SegmentId> segmentIds, CancellationToken ct = default);

    /// <summary>
    /// Precomputed effort totals for ONE segment, read straight from the per-effort aggregates (no GPS,
    /// no detailed-effort fallback) so the Statistics "/ Total" matches the segment card. Null when the
    /// segment has no visible efforts or its aggregates are still NULL (pending analysis → caller shows "-").
    /// </summary>
    Task<SegmentEffortTotals?> GetPrecomputedEffortTotalsAsync(SegmentId segmentId, CancellationToken ct = default);

    /// <summary>
    /// Durably excludes an effort from totals/counts/charts/heatmap paint (item 25) by inserting a
    /// SegmentEffortExclusion the read consumers anti-join out — survives re-analysis. Owner-only
    /// (returns false / 404 for non-owners). The GPSinSegment row is left in place. Idempotent.
    /// </summary>
    Task<bool> RemoveEffortAsync(GpsInSegmentId effortId);

    /// <summary>
    /// Restores a previously excluded effort by deleting its SegmentEffortExclusion row (item 25).
    /// Owner-only (returns false / 404 for non-owners). Idempotent.
    /// </summary>
    Task<bool> RestoreEffortAsync(GpsInSegmentId effortId);

    /// <summary>
    /// Performance data for a subset of efforts (by GPSinSegment id), used by the chart's chunked
    /// progressive loader. Loads GPS for only the requested efforts and caches each separately, so the
    /// working set stays bounded to one chunk. When <paramref name="maxPointsPerEffort"/> &gt; 0, each
    /// effort's points are stride-downsampled to that budget (endpoints pinned) and
    /// <see cref="EffortPerformanceData.PointsTruncated"/> is set; 0 = full resolution.
    /// </summary>
    Task<List<EffortPerformanceData>> GetPerformanceDataForEffortsAsync(SegmentId segmentId, IReadOnlySet<GpsInSegmentId> effortIds, int maxPointsPerEffort = 0, CancellationToken ct = default);

    /// <summary>
    /// Every visible, non-excluded matched effort's simplified lat/lng track through a shape/gate
    /// segment — the list minimap preview (shape segments have no Segment_GPS to draw). Slices each
    /// effort out of its decoded activity track (decode-once-per-track LRU), downsamples to
    /// <paramref name="maxPointsPerEffort"/>, and caps the effort count at <paramref name="maxEfforts"/>
    /// (<see cref="EffortsSummaryDto.Truncated"/> flags the cut). Result is memory-cached and evicted on
    /// analyze/delete via the owner's change token.
    /// </summary>
    Task<EffortsSummaryDto> GetEffortPolylinesAsync(SegmentId segmentId, int maxEfforts, int maxPointsPerEffort, CancellationToken ct = default);
}
