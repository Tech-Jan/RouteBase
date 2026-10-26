using RouteBaseWeb.Services.Tracks;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Lifecycle;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace RouteBaseWeb.Api;

/// <summary>Bodies for the trash's bulk actions.</summary>
public sealed record TrackIdsRequest(List<int>? TrackIds);
public sealed record PurgeRequest(List<int>? TrackIds, bool ConfirmMergeSources = false);

/// <summary>
/// The trash: list · restore · delete permanently · empty. Owner-scoped throughout — you only ever see and
/// act on your own rows.
///
/// The one rule with teeth here is invariant 4: purging a merge's SOURCE while the merged ride still exists
/// destroys the ability to ever un-merge it. That is allowed — but only as an informed choice, so the purge
/// endpoints refuse until the caller has been told which rows those are and says yes.
/// </summary>
[ApiController]
[Authorize]
[Route("api/trash")]
public class TrashApiController : ControllerBase
{
    private readonly TrashQueryService _trash;
    private readonly IActivityLifecycleService _lifecycle;
    private readonly IActivityCommandService _activities;
    private readonly IActivityGpsService _gps;
    private readonly IVisibilityService _visibility;
    private readonly ILogger<TrashApiController> _logger;

    // Matches the review slice's cap: far more ids than any real trash, but the bound is what stops a
    // hand-rolled request from turning into an unbounded IN (...) over the DB.
    private const int MaxTrackIds = 5000;

    public TrashApiController(
        TrashQueryService trash,
        IActivityLifecycleService lifecycle,
        IActivityCommandService activities,
        IActivityGpsService gps,
        IVisibilityService visibility,
        ILogger<TrashApiController> logger)
    {
        _trash      = trash;
        _lifecycle  = lifecycle;
        _activities = activities;
        _gps        = gps;
        _visibility = visibility;
        _logger     = logger;
    }

    // GET /api/trash — everything in the user's trash.
    [HttpGet]
    [ProducesResponseType<TrashListResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(new TrashListResponse(await _trash.ListAsync(ct)));

    // GET /api/trash/{trackId}/gps-summary — the minimap shape for a trashed ride.
    //
    // The activities endpoint of the same name can't serve these: its read is visible-gated (released only),
    // which is exactly the point of that gate. So the trash slice serves its own rows, gating on owned AND
    // trashed — the state check the owner-gated GPS read deliberately leaves to its caller.
    [HttpGet("{trackId:int}/gps-summary")]
    [ProducesResponseType<List<LatLngDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GpsSummary(int trackId, CancellationToken ct)
    {
        if (!await _trash.IsTrashedAsync(new TrackId(trackId), ct))
            return NotFound(ApiError.NotFound("Not in your trash"));

        const int maxPoints = 50; // matches the dashboard minimap
        var coords = await _gps.GetOwnedTrackCoordinatesAsync(new TrackId(trackId), maxPoints, ct);
        return Ok(coords.Select(c =>
            new LatLngDto(GpsPrecision.Coord(c.Lat), GpsPrecision.Coord(c.Lng))).ToList());
    }

    // POST /api/trash/restore — { trackIds:int[] }. Each ride returns to its lifecycle_prev.
    [HttpPost("restore")]
    [ProducesResponseType<RestoreResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Restore([FromBody] TrackIdsRequest request, CancellationToken ct)
    {
        var ids = Validate(request?.TrackIds);
        if (ids is null) return BadRequest(ApiError.BadRequest("trackIds required (max 5000)"));

        // Batch seam (P7): up to 5000 ids used to mean 5000 loads + saves; now one of each.
        int restored = (await _lifecycle.RestoreManyAsync(ids.Select(i => new TrackId(i)).ToList(), ct)).Moved;

        _logger.LogInformation("Trash: restored {Count} activities", restored);
        return Ok(new RestoreResponse(restored));
    }

    // POST /api/trash/purge — { trackIds:int[], confirmMergeSources:bool }. Permanent.
    [HttpPost("purge")]
    [ProducesResponseType<PurgeResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<MergeSourcesConfirmBody>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Purge([FromBody] PurgeRequest request, CancellationToken ct)
    {
        if (request is null || Validate(request.TrackIds) is not { } ids)
            return BadRequest(ApiError.BadRequest("trackIds required (max 5000)"));

        return await PurgeAsync(ids, request.ConfirmMergeSources, ct);
    }

    // POST /api/trash/empty — { confirmMergeSources:bool }. Purges everything in the trash.
    [HttpPost("empty")]
    [RejectCrossSiteBrowserRequests] // body is OPTIONAL — an empty cross-site POST binds null and executes (PI-01)
    [ProducesResponseType<PurgeResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<MergeSourcesConfirmBody>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Empty([FromBody] PurgeRequest? request, CancellationToken ct)
    {
        var ids = await _trash.TrashedIdsAsync(ct);
        if (ids.Count == 0) return Ok(new PurgeResponse(0));

        return await PurgeAsync(ids.Select(t => t.Value).ToList(), request?.ConfirmMergeSources ?? false, ct);
    }

    /// <summary>
    /// The shared purge path. Refuses with 409 + the offending ids until the caller confirms, so "empty trash"
    /// can never quietly take a merge's un-doability with it. The UI turns that 409 into the named warning.
    /// </summary>
    private async Task<IActionResult> PurgeAsync(List<int> ids, bool confirmed, CancellationToken ct)
    {
        var mergeSources = await _trash.MergeSourcesAmongAsync(ids.Select(i => new TrackId(i)).ToList(), ct);
        if (mergeSources.Count > 0 && !confirmed)
            return Conflict(new MergeSourcesConfirmBody(
                Message: mergeSources.Count == 1
                    ? "1 of these rides was merged into another. Deleting it permanently means that merge can never be undone."
                    : $"{mergeSources.Count} of these rides were merged into others. Deleting them permanently means those merges can never be undone.",
                MergeSourceIds: mergeSources.Select(t => t.Value).ToList()));

        // DeleteActivityCoreAsync is the permanent purge — child rows, content objects (by refcount), photo
        // renditions. Scope to rows that are actually in THIS user's trash; the owner id also rides along
        // as the core's own cross-owner assertion.
        var trashed = (await _trash.TrashedIdsAmongAsync(ids.Select(i => new TrackId(i)).ToList(), ct)).ToHashSet();
        var ownerId = _visibility.GetCurrentUserId();

        // requireTrashed: the snapshot above goes stale over this loop — a ride restored from a second
        // tab after the snapshot must be re-caught inside the delete's own transaction, not purged.
        int purged = 0;
        foreach (int id in ids)
            if (trashed.Contains(new TrackId(id)) && await _activities.DeleteActivityCoreAsync(new TrackId(id), ownerId, requireTrashed: true))
                purged++;

        _logger.LogInformation("Trash: purged {Count} activities permanently", purged);
        return Ok(new PurgeResponse(purged));
    }

    private static List<int>? Validate(List<int>? ids) =>
        ids is null || ids.Count == 0 || ids.Count > MaxTrackIds ? null : ids.Distinct().ToList();
}
