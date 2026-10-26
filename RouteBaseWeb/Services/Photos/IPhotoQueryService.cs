using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Photos;

namespace RouteBaseWeb.Services.Photos;

/// <summary>
/// Read-model composition for photos: listing/grid (owner-trimmed DTOs, activity-grouping joins),
/// overview map markers, and view-gated rendition serving. Cross-context Activity EF reads (sort
/// subquery, header lookup) live here — the sanctioned rule is that such reads compose inside a query
/// service, never in a controller (mirrors <see cref="Interfaces.IActivityQueryService.GetPhotoCountsAsync"/>).
/// </summary>
public interface IPhotoQueryService
{
    /// <summary>One page of photos. With <paramref name="trackId"/>: that activity's photos when the
    /// caller may view it (else an empty page); without it: the caller's own photos. Non-owners get the
    /// AS-05 trimmed DTO shape.
    ///
    /// Date filtering is by the photo's OWN local calendar day — the day a human would say the photo
    /// was taken, derived per row as <c>CapturedUtc + TzOffset</c> (null offset behaves as UTC). Both
    /// bounds are inclusive; a UTC-column comparison would shift photos taken near midnight into the
    /// wrong day for any non-zero offset. This is the one statement of the rule (referenced from
    /// <see cref="Helpers.DateTimeIso"/>); photos without a capture time never match an active filter.
    ///
    /// <paramref name="scope"/> has no default on purpose: a caller reading under an impersonated identity
    /// must say so, because the per-row owner test cannot tell impersonation from the real thing.</summary>
    Task<PhotoListResponse> ListAsync(
        TrackId? trackId, DateOnly? from, DateOnly? to, string sort, int page, int pageSize,
        PhotoFieldScope scope, CancellationToken ct = default);

    /// <summary>True when <paramref name="photoId"/> belongs to <paramref name="trackId"/>. The containment
    /// check behind share-scoped rendition serving: one indexed seek, and unlike listing a page of photos
    /// it cannot silently answer "no" for a photo that sits past the page size.</summary>
    Task<bool> IsPhotoInActivityAsync(PhotoId photoId, TrackId trackId, CancellationToken ct = default);

    /// <summary>The caller's own placed photos as slim markers for the Activities overview overlay.</summary>
    Task<PhotoMarkersResponse> GetMapMarkersAsync(CancellationToken ct = default);

    /// <summary>Absolute path of a servable rendition, or <c>null</c> when the photo is missing, not
    /// viewable, or its file is absent — every null case is a 404 to the caller.</summary>
    Task<string?> GetServableImagePathAsync(PhotoId photoId, PhotoTier tier, CancellationToken ct = default);
}
