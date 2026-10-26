using RouteBaseWeb.Auth;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Photos;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Photos;
using RouteBaseWeb.Services.Sharing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace RouteBaseWeb.Api;

/// <summary>
/// Read-only subresources for a share view, mounted UNDER the token: <c>/s/{shareToken}/api/…</c>.
///
/// The token is in the route on every request, which is what keeps the whole slice free of ambient
/// authority — there is no share cookie and no share header, so a grant can never ride along on a request
/// the visitor did not explicitly address to a share URL. It also means an <c>&lt;img src&gt;</c> works
/// with no client-side plumbing: the credential is simply part of the URL, and an image request cannot
/// carry a custom header.
///
/// Every action re-asserts the grant covers the resource. A route parameter is not trusted merely because
/// the token authenticated — that check is what stops a token for activity A from reading activity B.
/// </summary>
[ApiController]
[Route("s/{shareToken}/api")]
[Authorize(AuthenticationSchemes = ShareLinkDefaults.AuthenticationScheme,
           Policy = ShareLinkDefaults.ReadPolicy)]
public class ShareReadController(
    IShareLinkAccess shareAccess,
    IPhotoQueryService photoQuery,
    IActivityQueryService activityQuery) : ControllerBase
{
    /// <summary>
    /// Hard ceiling on how many photos a share view will ever serve for one activity — same
    /// payload-bounding philosophy as <see cref="ShareSegmentReader.MaxTracks"/>. The anonymous
    /// surface takes no page parameter, so this is not just a page size: it is the total.
    /// Capped rather than paged, on purpose.
    /// </summary>
    public const int MaxPhotosPerActivity = 200;

    /// <summary>
    /// GET /s/{token}/api/photos — the shared activity's photos. The trackId is NOT a parameter: it comes
    /// from the grant, so this cannot be pointed at another activity.
    /// </summary>
    [HttpGet("photos")]
    [ProducesResponseType<PhotoListResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Photos(CancellationToken ct)
    {
        ShareResponseHeaders.Apply(Response);

        var grant = await shareAccess.CurrentAsync(ct);
        if (grant is null || grant.Kind != ShareKind.Activity) return NotFound();

        var page = await shareAccess.WidenAsync(
            ShareKind.Activity,
            grant.ResourceId,
            // NeverOwner, because the read below runs as the IMPERSONATED OWNER: left to infer ownership
            // from the ambient user, the DTO would carry the owner's camera model and original filenames
            // (home-directory paths) to an anonymous visitor — strictly more than a signed-in friend gets.
            // async, not a bare expression lambda: Task<T> is invariant, so returning Task<PhotoListResponse>
            // where Task<PhotoListResponse?> is expected warns. Awaiting converts the value instead.
            async () =>
            {
                // Lifecycle gate, evaluated as the impersonated OWNER: VisibleActivities admits an owner's
                // activity only while it is Released — the share PAGE's own 404 rule. Without this, the
                // photo list kept serving after the owner trashed/parked the activity (the photo gates
                // short-circuit on owner identity with no lifecycle clause). The segment side states the
                // same rule in ShareSegmentReader.OwnerEfforts; this is its activity-slice twin.
                if (!await activityQuery.IsActivityVisibleAsync(new TrackId(grant.ResourceId))) return null;

                return await photoQuery.ListAsync(
                    new TrackId(grant.ResourceId), null, null, "captured", 1, MaxPhotosPerActivity,
                    PhotoFieldScope.NeverOwner, ct);
            },
            ct);

        return page is null ? NotFound() : Ok(page);
    }

    /// <summary>
    /// GET /s/{token}/api/photos/{id}/image — one rendition. The photo must belong to the granted
    /// activity; a photo id from any other activity 404s even with a live token.
    /// </summary>
    [HttpGet("photos/{id:int}/image")]
    // Metadata-only 200: a bare [Produces] is a result filter that would force the JSON 400 below
    // to an empty 406.
    [ProducesResponseType(typeof(void), StatusCodes.Status200OK, "image/jpeg")]
    [ProducesResponseType<ShareErrorResponse>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> PhotoImage(
        int id, [FromQuery] string tier = "thumb", CancellationToken ct = default)
    {
        // Applied up front, so the 400/404 paths carry the same hygiene as the success path. A response
        // from a credential-bearing URL must never be shared-cacheable, whatever its status code.
        ShareResponseHeaders.Apply(Response);

        if (!Enum.TryParse<PhotoTier>(tier, ignoreCase: true, out var parsedTier)
            || parsedTier is not (PhotoTier.Thumb or PhotoTier.Preview))
            return BadRequest(new ShareErrorResponse("tier must be thumb or preview."));

        var grant = await shareAccess.CurrentAsync(ct);
        if (grant is null || grant.Kind != ShareKind.Activity) return NotFound();

        var path = await shareAccess.WidenAsync(
            ShareKind.Activity,
            grant.ResourceId,
            async () =>
            {
                // Same lifecycle gate as the list endpoint, and BEFORE any photo lookup: image bytes for a
                // trashed activity must not serve just because the bytes themselves still exist.
                if (!await activityQuery.IsActivityVisibleAsync(new TrackId(grant.ResourceId))) return null;

                // Ownership of the photo BY THE GRANTED ACTIVITY, checked before serving bytes: the grant
                // authorizes one activity, not the photo table. One indexed seek — the previous
                // list-a-page-and-scan form silently 404'd any photo past the page size on a photo-heavy
                // ride, and re-listed 200 rows per image request.
                if (!await photoQuery.IsPhotoInActivityAsync(new PhotoId(id), new TrackId(grant.ResourceId), ct)) return null;

                return await photoQuery.GetServableImagePathAsync(new PhotoId(id), parsedTier, ct);
            },
            ct);

        if (path is null) return NotFound();

        // Not `immutable` like the authenticated endpoint: a share URL must never be held by a shared or
        // CDN cache, and ShareResponseHeaders would fight an immutable directive set here.
        return PhysicalFile(path, "image/jpeg");
    }
}
