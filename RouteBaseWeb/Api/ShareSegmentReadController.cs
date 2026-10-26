using System.Globalization;
using RouteBaseWeb.Auth;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Sharing;
using RouteBaseWeb.Services.Sharing;
using RouteBaseWeb.Services.Tracks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace RouteBaseWeb.Api;

/// <summary>
/// Read-only segment subresources for a share view, mounted UNDER the token:
/// <c>/s/{shareToken}/api/segment/…</c>.
///
/// A separate controller from <see cref="ShareReadController"/> on purpose: that one is the activity/photo
/// slice, and the segment share surface is its own vertical slice — removing the feature should mean
/// deleting files. It also means the endpoint allowlist gains exactly one reviewed entry, which is the
/// intended gate rather than a cost to avoid.
///
/// The segment id is never a route parameter — it comes from the grant. There is therefore no id to
/// re-assert here, and <c>Covers</c> is satisfied structurally, which is a stronger property than the
/// photo endpoints have.
///
/// Every action is a GET. That is not incidental: <c>ShareEndpointAllowlistTests</c> asserts no
/// share-reachable action carries a mutating verb, and it is why these are query-string endpoints rather
/// than mirrors of the authenticated POST bodies.
/// </summary>
[ApiController]
[Route("s/{shareToken}/api/segment")]
[Authorize(AuthenticationSchemes = ShareLinkDefaults.AuthenticationScheme,
           Policy = ShareLinkDefaults.ReadPolicy)]
