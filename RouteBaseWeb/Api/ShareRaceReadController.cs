using RouteBaseWeb.Auth;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Sharing;
using RouteBaseWeb.Services.Sharing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace RouteBaseWeb.Api;

/// <summary>
/// Read-only race subresource for a share view, mounted UNDER the token:
/// <c>/s/{shareToken}/api/race/…</c>.
///
/// Its own vertical slice, separate from the activity/photo (<see cref="ShareReadController"/>), segment
/// (<see cref="ShareSegmentReadController"/>) and live (<see cref="ShareRecordingReadController"/>)
/// surfaces — removing race sharing should mean deleting files, and it costs the endpoint allowlist
/// exactly one reviewed entry.
///
/// The race id is never a route parameter — it comes from the grant, so <c>Covers</c> is satisfied
/// structurally rather than by an assertion someone has to remember (the same property the segment and live
/// surfaces have).
///
/// One GET, and that is not incidental: <c>ShareEndpointAllowlistTests</c> asserts no share-reachable
/// action carries a mutating verb. The race slice's nine CRUD writes stay hardcoded to <c>/api/RacesApi</c>
/// on the owner page and are never reachable from here.
/// </summary>
[ApiController]
[Route("s/{shareToken}/api/race")]
[Authorize(AuthenticationSchemes = ShareLinkDefaults.AuthenticationScheme,
           Policy = ShareLinkDefaults.ReadPolicy)]
public class ShareRaceReadController(
    IShareLinkAccess shareAccess,
    IShareRaceReader reader) : ControllerBase
{
    /// <summary>
    /// GET /s/{token}/api/race/replay — the whole replay in one body: course grid, checkpoints and the
    /// redacted field.
    ///
    /// One request rather than the owner page's ETag'd FileResult: the anonymous surface has no revalidation
    /// story worth the hand-serialization (no-store is mandatory here — <see cref="ShareResponseHeaders"/>),
    /// and the payload is bounded by the same caps the owner path uses (a shared course grid, ~2,000 points
    /// for the one real ride).
    /// </summary>
    [HttpGet("replay")]
    [ProducesResponseType<ShareRaceReplayPayload>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Replay(CancellationToken ct)
    {
        // Headers first, before ANY early return — an unauthenticated 404 must carry the same token
        // hygiene (no-store, referrer policy, noindex) a rendered page does.
        ShareResponseHeaders.Apply(Response);

        var grant = await shareAccess.CurrentAsync(ct);
        if (grant is null || grant.Kind != ShareKind.Race) return NotFound();

        // NOT WidenAsync: the reader is owner-scoped by construction and takes the grant as data. Widening
        // would run RaceAccess's per-rider re-authorization as the OWNER, whose friend graph would then
        // admit every friend's ride — name and full GPS — to an anonymous stranger. See IShareRaceReader.
        var payload = await reader.GetReplayAsync(grant, ct);
        return payload is null ? NotFound() : Ok(payload);
    }
}
