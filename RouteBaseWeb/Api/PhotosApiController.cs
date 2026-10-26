using RouteBaseWeb.Constants;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Photos;
using RouteBaseWeb.Models.Uploads;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Photos;
using RouteBaseWeb.Services.Uploads;
using Microsoft.AspNetCore.Mvc;

namespace RouteBaseWeb.Api;

/// <summary>
/// Photo upload (chunked batch → background import), auth'd immutable serving, manual assign,
/// delete, listing, and import status. Pure HTTP mapping: reads route to <see cref="IPhotoQueryService"/>,
/// row mutations to <see cref="IPhotoCommandService"/>, staged-file assignment to the orchestrator, and
/// chunked upload to the shared <see cref="BatchUploadCoordinator"/>. Serving streams the rendition file
/// (kernel sendfile) after a view check; renditions are content-addressed → immutable-cacheable (§1a/§12).
/// </summary>
[ApiController]
[Route("api/photos")]
public class PhotosApiController : ControllerBase
{
    /// <summary>Bulk-request ceiling (same rationale as ActivitiesApiController.MaxTrackIds).</summary>
    private const int MaxPhotoIds = 10_000;

    private static readonly HashSet<string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp" };

    private readonly PhotoImportStaging _staging;
    private readonly BatchUploadCoordinator _coordinator;
    private readonly ICurrentUserService _currentUser;
    private readonly IPhotoQueryService _photoQuery;
    private readonly IPhotoCommandService _photoCommand;
    private readonly IActivityQueryService _activities;
    private readonly PhotoImportProgress _progress;
    private readonly PhotoImportOrchestrator _orchestrator;
    private readonly ILogger<PhotosApiController> _logger;

    public PhotosApiController(
        PhotoImportStaging staging, PhotoUploadPolicy uploadPolicy, OperationQueue queue,
        ICurrentUserService currentUser, IPhotoQueryService photoQuery, IPhotoCommandService photoCommand,
        IActivityQueryService activities, PhotoImportProgress progress, PhotoImportOrchestrator orchestrator,
        ILogger<PhotosApiController> logger)
    {
        _staging = staging;
        // Lambda (not method group): policy stays lazily dereferenced like the old field — tests for
        // non-upload endpoints construct this controller with a null policy.
        _coordinator = new BatchUploadCoordinator(
            staging, queue, PhotoUploadPolicy.HeaderBytes,
            (c, count, bytes) => uploadPolicy.Evaluate(c, count, bytes),
            static (userId, batchId) => new OperationQueue.WorkItem
            {
                Type = OperationQueue.OperationType.PhotoImport,
                UserId = userId,
                Payload = new PhotoImportPayload(userId, batchId),
            });
        _currentUser = currentUser;
        _photoQuery = photoQuery;
        _photoCommand = photoCommand;
        _activities = activities;
        _progress = progress;
        _orchestrator = orchestrator;
        _logger = logger;
    }

