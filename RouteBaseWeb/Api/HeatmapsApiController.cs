using RouteBaseWeb.Helpers;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Heatmaps;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Requests;

namespace RouteBaseWeb.Api;

/// <summary>
/// Attach + view endpoints for activity heatmaps. A heatmap is an optional attachment on a normal
/// shapes segment: attach/regenerate enqueues a background render that reads the segment's efforts,
/// and the tile pyramid is served as immutable PNGs.
/// </summary>
[ApiController]
[Route("api/heatmaps")]
public class HeatmapsApiController : ControllerBase
{
    private readonly IHeatmapLifecycle _heatmaps;
    private readonly OperationQueue _operationQueue;
    private readonly ICurrentUserService _currentUser;
    private readonly HeatmapTilePackStore _tileStore;
    private readonly HeatmapTileMetaCache _tileMeta;
    private readonly ILogger<HeatmapsApiController> _logger;

    public HeatmapsApiController(
        IHeatmapLifecycle heatmaps,
        OperationQueue operationQueue,
        ICurrentUserService currentUser,
        HeatmapTilePackStore tileStore,
        HeatmapTileMetaCache tileMeta,
        ILogger<HeatmapsApiController> logger)
    {
        _heatmaps       = heatmaps;
        _operationQueue = operationQueue;
        _currentUser    = currentUser;
        _tileStore      = tileStore;
        _tileMeta       = tileMeta;
        _logger         = logger;
    }

    /// <summary>
    /// Attach (or regenerate) a heatmap on an owned shapes segment and enqueue the render. 404 when the
    /// segment is missing/not-owned/not-shapes, 409 while a render is pending/running, else 202 — poll
    /// <see cref="GetHeatmap"/> for status.
    /// </summary>
    [HttpPost("{segmentId:int}")]
    [RejectCrossSiteBrowserRequests] // body is OPTIONAL — an empty cross-site POST binds null and executes (PI-01)
    [ProducesResponseType<HeatmapAcceptedResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiError>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Attach(int segmentId, [FromBody] AttachHeatmapRequest? request)
    {
        var result = await _heatmaps.AttachAsync(new SegmentId(segmentId), request?.ActivityTypes ?? []);
        switch (result)
        {
            case HeatmapAttachResult.NotFound:
                return NotFound();
            case HeatmapAttachResult.Conflict:
                return Conflict(ApiError.Conflict("A heatmap render is already pending or running for this segment."));
        }

        string userId = _currentUser.GetUserId();
        if (!_operationQueue.TryEnqueue(new OperationQueue.WorkItem
        {
            Type    = OperationQueue.OperationType.HeatmapGeneration,
            UserId  = userId,
            Payload = new HeatmapGenerationPayload(userId, segmentId)
        }))
        {
            // Queue full: the row was just flipped to pending, so undo it to failed — otherwise
            // every retry hits the pending/running guard and the heatmap is wedged forever.
            await _heatmaps.FailPendingAsync(new SegmentId(segmentId), "Operation queue is full — try again later.");
            return StatusCode(503, ApiError.ServiceUnavailable("Operation queue is full — try again later."));
        }

        return AcceptedAtAction(nameof(GetHeatmap), new { segmentId }, new HeatmapAcceptedResponse(segmentId));
    }

    /// <summary>Metadata + generation status for a heatmap segment (single poll/display endpoint).</summary>
    [HttpGet("{segmentId:int}")]
    [ProducesResponseType<HeatmapMetaResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetHeatmap(int segmentId, CancellationToken ct)
    {
        var heatmap = await _heatmaps.GetVisibleAsync(new SegmentId(segmentId), ct);
        if (heatmap == null)
            return NotFound();

        return Ok(new HeatmapMetaResponse(
            SegmentId: segmentId,
            Status: heatmap.Status,
            Error: heatmap.Error,
            Layer: HeatmapGenerationService.CountLogLayer,
            MinZoom: heatmap.MinZoom,
            MaxZoom: heatmap.MaxZoom,
            CountMax: heatmap.CountMax,
            Bounds: heatmap is { BoundsSouth: double s, BoundsWest: double w, BoundsNorth: double n, BoundsEast: double e }
                ? new HeatmapBoundsDto(South: s, West: w, North: n, East: e)
                : null,
            ActivityTypes: heatmap.ActivityTypes,
            GeneratedAt: heatmap.GeneratedAt.ToUtcOffset()));
    }

    // Empty squares get a real transparent tile instead of a 404: Leaflet fills errored tiles by
    // stretching cached parent tiles (dark blur veil → "black gradient boxes"), so a 200 that the
    // browser can cache is the only rendering-safe answer.
    private static readonly byte[] TransparentTile =
        PngEncoder.EncodeRgba(new byte[WebMercator.TileSize * WebMercator.TileSize * 4],
            WebMercator.TileSize, WebMercator.TileSize);

    /// <summary>
    /// Serve one PNG tile. Tiles are immutable per generation, so a strong ETag drives 304s and the
    /// response is privately cacheable for a week. Missing tiles are served as transparent PNGs.
    /// </summary>
    [HttpGet("{segmentId:int}/tiles/{layer}/{z:int}/{x:int}/{y:int}.png")]
    // Metadata-only 200: errors here are bodyless today, but a bare [Produces] would 406 any future
    // JSON-bodied error (it stamps its content type on every ObjectResult) — same class as the export bug.
    [ProducesResponseType(typeof(void), StatusCodes.Status200OK, "image/png")]
    public async Task<IActionResult> GetTile(int segmentId, string layer, int z, int x, int y, CancellationToken ct)
    {
        // HM-14: validate the address before any header is built — a crafted layer string must not
        // reach the ETag header (illegal header chars throw → 500), and out-of-range z/x/y would
        // alias other entries in the packed 8+8+24+24-bit index key.
        if (!HtilesFormat.TryLayerId(layer, out _)
            || z is < 0 or > 22 || x < 0 || y < 0 || x >= (1L << z) || y >= (1L << z))
            return NotFound();

        try
        {
            // Cached ~30s per (user, segment): a pan/scrub fires many tile GETs; only pending/running/
            // failed states are absent here (never cached). Once a regenerate flips the row to pending
            // the old imagery drops out within one TTL; the `stale` status is what keeps tiles viewable.
            var meta = await _tileMeta.GetServableAsync(new SegmentId(segmentId), ct);
            if (meta is null)
                return NotFound();

            var etag = $"\"{meta.Value.HeatmapId}-{meta.Value.GeneratedAtTicks}-{layer}-{z}-{x}-{y}\"";
            // immutable: the ?v={generatedAt} URL changes on every regenerate, so a given URL's bytes
            // never change — tell the browser to skip revalidation (even on reload), not just cache 7 days.
            Response.Headers.CacheControl = "private, max-age=604800, immutable";
            Response.Headers.ETag = etag;

            if (Request.Headers.IfNoneMatch == etag)
                return StatusCode(StatusCodes.Status304NotModified);

            var png = await _tileStore.ReadTileAsync(
                meta.Value.HeatmapId, meta.Value.GeneratedAtTicks, layer, z, x, y, ct);
            return File(png ?? TransparentTile, "image/png");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client (Leaflet) aborted the request, e.g. scrubbing the replay bar rapidly.
            return new EmptyResult();
        }
    }
}
