namespace RouteBaseWeb.Models.Sharing;

/// <summary>
/// Which affordances an activity detail render offers. Markup gating of the same class as the existing
/// <c>IsOwner</c> checks — NOT a feature flag threaded through core logic.
///
/// It exists because "is this the owner?" and "may this page offer X?" are different questions, and a share
/// view has to answer no to all of them regardless of who is looking. Modelling that as one boolean per
/// affordance keeps the share page from having to know what an owner can do, and keeps the canonical page
/// from having to know a share view exists.
/// </summary>
/// <param name="CanEdit">Owner action bar, track editor entry, version history.</param>
/// <param name="CanCreateSegments">
/// The Create Segment tab and the Custom Segments panel. Deliberately separate from
/// <paramref name="CanEdit"/>: a signed-in NON-owner may create a segment from a friend's ride today, and
/// that behaviour is preserved. It is off for a share view, which also suppresses the
/// <c>/api/SegmentsApi</c> call the panel fires on every page load.
/// </param>
/// <param name="CanAddPhotos">The "Add photos" link, which targets an owner-only import route.</param>
/// <param name="ShowAppNav">
/// In-app chrome that is meaningless to an anonymous viewer: breadcrumbs to /Activities, the
/// "Friend's activity" chip, and the visibility badge (which would otherwise announce "Private" on a
/// deliberately shared private ride).
/// </param>
public sealed record ActivityViewSurfaces(
    bool CanEdit,
    bool CanCreateSegments,
    bool CanAddPhotos,
    bool ShowAppNav)
{
    /// <summary>What the canonical /Activities/Details page offers, given whether the viewer owns it.</summary>
    public static ActivityViewSurfaces ForDetailsPage(bool isOwner) =>
        new(CanEdit: isOwner, CanCreateSegments: true, CanAddPhotos: isOwner, ShowAppNav: true);

    /// <summary>
    /// What the owner sees on a ride that is not released yet (awaiting review, or trashed): the ride itself
    /// and the app chrome, none of the verbs. Editing, segment creation and photo import all assume an
    /// activity in circulation — the review banner's approve / restore / delete are the only meaningful ones,
    /// and they are not surfaces (the banner renders off the lifecycle columns).
    /// </summary>
    public static readonly ActivityViewSurfaces ForOwnerReview =
        new(CanEdit: false, CanCreateSegments: false, CanAddPhotos: false, ShowAppNav: true);

    /// <summary>
    /// What a share view offers: nothing. Hard-coded rather than derived from IsOwner — share-ness is a
    /// property of the ROUTE, not the principal, so an owner opening their own link sees exactly what a
    /// stranger sees.
    /// </summary>
    public static readonly ActivityViewSurfaces ReadOnlyShare =
        new(CanEdit: false, CanCreateSegments: false, CanAddPhotos: false, ShowAppNav: false);
}