    // ── Chunked batch upload (begin → files… → commit) — race/reservation semantics live in the shared
    // BatchUploadCoordinator (CR5-02); this controller maps outcomes and keeps the photo-local checks.
    // These form/multipart POSTs are cross-site-reachable without preflight and localhost mode has no cookie
    // for SameSite to protect — guarded by the global CrossSiteApiGuardFilter (D1), which covers every
    // state-changing api/ verb by default (no per-action [RejectCrossSiteBrowserRequests] needed).
    /// <summary>POST api/photos/batch/begin — reserve the import slot and open an empty staged batch.
    /// Optional <paramref name="trackId"/> (the "add photos to this activity" path, §2) makes every photo
    /// import straight to that activity — only honored when the caller owns it.</summary>
    [HttpPost("batch/begin")]
    [ProducesResponseType<PhotoBeginBatchResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> BeginBatch([FromForm] int previewMaxEdge = 0, [FromForm] int? trackId = null)
    {
        var userId = _currentUser.GetUserId();
        int size = ProfileSettingsDefaults.PhotoPreviewMaxEdgeAllowed.Contains(previewMaxEdge)
            ? previewMaxEdge
            : ProfileSettingsDefaults.PhotoPreviewMaxEdge;

        int? forceTrackId = null;
        if (trackId is { } tid)
        {
            if (!await _activities.IsActivityOwnedAsync(new TrackId(tid)))
                return NotFound(ApiError.NotFound("Activity not found."));
            forceTrackId = tid;
        }

        var begin = _coordinator.Begin(userId, () => _staging.BeginBatch(userId, size, forceTrackId));
        if (!begin.Reserved)
            return Conflict(ApiError.Conflict("An import is already running. Wait for it to finish or cancel it first."));
        return Ok(new PhotoBeginBatchResponse(begin.BatchId, size, forceTrackId));
    }

    /// <summary>POST api/photos/batch/{batchId}/files — stage a chunk of image files into an open batch.</summary>
    [HttpPost("batch/{batchId}/files")]
    [RequestSizeLimit(200 * 1024 * 1024)]
    [ProducesResponseType<StagedFilesResponse>(StatusCodes.Status200OK)]
    // TODO(openapi-review): D20
    [ProducesResponseType<PolicyRejectedBody>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AppendBatchFiles(string batchId, [FromForm] List<IFormFile> files, CancellationToken ct = default)
    {
        if (files == null || files.Count == 0)
            return BadRequest(ApiError.BadRequest("No files provided."));

        // Keep the extension check for staged-name hygiene (the sniffer below is the real authority).
        var invalid = files.Where(f => !AllowedExtensions.Contains(Path.GetExtension(f.FileName))).ToList();
        if (invalid.Count > 0)
            return BadRequest(new PolicyRejectedBody("Only .jpg/.jpeg/.png/.webp images are accepted.", invalid.Select(f => f.FileName)));

        // Safe-upload guard (PI-01/03/09) runs inside the coordinator: content-sniff allowlist +
        // per-file/per-batch caps + bad-name reject.
        var userId = _currentUser.GetUserId();
        var result = await _coordinator.AppendAsync(userId, batchId, files, ct);
        return result.Outcome switch
        {
            BatchAppendOutcome.Staged =>
                Ok(new StagedFilesResponse(result.Staged)),
            BatchAppendOutcome.BatchNotFound =>
                NotFound(ApiError.NotFound("Import batch not found. Start the import again.")),
            BatchAppendOutcome.PolicyRejected =>
                BadRequest(new PolicyRejectedBody(result.Error, result.RejectedFiles)),
            _ => // AlreadyCommitted (pre-check or in-lock raced commit, PI-05)
                Conflict(ApiError.Conflict("Batch already committed.")),
        };
    }

    /// <summary>POST api/photos/batch/{batchId}/commit — enqueue the staged batch for background import.</summary>
    [HttpPost("batch/{batchId}/commit")]
    [ProducesResponseType<CommitBatchResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiError>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CommitBatch(string batchId)
    {
        var userId = _currentUser.GetUserId();
        var result = await _coordinator.CommitAsync(userId, batchId);
        return result.Outcome switch
        {
            BatchCommitOutcome.Queued =>
                Ok(new CommitBatchResponse(batchId, result.QueuedFiles)),
            BatchCommitOutcome.BatchNotFound =>
                NotFound(ApiError.NotFound("Import batch not found. Start the import again.")),
            BatchCommitOutcome.EmptyBatch =>
                BadRequest(ApiError.BadRequest("No files were staged for this batch.")),
            BatchCommitOutcome.AlreadyCommitted =>
                Conflict(ApiError.Conflict("Batch already committed — import is already queued.")),
            BatchCommitOutcome.LostCommitRace => // PI-04
                Conflict(ApiError.Conflict("Batch already committed or no longer exists. Check the import status.")),
            _ => // QueueFull
                StatusCode(503, ApiError.ServiceUnavailable("Operation queue is full. Try again shortly.")),
        };
    }

    /// <summary>POST api/photos/batch/{batchId}/abort — discard an uncommitted staged batch and release the slot.</summary>
    [HttpPost("batch/{batchId}/abort")]
    [ProducesResponseType<AbortBatchResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AbortBatch(string batchId)
    {
        var userId = _currentUser.GetUserId();
        return await _coordinator.AbortAsync(userId, batchId) switch
        {
            BatchAbortOutcome.Aborted      => Ok(new AbortBatchResponse(true)),
            BatchAbortOutcome.UnknownBatch => Ok(new AbortBatchResponse(false)),
            _ => Conflict(ApiError.Conflict("Batch already committed — cancel via the operation toast.")),
        };
    }

    // ── Serving ─────────────────────────────────────────────────────────────────
    /// <summary>GET api/photos/{id}/image?tier={thumb|preview}&amp;v={hash8} — stream a rendition after an
    /// activity-visibility check. Content-addressed → immutable-cacheable; missing file → 404 (blur-up shows).</summary>
    [HttpGet("{id:int}/image")]
    // Metadata-only 200 (D14): a bare [Produces] is a result filter that would force the JSON 400
    // below to an empty 406.
    [ProducesResponseType(typeof(void), StatusCodes.Status200OK, "image/jpeg")]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetImage(int id, [FromQuery] string tier = "thumb", CancellationToken ct = default)
    {
        if (!Enum.TryParse<PhotoTier>(tier, ignoreCase: true, out var t) || t is not (PhotoTier.Thumb or PhotoTier.Preview))
            return BadRequest(ApiError.BadRequest("tier must be thumb or preview."));

        var path = await _photoQuery.GetServableImagePathAsync(new PhotoId(id), t, ct);
        if (path == null) return NotFound();

        // Renditions are immutable per content hash (the ?v= cache-buster changes when bytes change), so a
        // long private cache is safe: the browser serves repeat views without re-hitting the app. Kept
        // `private` (not `public`) while serving is auth-gated in-process; revisit for a CDN + signed URLs.
        Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        return PhysicalFile(path, "image/jpeg");
    }

    // ── Listing (grid / Details panel + minimap) ─────────────────────────────────
    /// <summary>GET api/photos?trackId=&amp;from=&amp;to=&amp;page=&amp;pageSize= — trackId path lists one
    /// activity's photos (with markers) if viewable; without trackId, the caller's own photos (grid).</summary>
    [HttpGet]
    [ProducesResponseType<PhotoListResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] int? trackId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] string sort = "captured",
        [FromQuery] int page = 1, [FromQuery] int pageSize = 60, CancellationToken ct = default) =>
        Ok(await _photoQuery.ListAsync(
            trackId is { } tq ? new TrackId(tq) : (TrackId?)null, from, to, sort, page, pageSize, PhotoFieldScope.OwnerWhenOwned, ct));

    // ── Overview map markers ─────────────────────────────────────────────────────
    /// <summary>GET api/photos/map-markers — the caller's own placed photos (stored lat/lng) as slim
    /// markers for the Activities overview map overlay. Own photos are always viewable → no per-item
    /// visibility check. Newest-capture first, capped; the overlay filters to visible tracks client-side.</summary>
    [HttpGet("map-markers")]
    [ProducesResponseType<PhotoMarkersResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> MapMarkers(CancellationToken ct = default) =>
        Ok(await _photoQuery.GetMapMarkersAsync(ct));

    // ── Assign / delete ───────────────────────────────────────────────────────────
    /// <summary>POST api/photos/assign — persist a staged ambiguous/unmatched (or "keep anyway" duplicate)
    /// file to one of the caller's activities. Thin delegation to the import orchestrator + progress resolve
    /// (the staged-file path stays with the importer, not the row-mutate command service).</summary>
    [HttpPost("assign")]
    [ProducesResponseType<AssignPhotoResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<DuplicatePhotoConflictBody>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiError>(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Assign([FromBody] AssignPhotoRequest req, CancellationToken ct)
    {
        var userId = _currentUser.GetUserId();
        if (!await _activities.IsActivityOwnedAsync(new TrackId(req.TrackId)))
            return NotFound(ApiError.NotFound("Activity not found."));

        var result = await _orchestrator.AssignStagedAsync(userId, req.BatchId, req.StagedName, new TrackId(req.TrackId), req.KeepAnyway, ct);
        switch (result.Outcome)
        {
            case "assigned":
                _progress.ResolveStagedFile(userId, req.StagedName, "imported", new TrackId(req.TrackId));
                return Ok(new AssignPhotoResponse(result.PhotoId, "imported"));
            case "duplicate":
                return Conflict(new DuplicatePhotoConflictBody(
                    "This photo is a duplicate. Use keep-anyway to import it regardless.", "duplicate"));
            case "gone":
                return NotFound(ApiError.NotFound("The staged file has expired. Re-upload it."));
            default:
                return StatusCode(500, ApiError.ServerError("Could not process the photo."));
        }
    }

    /// <summary>POST api/photos/{id}/place — hand-place a photo's map marker at a clicked lat/lng
    /// (Details "place on map"). Owner-only; sets the resolved location with source <c>manual</c>.</summary>
    [HttpPost("{id:int}/place")]
    [ProducesResponseType<PhotoLocationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Place(int id, [FromBody] PlacePhotoRequest req, CancellationToken ct)
    {
        if (req.Lat is < -90 or > 90 || req.Lng is < -180 or > 180)
            return BadRequest(ApiError.BadRequest("lat/lng out of range."));

        return await _photoCommand.PlaceAsync(new PhotoId(id), req.Lat, req.Lng, ct) switch
        {
            PhotoCommandOutcome.Ok => Ok(new PhotoLocationResponse(id, req.Lat, req.Lng)),
            _ => NotFound(),
        };
    }

    /// <summary>DELETE api/photos/{id}/place — remove a photo from the map: it becomes "free" (still on the
    /// activity, off the map). The stored lat/lng is kept so <c>restore</c> can snap it back. Owner-only.</summary>
    [HttpDelete("{id:int}/place")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Unplace(int id, CancellationToken ct) =>
        await _photoCommand.UnplaceAsync(new PhotoId(id), ct) switch
        {
            PhotoCommandOutcome.Ok => NoContent(),
            _ => NotFound(),
        };

    /// <summary>POST api/photos/{id}/restore — put a hidden photo back on the map at its remembered location.
    /// Owner-only; 409 if there's no stored location to restore (place it instead).</summary>
    [HttpPost("{id:int}/restore")]
    [ProducesResponseType<PhotoLocationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Restore(int id, CancellationToken ct)
    {
        var result = await _photoCommand.RestoreAsync(new PhotoId(id), ct);
        return result.Outcome switch
        {
            PhotoCommandOutcome.Ok => Ok(new PhotoLocationResponse(id, result.Lat, result.Lng)),
            PhotoCommandOutcome.NoRememberedLocation =>
                Conflict(ApiError.Conflict("No remembered location to restore — place it on the map instead.")),
            _ => NotFound(),
        };
    }

    /// <summary>DELETE api/photos/{id} — delete the caller's photo (row, then rendition files, §1b).</summary>
    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct) =>
        await _photoCommand.DeleteAsync(new PhotoId(id), ct) switch
        {
            PhotoCommandOutcome.Ok => NoContent(),
            _ => NotFound(),
        };

    /// <summary>POST api/photos/delete — bulk-delete the caller's photos (multi-select "Apply"). Silently
    /// ignores ids the caller doesn't own; rows first, then rendition files.</summary>
    [HttpPost("delete")]
    [ProducesResponseType<DeletedCountResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DeleteMany([FromBody] DeletePhotosRequest req, CancellationToken ct)
    {
        var ids = (req.PhotoIds ?? new()).Distinct().ToList();
        if (ids.Count == 0) return BadRequest(ApiError.BadRequest("No photos selected."));
        // Mirrors MaxTrackIds on the activities bulk endpoints: no UI selection can produce more, so
        // anything larger is a hand-rolled request turning into an unbounded IN (...) over the DB.
        if (ids.Count > MaxPhotoIds)
            return BadRequest(ApiError.BadRequest($"Too many photos in one request (max {MaxPhotoIds})."));

        // deleted == 0 is still a 200: per the contract above, foreign/already-gone ids are silently
        // ignored — a 404 here made a second tab's confirmed delete look ignored (grid never reloads).
        int deleted = await _photoCommand.DeleteManyAsync(ids, ct);
        return Ok(new DeletedCountResponse(deleted));
    }

    // ── Import status (Photos-page results panel polling) ────────────────────────
    [HttpGet("import-status")]
    [ProducesResponseType<PhotoImportSnapshot>(StatusCodes.Status200OK)]
    public IActionResult ImportStatus() => Ok(_progress.GetStatus(_currentUser.GetUserId()));
}
