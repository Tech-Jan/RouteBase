namespace RouteBaseWeb.Models.Sharing;

/// <summary>
/// Which affordances a segment statistics render offers. The segment-side twin of
/// <see cref="ActivityViewSurfaces"/> — markup gating of the same class as the existing <c>IsOwner</c>
/// checks, NOT a feature flag threaded through core logic.
///
/// Note two of these are NOT owner questions at all: <paramref name="ShowHeatmap"/> and
/// <paramref name="ShowRiderIdentity"/> are true for every signed-in viewer and false only on a share
/// render. That is precisely why they must be surfaces rather than an <c>if (isShare)</c> in the markup —
/// the canonical page must not know a share view exists.
/// </summary>
/// <param name="CanManageSegment">
/// The whole owner panel: the match-threshold input and its reset link, the shape-editor link, Re-analyze,
/// Delete — and the visibility select. Grouped as one surface because they share a container and a reason.
/// </param>
/// <param name="CanGenerateHeatmap">The Generate/Regenerate button and the "stale" hint beside it.</param>
/// <param name="ShowHeatmap">
/// The heatmap map-view menu items, and the <c>hasHeatmap</c>/<c>heatmapStatus</c> config keys. Off for a
/// share render, which is what stops heatmap-layer.ts from starting its 2s x 300 poll: with neither key
/// serialized, the controls compute status 'none' and never poll.
/// </param>
/// <param name="CanExcludeEfforts">The efforts grid's Excluded + actions columns and the remove/restore
/// delegation. Client-rendered, so no server-side render guard can see these — pinned by vitest instead.</param>
/// <param name="ShowRiderIdentity">
/// The Rider column, the "Me" badge, and <c>userDisplayName</c> on the serialized effort rows. Off for a
/// share render: the owner's account display name is not part of what a link publishes.
/// </param>
/// <param name="CanLinkToActivities">
/// The per-effort link to /Activities/Details. Off for a share render, where it would be a dead link into
/// the login flow — and where the share body must render zero hrefs.
/// </param>
/// <param name="CanShare">The mint button and its dialog.</param>
/// <param name="ShowAppNav">In-app chrome meaningless to an anonymous viewer: the breadcrumb to /Segments.</param>
public sealed record SegmentViewSurfaces(
    bool CanManageSegment,
    bool CanGenerateHeatmap,
    bool ShowHeatmap,
    bool CanExcludeEfforts,
    bool ShowRiderIdentity,
    bool CanLinkToActivities,
    bool CanShare,
    bool ShowAppNav)
{
    /// <summary>What the canonical /Segments/Statistics page offers, given whether the viewer owns it.</summary>
    public static SegmentViewSurfaces ForStatisticsPage(bool isOwner) =>
        new(CanManageSegment: isOwner,
            CanGenerateHeatmap: isOwner,
            ShowHeatmap: true,
            CanExcludeEfforts: isOwner,
            ShowRiderIdentity: true,
            CanLinkToActivities: true,
            CanShare: isOwner,
            ShowAppNav: true);

    /// <summary>
    /// What a share view offers: nothing. Hard-coded rather than derived from IsOwner — share-ness is a
    /// property of the ROUTE, not the principal, so an owner opening their own link sees exactly what a
    /// stranger sees. SegmentViewSurfacesTests asserts by reflection that EVERY member here is false, so a
    /// field added later cannot default to true and quietly open a surface.
    /// </summary>
    public static readonly SegmentViewSurfaces ReadOnlyShare =
        new(CanManageSegment: false,
            CanGenerateHeatmap: false,
            ShowHeatmap: false,
            CanExcludeEfforts: false,
            ShowRiderIdentity: false,
            CanLinkToActivities: false,
            CanShare: false,
            ShowAppNav: false);
}
