using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Tracks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RouteBaseWeb.Services.Lifecycle;

namespace RouteBaseWeb.Api;

/// <summary>
/// AJAX backend for the merged Record page's pending-review controls (replaces the deleted /Review
/// Razor handlers). Owner-scoped throughout — you only ever see and act on your own pending rides.
/// </summary>
[ApiController]
[Authorize]
[Route("api/review")]
public class ReviewApiController : ControllerBase
{
    private readonly IAutoReviewService _review;
    private readonly IVisibilityService _visibility;
    private readonly RouteBaseDbContext _context;
    private readonly ITrackReader _reader;
    private readonly ILogger<ReviewApiController> _logger;

    // ~200 points is plenty for a thumbnail polyline; keeps a long recording's payload small.
    private const int MinimapMaxPoints = 200;

    // Input hygiene (RF-09): matches the max ids a single import batch can produce; 40 chars ≫ any real activity type.
    private const int MaxTrackIds = 5000;
    private const int MaxActivityTypeLength = 40;

    public ReviewApiController(
        IAutoReviewService review,
        IVisibilityService visibility,
        RouteBaseDbContext context,
        ITrackReader reader,
        ILogger<ReviewApiController> logger)
    {
        _review     = review;
        _visibility = visibility;
        _context    = context;
        _reader     = reader;
        _logger     = logger;
    }

    // GET /api/review/{trackId}/gps — owner-scoped coords for a pending/trashed-row minimap.
    [HttpGet("{trackId:int}/gps")]
    [ProducesResponseType<List<LatLngDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPendingGps(int trackId, CancellationToken ct)
    {
        // Owner-scoped, awaiting-review or trashed. Bypasses the VisibleActivities 'released' gate that makes
        // the normal /api/activities/{id}/gps endpoint return empty for these tracks. 404 otherwise.
        var isOwnedReviewable = await _visibility.OwnedActivities(_context.Activities)
            .AnyAsync(a => a.TrackId == new TrackId(trackId) && a.Source == ActivitySources.AutoRecording
                && (a.Lifecycle == LifecycleStates.InWork || a.Lifecycle == LifecycleStates.Trashed), ct);
        if (!isOwnedReviewable)
            return NotFound();

        var track = await _reader.GetTrackAsync(new TrackId(trackId), ct);
        if (track == null)
            return Ok(Array.Empty<LatLngDto>());

        return Ok(DownsampleLatLng(track, MinimapMaxPoints));
    }

    // POST /api/review/approve — { trackIds:int[], activityType?:string }. Single id + type retypes;
    // otherwise bulk confirm as-is.
    [HttpPost("approve")]
    [ProducesResponseType<ConfirmResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Approve([FromBody] ApproveRequest req)
    {
        var ids = req.TrackIds ?? Array.Empty<int>();
        if (ids.Count == 0)
            return Ok(new ConfirmResponse(0));
        if (ids.Count > MaxTrackIds)
            return BadRequest(ApiError.BadRequest($"Too many ids: max {MaxTrackIds}"));
        if (req.ActivityType is { Length: > MaxActivityTypeLength })
            return BadRequest(ApiError.BadRequest($"Activity type too long: max {MaxActivityTypeLength} chars"));

        int confirmed = ids.Count == 1 && !string.IsNullOrWhiteSpace(req.ActivityType)
            ? (await _review.ConfirmAsync(new TrackId(ids[0]), req.ActivityType) ? 1 : 0)
            : await _review.ConfirmManyAsync(ids.Select(i => new TrackId(i)).ToList());

        return Ok(new ConfirmResponse(confirmed));
    }

    // POST /api/review/trash — { trackIds:int[] }. Soft-trash: trashed/review-rejected + 2-day purge window,
    // restorable meanwhile. (Immediate hard delete is the separate DELETE /api/recording/sessions/{id}.)
    [HttpPost("trash")]
    [ProducesResponseType<TrashCountResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Trash([FromBody] TrashRequest req)
    {
        var ids = req.TrackIds ?? Array.Empty<int>();
        if (ids.Count > MaxTrackIds)
            return BadRequest(ApiError.BadRequest($"Too many ids: max {MaxTrackIds}"));
        var trashed = await _review.TrashManyAsync(ids.Select(i => new TrackId(i)).ToList());
        return Ok(new TrashCountResponse(trashed));
    }

    // POST /api/review/restore — { trackIds:int[] }. Undo a trash before the purge reclaims it.
    [HttpPost("restore")]
    [ProducesResponseType<RestoreResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Restore([FromBody] RestoreRequest req)
    {
        var ids = req.TrackIds ?? Array.Empty<int>();
        if (ids.Count > MaxTrackIds)
            return BadRequest(ApiError.BadRequest($"Too many ids: max {MaxTrackIds}"));
        var restored = await _review.RestoreManyAsync(ids.Select(i => new TrackId(i)).ToList());
        return Ok(new RestoreResponse(restored));
    }

    // Non-NULL coords only, strided to <= maxPoints — the same subsequence rule the map overlays use.
    // Internal for the budget pin (InternalsVisibleTo RouteBaseWeb.Tests).
    internal static List<LatLngDto> DownsampleLatLng(DecodedTrack track, int maxPoints)
    {
        var result = new List<LatLngDto>();
        int cnt = 0;
        for (int i = 0; i < track.Count; i++)
            if (!double.IsNaN(track.Lat[i]) && !double.IsNaN(track.Lng[i])) cnt++;
        if (cnt == 0) return result;

        // Ceiling, not floor: floor returned up to ~2× the budget (399 valid points → stride 1 → all 399).
        int stride = (cnt + maxPoints - 1) / maxPoints;
        int k = 0;
        for (int i = 0; i < track.Count; i++)
        {
            double la = track.Lat[i], ln = track.Lng[i];
            if (double.IsNaN(la) || double.IsNaN(ln)) continue;
            if (k % stride == 0) result.Add(new LatLngDto(la, ln));
            k++;
        }
        return result;
    }
}

/// <summary>Approve one (with optional retype) or many pending rides.</summary>
public record ApproveRequest(IReadOnlyList<int>? TrackIds, string? ActivityType);

/// <summary>Soft-trash pending rides (restorable until the 2-day purge).</summary>
public record TrashRequest(IReadOnlyList<int>? TrackIds);

/// <summary>Restore trashed rides back to pending.</summary>
public record RestoreRequest(IReadOnlyList<int>? TrackIds);
