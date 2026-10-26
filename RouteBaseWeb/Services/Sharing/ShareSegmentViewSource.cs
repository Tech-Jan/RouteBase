using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Segments;

namespace RouteBaseWeb.Services.Sharing;

/// <summary>
/// The share route's <see cref="ISegmentViewSource"/>: every gated read goes through
/// <see cref="IShareSegmentReader"/> under one grant.
///
/// This is the entire difference between the two segment renders. <see cref="SegmentDetailViewBuilder"/>
/// is untouched by the share feature — it never learned that share links exist — because the only thing
/// that varies is where the segment, the efforts and the totals come from.
///
/// <see cref="Bind"/> rather than constructor-injecting the grant: the source is resolved from DI (scoped)
/// but the grant is only known after the page has authenticated the token.
/// </summary>
public sealed class ShareSegmentViewSource(IShareSegmentReader reader) : ISegmentViewSource
{
    private ShareGrant? _grant;

    /// <summary>Binds this source to the request's grant. Must be called before any read.</summary>
    public void Bind(ShareGrant grant) => _grant = grant;

    private ShareGrant Grant => _grant
        ?? throw new InvalidOperationException(
            "ShareSegmentViewSource used before Bind(grant). Reading without a grant would have no "
            + "authority at all, so this throws rather than defaulting to anything.");

    public Task<Segment?> GetSegmentAsync(SegmentId segmentId, CancellationToken ct = default) =>
        reader.GetSegmentAsync(Grant, ct);

    public Task<List<SegmentEffortDetailed>> GetEffortsAsync(SegmentId segmentId, CancellationToken ct = default) =>
        reader.GetOwnerEffortsAsync(Grant, ct);

    public Task<SegmentEffortTotals?> GetTotalsAsync(SegmentId segmentId, CancellationToken ct = default) =>
        reader.GetOwnerEffortTotalsAsync(Grant, ct);
}
