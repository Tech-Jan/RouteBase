using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services.Sharing;

/// <summary>
/// The share path's ONLY segment query surface.
///
/// Purpose-built because identity-widening cannot express "widen the segment, not the efforts":
/// <see cref="SegmentEfforts"/> takes <c>GetCurrentUserId()</c> once and uses it for the segment gate AND
/// the per-effort gate, so under <c>WidenAsync</c> the effort gate widens to the owner's FRIENDS too —
/// turning a public link into a window on the owner's friend graph.
///
/// Every method takes the <see cref="ShareGrant"/> rather than a bare id, so a mismatched
/// owner/segment pair is unconstructible at the call site.
/// </summary>
public interface IShareSegmentReader
{
    /// <summary>The granted segment, or null when the grant no longer matches a segment this owner owns.</summary>
    Task<Segment?> GetSegmentAsync(ShareGrant grant, CancellationToken ct = default);

    /// <summary>The owner's own efforts on the granted segment, best time first.</summary>
    Task<List<SegmentEffortDetailed>> GetOwnerEffortsAsync(ShareGrant grant, CancellationToken ct = default);

    /// <summary>Totals over exactly the efforts <see cref="GetOwnerEffortsAsync"/> returns.</summary>
    Task<SegmentEffortTotals?> GetOwnerEffortTotalsAsync(ShareGrant grant, CancellationToken ct = default);

    /// <summary>Per-point performance data for the requested efforts, INTERSECTED with the permitted set.
    /// Ids outside the set are dropped silently — never 404'd, which would be an existence oracle.</summary>
    Task<List<EffortPerformanceData>> GetPerformanceAsync(
        ShareGrant grant, IReadOnlySet<GpsInSegmentId> effortIds, int maxPointsPerEffort, CancellationToken ct = default);

    /// <summary>
    /// A page of effort tracks, over a server-derived and deterministically ordered set. Takes no
    /// caller-supplied ids at all: with no id parameter this cannot return a foreign track whatever a
    /// future bug does to the filter.
    /// </summary>
    Task<ShareSegmentTracksResult> GetTracksAsync(
        ShareGrant grant, int skip, int take, int maxPointsPerTrack, CancellationToken ct = default);

    /// <summary>One track at full resolution, or null when it is not among the permitted set (→ 404).</summary>
    Task<List<GpsCoordinate>?> GetTrackAsync(
        ShareGrant grant, TrackId trackId, int maxPoints, CancellationToken ct = default);
}

/// <summary>A page of tracks plus the total permitted count, so the client knows when to stop.</summary>
public sealed record ShareSegmentTracksResult(
    IReadOnlyList<(int TrackId, List<GpsCoordinate> Points)> Tracks, int Total);
