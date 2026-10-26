using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;

namespace RouteBaseWeb.Services.Segments;

/// <summary>
/// Where a segment view's GATED data comes from. This interface is the seam between the two routes: the
/// canonical page reads through the visibility service, and the share path (phase 2 step 5) will read
/// through an owner-scoped query built from a share grant.
///
/// It exists because the gate is the ONLY thing that differs between the two renders. Everything else —
/// profile settings, the MapTiler key, shape rectangles, surfaces — is identical, so
/// <see cref="SegmentDetailViewBuilder"/> owns all of it and knows nothing about who is asking.
///
/// Returning null from <see cref="GetSegmentAsync"/> means "not visible to this caller", which the builder
/// turns into a 404. Not-yours and not-there deliberately collapse to the same answer.
/// </summary>
public interface ISegmentViewSource
{
    Task<Segment?> GetSegmentAsync(SegmentId segmentId, CancellationToken ct = default);
    Task<List<SegmentEffortDetailed>> GetEffortsAsync(SegmentId segmentId, CancellationToken ct = default);
    Task<SegmentEffortTotals?> GetTotalsAsync(SegmentId segmentId, CancellationToken ct = default);
}

/// <summary>
/// The signed-in route's source: the existing visibility-gated services, unchanged. Every gate that
/// applied before the extraction still applies, in the same place, because this delegates rather than
/// reimplements.
/// </summary>
public sealed class AuthenticatedSegmentViewSource(
    ISegmentCrud segmentCrud,
    ISegmentEfforts segmentEfforts) : ISegmentViewSource
{
    public Task<Segment?> GetSegmentAsync(SegmentId segmentId, CancellationToken ct = default) =>
        segmentCrud.GetSegmentWithGpsAsync(segmentId);

    public Task<List<SegmentEffortDetailed>> GetEffortsAsync(SegmentId segmentId, CancellationToken ct = default) =>
        segmentEfforts.GetSegmentEffortsDetailedAsync(segmentId, ct);

    public Task<SegmentEffortTotals?> GetTotalsAsync(SegmentId segmentId, CancellationToken ct = default) =>
        segmentEfforts.GetPrecomputedEffortTotalsAsync(segmentId, ct);
}