public class ShareSegmentReadController(
    IShareLinkAccess shareAccess,
    IShareSegmentReader reader) : ControllerBase
{
    /// <summary>
    /// Cap on effort ids per request. 200 ids x ~7 chars is ~1.4 KB — comfortably under Kestrel's 8 KB
    /// request-line default even with the 43-char token and the rest of the query, and a GET has nowhere
    /// else to put them.
    /// </summary>
    public const int MaxEffortIdsPerRequest = 200;

    /// <summary>Per-track point ceiling on the paged read. Same number as the authenticated bulk endpoint.</summary>
    public const int BulkMaxPointsPerTrack = 2000;

    /// <summary>Per-track point ceiling on the single-track upgrade. Same number as its authenticated twin.</summary>
    public const int SingleMaxPointsPerTrack = 5000;

    /// <summary>The grant for this request, or null when it is not a live SEGMENT grant.</summary>
    private async Task<ShareGrant?> SegmentGrantAsync(CancellationToken ct)
    {
        var grant = await shareAccess.CurrentAsync(ct);
        return grant is null || grant.Kind != ShareKind.Segment ? null : grant;
    }

    /// <summary>
    /// GET /s/{token}/api/segment/performance?effortIds=1,2,3&amp;maxPoints=N — chart + replay data.
    ///
    /// Ids outside the permitted set are dropped SILENTLY. Never 404 on a non-covered id: that would
    /// confirm which ids exist, which is the oracle this design avoids.
    /// </summary>
    [HttpGet("performance")]
    [ProducesResponseType<ShareEffortPerformanceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ShareErrorResponse>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Performance(
        [FromQuery] string? effortIds, [FromQuery] int maxPoints, CancellationToken ct)
    {
        // Headers first, before ANY early return — an unauthenticated 404 must carry the same token
        // hygiene (no-store, referrer policy, noindex) a rendered page does.
        ShareResponseHeaders.Apply(Response);

        var grant = await SegmentGrantAsync(ct);
        if (grant is null) return NotFound();

        var ids = ParseIds(effortIds);
        if (ids.Count > MaxEffortIdsPerRequest)
            return BadRequest(new ShareErrorResponse("Too many effort IDs requested."));
        if (ids.Count == 0) return Ok(new ShareEffortPerformanceResponse([]));

        // Mirror of the authenticated clamp (SegmentsApiController): <=0 stays 0 = full resolution, which
        // replay depends on; positive values snap into [10, 5000]. On this ANONYMOUS surface the clamp is
        // also load-bearing for the cache — the value is a component of ShareSegmentPerfEffort keys in a
        // size-unbounded IMemoryCache, so an unclamped int would let one leaked link mint an entry per
        // distinct integer.
        var maxPts = maxPoints <= 0 ? 0 : Math.Clamp(maxPoints, 10, 5000);

        var efforts = await reader.GetPerformanceAsync(grant, ids, maxPts, ct);
        return Ok(new ShareEffortPerformanceResponse(efforts));
    }

    /// <summary>
    /// GET /s/{token}/api/segment/tracks?skip=0&amp;take=150&amp;maxPoints=2000 — effort tracks for the map.
    ///
    /// Takes NO track ids. The permitted set is derived from the grant and paged over a deterministic
    /// order, so this cannot return a foreign track however wrong a future filter becomes. An intersection
    /// would only be as correct as the intersection code; a derivation has no failure mode that widens.
    /// </summary>
    [HttpGet("tracks")]
    [ProducesResponseType<ShareSegmentTracksResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Tracks(
        [FromQuery] int skip, [FromQuery] int take, [FromQuery] int maxPoints, CancellationToken ct)
    {
        ShareResponseHeaders.Apply(Response);

        var grant = await SegmentGrantAsync(ct);
        if (grant is null) return NotFound();

        var page = await reader.GetTracksAsync(grant, skip, take, ClampBulkPoints(maxPoints), ct);
        return Ok(new ShareSegmentTracksResponse(
            page.Tracks.Select(t => new ShareSegmentTrackDto(t.TrackId, Rounded(t.Points))).ToList(),
            page.Total));
    }

    /// <summary>
    /// GET /s/{token}/api/segment/tracks/{trackId} — one track at higher resolution (click-to-upgrade).
    /// A non-permitted id and an unknown id both 404, indistinguishably.
    /// </summary>
    [HttpGet("tracks/{trackId:int}")]
    [ProducesResponseType<ShareSegmentTrackDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Track(int trackId, [FromQuery] int maxPoints, CancellationToken ct)
    {
        ShareResponseHeaders.Apply(Response);

        var grant = await SegmentGrantAsync(ct);
        if (grant is null) return NotFound();

        var points = await reader.GetTrackAsync(grant, new TrackId(trackId), ClampSinglePoints(maxPoints), ct);
        return points is null ? NotFound() : Ok(new ShareSegmentTrackDto(trackId, Rounded(points)));
    }

    /// <summary>
    /// Per-track point ceiling for the PAGED read, mirroring the authenticated bulk endpoint
    /// (<c>SegmentsApiController.GetBulkActivityTracks</c>): a positive value is capped at 2000, anything
    /// else means the default. Unclamped, <c>maxPoints=99999999</c> drives
    /// <c>TrackCoordinates.Downsample</c>'s stride to 1 and serves every track in the page at full
    /// resolution — leaving the ANONYMOUS surface with a weaker resolution limit than the authenticated
    /// one, on a payload whose endpoints are the owner's home.
    /// </summary>
    private static int ClampBulkPoints(int maxPoints)
        => maxPoints > 0 ? Math.Min(maxPoints, BulkMaxPointsPerTrack) : ShareSegmentReader.DefaultMaxPointsPerTrack;

    /// <summary>
    /// Same ceiling for the SINGLE-track click-to-upgrade read, mirroring its authenticated twin
    /// (<c>SegmentsApiController.GetFullActivityTrack</c>): 5000 rather than 2000, because one track at a
    /// time is what the upgrade path is for.
    /// </summary>
    private static int ClampSinglePoints(int maxPoints)
        => maxPoints > 0 ? Math.Min(maxPoints, SingleMaxPointsPerTrack) : ShareSegmentReader.DefaultMaxPointsPerTrack;

    /// <summary>
    /// Rounds at the wire boundary, like every authenticated GPS endpoint (GpsPrecision): full-precision
    /// doubles are wasted digits on a multi-thousand-point payload, and the anonymous surface is the most
    /// bandwidth-sensitive one. New instances, never mutation — decoded tracks are cached and shared.
    /// </summary>
    private static List<GpsCoordinate> Rounded(IReadOnlyList<GpsCoordinate> points)
        => points.Select(p => new GpsCoordinate
        {
            Lat = GpsPrecision.Coord(p.Lat),
            Lng = GpsPrecision.Coord(p.Lng),
            GpsId = p.GpsId,
        }).ToList();

    /// <summary>Comma-separated ints; anything unparseable is ignored rather than erroring, so a mangled
    /// query degrades to fewer efforts instead of a failed page.</summary>
    private static HashSet<GpsInSegmentId> ParseIds(string? raw)
    {
        var ids = new HashSet<GpsInSegmentId>();
        if (string.IsNullOrWhiteSpace(raw)) return ids;
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(part, CultureInfo.InvariantCulture, out var id)) ids.Add(new GpsInSegmentId(id));
        return ids;
    }
}
