using Microsoft.EntityFrameworkCore;
using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services.Segments;

/// <summary>
/// Sums the precomputed per-effort aggregate columns for one segment. Extracted from
/// <see cref="SegmentEfforts"/> so the authenticated page and the share path
/// (<c>Services/Sharing/ShareSegmentReader</c>) run the SAME sum over differently-gated sources —
/// the caller supplies an already-authorized query, this decides only what the numbers mean.
///
/// Note "precomputed" refers to the per-effort columns on GPSinSegment (filled by analysis), not to a
/// stored totals row: this is a live GROUP BY per call. That is why narrowing the source is free.
/// </summary>
public static class SegmentEffortTotalsProjection
{
    /// <summary>
    /// Returns null when the source has no efforts OR any aggregate is still NULL (pre-analyze);
    /// callers pair this with their own effort count to decide between "-" (pending) and nothing
    /// (no efforts). Never loads GPS and never falls back to GPS-derived detailed efforts.
    ///
    /// The Pending rule lives HERE rather than at each call site so the two paths cannot disagree about
    /// when a segment shows "-".
    /// </summary>
    public static async Task<SegmentEffortTotals?> SumAsync(
        IQueryable<GpsInSegment> authorizedEfforts, CancellationToken ct = default)
    {
        var agg = await authorizedEfforts
            .GroupBy(g => g.SegmentId)
            .Select(grp => new
            {
                Count   = grp.Count(),
                Dist    = grp.Sum(x => x.EffortDistance  ?? 0),
                Elev    = grp.Sum(x => x.EffortElevation ?? 0),
                Time    = grp.Sum(x => x.EffortTime      ?? 0),
                Pending = grp.Sum(x => x.EffortDistance == null ? 1 : 0),
            })
            .FirstOrDefaultAsync(ct);

        if (agg == null || agg.Pending > 0) return null; // no efforts, or not yet analysed → caller shows "-"
        return new SegmentEffortTotals
        {
            EffortCount    = agg.Count,
            TotalDistance  = agg.Dist,
            TotalElevation = agg.Elev,
            TotalTime      = agg.Time,
        };
    }
}
