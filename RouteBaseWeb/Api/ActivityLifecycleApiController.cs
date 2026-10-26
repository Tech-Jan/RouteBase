using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Lifecycle;
using RouteBaseWeb.Services.TrackEdit;
using RouteBaseWeb.Services.Tracks;
using Microsoft.AspNetCore.Mvc;

namespace RouteBaseWeb.Api;

/// <summary>
/// The ContentLifecycle slice's endpoints: where a ride sits (park/release) and what it used to look like
/// (versions/restore/reset). Its own controller, on the SAME <c>api/activities</c> route prefix — so the
/// routes and every client are unchanged, but the slice is now a vertical one: removing it means deleting
/// files, not editing the shared activities controller. Attribute routing means two controllers may share a
/// prefix as long as no template collides, and none does.
/// </summary>
[ApiController]
[Route("api/activities")]
public class ActivityLifecycleApiController : ControllerBase
{
    private readonly IActivityLifecycleService _lifecycle;
    private readonly ITrackVersionHistory _versions;
    private readonly ILogger<ActivityLifecycleApiController> _logger;

    public ActivityLifecycleApiController(
        IActivityLifecycleService lifecycle,
        ITrackVersionHistory versions,
        ILogger<ActivityLifecycleApiController> logger)
    {
        _lifecycle = lifecycle;
        _versions  = versions;
        _logger    = logger;
    }

    /// <summary>
    /// Park: take a released ride OUT of circulation without deleting it — gone from lists, stats, maps and
    /// segments, still entirely intact. The state a ride sits in while you are unsure about it, which the trash
    /// is the wrong home for (it isn't deleted) and hiding it isn't either (that is what visibility means).
    /// </summary>
    [HttpPost("{id}/park")]
    [RejectCrossSiteBrowserRequests] // route-only POSTs here: cross-site form/fetch-able without preflight (PI-01)
    [ProducesResponseType<LifecycleTransitionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<TrackEditErrorBody>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<TrackEditErrorBody>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Park(int id, CancellationToken ct)
    {
        var result = await _lifecycle.TransitionAsync(
            new TrackId(id), LifecycleStates.InWork, LifecycleReasons.User, ct: ct);
        return result.Ok ? Ok(new LifecycleTransitionResponse(result.To)) : Fail(result);
    }

    /// <summary>Put a parked ride back into circulation.</summary>
    [HttpPost("{id}/release")]
    [RejectCrossSiteBrowserRequests]
    [ProducesResponseType<LifecycleTransitionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<TrackEditErrorBody>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<TrackEditErrorBody>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Release(int id, CancellationToken ct)
    {
        var result = await _lifecycle.TransitionAsync(
            new TrackId(id), LifecycleStates.Released, LifecycleReasons.User, ct: ct);
        return result.Ok ? Ok(new LifecycleTransitionResponse(result.To)) : Fail(result);
    }

    // Every refusal answers with the SAME named DTO the rest of the edit API uses. It was an anonymous
    // `new { error = … }` here, whose field the client never reads (it parses `code`) — so every refusal
    // arrived as `httpError` and the dialog fell back to "Could not restore this version", hiding the one
    // thing the user needed to be told: the ride has an edit open. Anonymous body, unreadable contract.
    private IActionResult Fail(LifecycleResult result) =>
        result.Error == NotFoundCode
            ? NotFoundBody()
            : Conflict(new TrackEditErrorBody(result.Error ?? throw new InvalidOperationException("a failed result carries an error code")));

    private const string NotFoundCode = "notFound";
    private IActionResult NotFoundBody() => NotFound(new TrackEditErrorBody(NotFoundCode));

    /// <summary>
    /// Every state this ride has ever been in. Owner-only: history is not a public fact about a ride, and a
    /// friend viewing your activity has no business reading what it looked like before you cropped it.
    /// </summary>
    [HttpGet("{id:int}/versions")]
    [ProducesResponseType<VersionsListResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<TrackEditErrorBody>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Versions(int id, CancellationToken ct)
    {
        if (!await _versions.OwnsAsync(new TrackId(id), ct)) return NotFoundBody();
        return Ok(new VersionsListResponse(await _versions.ListAsync(new TrackId(id), ct)));
    }

    /// <summary>
    /// Make an earlier version current again. Appends to history rather than rewriting it, and costs zero new
    /// bytes — the content it points at is already stored under its own hash.
    /// </summary>
    [HttpPost("{id:int}/versions/{versionId:int}/restore")]
    [RejectCrossSiteBrowserRequests]
    [ProducesResponseType<RestoreVersionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<TrackEditErrorBody>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<TrackEditErrorBody>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RestoreVersion(int id, int versionId, CancellationToken ct)
    {
        if (!await _versions.OwnsAsync(new TrackId(id), ct)) return NotFoundBody();
        switch (await _versions.RestoreAsync(new TrackId(id), new VersionId(versionId), ct))
        {
            case NotFoundCode:
                return NotFoundBody();
            case TrackEditApiErrors.StillRecording:
                return Conflict(new TrackEditErrorBody(TrackEditApiErrors.StillRecording));
            // Refused, not queued: an open editor stamped the head this would move.
            case TrackEditApiErrors.AlreadyCheckedOut:
                return Conflict(new TrackEditErrorBody(TrackEditApiErrors.AlreadyCheckedOut));
        }

        _logger.LogInformation("Activity {TrackId}: restored version {VersionId}", id, versionId);
        return Ok(new RestoreVersionResponse(versionId));
    }

    /// <summary>
    /// Put the ride back the way it started: the points it began with, and no edit history. Discards every
    /// edit ever made to it, permanently — the only destructive call on an otherwise append-only record, so it
    /// is never implicit and never a side effect of anything else.
    /// </summary>
    [HttpPost("{id:int}/versions/reset")]
    [RejectCrossSiteBrowserRequests]
    [ProducesResponseType<ResetResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<TrackEditErrorBody>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<TrackEditErrorBody>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ResetToOriginal(int id, CancellationToken ct)
    {
        if (!await _versions.OwnsAsync(new TrackId(id), ct)) return NotFoundBody();
        switch (await _versions.ResetToOriginalAsync(new TrackId(id), ct))
        {
            case NotFoundCode:
                return NotFoundBody();
            case TrackEditApiErrors.StillRecording:
                return Conflict(new TrackEditErrorBody(TrackEditApiErrors.StillRecording));
            // Refused, not queued: an open editor stamped the head this would move.
            case TrackEditApiErrors.AlreadyCheckedOut:
                return Conflict(new TrackEditErrorBody(TrackEditApiErrors.AlreadyCheckedOut));
        }

        _logger.LogInformation("Activity {TrackId}: reset to its original track", id);
        return Ok(new ResetResponse(true));
    }
}
