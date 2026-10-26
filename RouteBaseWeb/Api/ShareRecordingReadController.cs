using RouteBaseWeb.Auth;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Recording;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Sharing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace RouteBaseWeb.Api;

/// <summary>
/// Read-only live-recording subresource for a share view, mounted UNDER the token:
/// <c>/s/{shareToken}/api/recording/…</c>.
///
/// Its own vertical slice, separate from the activity/photo (<see cref="ShareReadController"/>) and segment
/// (<see cref="ShareSegmentReadController"/>) surfaces — removing the live-share feature should mean deleting
/// files, and it costs the endpoint allowlist exactly one reviewed entry.
///
/// The anonymous viewer POLLS this (the SignalR hub is real-user-only by design, and the ShareLink scheme
/// is deliberately kept out of the default selector). Every action is a GET — <c>ShareEndpointAllowlistTests</c>
/// asserts no share-reachable action carries a mutating verb.
///
/// The track id is never a route parameter — it comes from the grant. <c>Covers</c> is satisfied
/// structurally, like the segment surface.
/// </summary>
[ApiController]
[Route("s/{shareToken}/api/recording")]
[Authorize(AuthenticationSchemes = ShareLinkDefaults.AuthenticationScheme,
           Policy = ShareLinkDefaults.ReadPolicy)]
public class ShareRecordingReadController(
    IShareLinkAccess shareAccess,
    IRecordingService recording,
    ILiveViewerRegistry viewers) : ControllerBase
{
    /// <summary>The grant for this request, or null when it is not a live-RECORDING grant.</summary>
    private async Task<ShareGrant?> LiveGrantAsync(CancellationToken ct)
    {
        var grant = await shareAccess.CurrentAsync(ct);
        return grant is null || grant.Kind != ShareKind.LiveRecording ? null : grant;
    }

    /// <summary>
    /// GET /s/{token}/api/recording/live?after={seq} — the one dynamic poll: current status, point count,
    /// and any new GPS points since the cursor. The cursor is the dense staging <c>seq</c>, never a
    /// timestamp (LV-01); -1 (the default) means the whole history.
    ///
    /// Polling also marks the session watched (<see cref="ILiveViewerRegistry.TouchPublic"/>) so the phone
    /// keeps its fast upload cadence while a public link is open.
    /// </summary>
    [HttpGet("live")]
    [ProducesResponseType<ShareLiveUpdateDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Live([FromQuery] long? after, CancellationToken ct)
    {
        // Headers first, before ANY early return — an unauthenticated 404 must carry the same token
        // hygiene (no-store, referrer policy, noindex) a rendered page does.
        ShareResponseHeaders.Apply(Response);

        var grant = await LiveGrantAsync(ct);
        if (grant is null) return NotFound();

        // Lifecycle-gated + owner-pinned resolve (see GetLiveShareSessionAsync): null when the session row
        // is gone (abandoned+deleted), when the Activity shell is not Released (trashed/awaiting-review —
        // the share-READ gate the mint side promises), or when the owner doesn't match the grant's.
        // All indistinguishable from a wrong URL, like every other share miss.
        var session = await recording.GetLiveShareSessionAsync(new TrackId(grant.ResourceId), grant.OwnerUserId, ct);
        if (session is null) return NotFound();

        // Register interest so the recorder uploads at the watched (fast) cadence — but only while the
        // phone still uploads: a terminal session has no cadence to keep fast, and stamping it would only
        // feed the registry entries it must later sweep. Keyed by session id, which the registry and the
        // append response both use.
        if (session.Status is RecordingSessionStatus.Recording or RecordingSessionStatus.Paused)
            viewers.TouchPublic(session.SessionId);

        var points = await recording.GetLivePointsAsync(session, after ?? -1);
        return Ok(new ShareLiveUpdateDto(
            session.Status,
            session.PointCount,
            points.Select(LivePointDto.From).ToList()));
    }
}
