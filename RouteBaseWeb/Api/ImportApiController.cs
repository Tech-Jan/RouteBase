using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Uploads;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Uploads;
using Microsoft.AspNetCore.Mvc;

namespace RouteBaseWeb.Api;

[ApiController]
[Route("api/import")]
public class ImportApiController : ControllerBase
{
    private readonly ICurrentUserService _currentUser;
    private readonly TrackImportStaging _staging;
    private readonly BatchUploadCoordinator _coordinator;
    private readonly ILogger<ImportApiController> _logger;

    public ImportApiController(
        ICurrentUserService currentUser,
        TrackImportStaging staging,
        TrackUploadPolicy uploadPolicy,
        OperationQueue queue,
        ILogger<ImportApiController> logger)
    {
        _currentUser = currentUser;
        _staging     = staging;
        _logger      = logger;
        // Lambda (not method group) keeps the policy lazily dereferenced, like the old field.
        _coordinator = new BatchUploadCoordinator(
            staging, queue, TrackUploadPolicy.HeaderBytes,
            (c, count, bytes) => uploadPolicy.Evaluate(c, count, bytes),
            static (userId, batchId) => new OperationQueue.WorkItem
            {
                Type    = OperationQueue.OperationType.TrackImport,
                UserId  = userId,
                Payload = new TrackImportPayload(userId, batchId)
            });
    }

    // ── Chunked batch import (begin → files… → commit) ─────────────────────────
    //
    // Large imports (hundreds–thousands of files) are staged in small chunks so no
    // single HTTP request has to carry every file — a single giant request times out
    // on the reverse proxy and risks the multipart form-length limit. The per-user
    // import slot is reserved at "begin" and released at "commit" (by the worker when
    // it finishes), on "abort", or on any failure path. Reservation/commit/abort race
    // semantics live in the shared BatchUploadCoordinator (CR5-02); this controller
    // maps its outcomes to status codes + bodies.
    //
    // These batch endpoints bind form/route data only, so a hostile page can fire them as plain cross-site
    // POSTs (no CORS preflight), and in localhost mode there is no cookie for SameSite to protect — so a
    // drive-by request could inject activity files into the library and wedge the per-user import slot until
    // restart (API-02). Guarded by the global CrossSiteApiGuardFilter (D1), which covers every state-changing
    // api/ verb by default — no per-action [RejectCrossSiteBrowserRequests] needed.

    /// <summary>POST api/import/batch/begin — reserve the import slot and open an empty staged batch.</summary>
    [HttpPost("batch/begin")]
    [ProducesResponseType<BeginBatchResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public IActionResult BeginBatch([FromForm] bool useFilenameAsId = false)
    {
        var userId = _currentUser.GetUserId();

        var begin = _coordinator.Begin(userId, () =>
        {
            var batchId = _staging.BeginBatch(userId, useFilenameAsId);
            _logger.LogInformation("Opened GPX batch {BatchId} for user {UserId}", batchId, userId);
            return batchId;
        });

        if (!begin.Reserved)
            return Conflict(ApiError.Conflict("An import is already running. Wait for it to finish or cancel it first."));
        return Ok(new BeginBatchResponse(begin.BatchId));
    }

    /// <summary>POST api/import/batch/{batchId}/files — stage a chunk of activity files into an open batch.</summary>
    [HttpPost("batch/{batchId}/files")]
    [RequestSizeLimit(100 * 1024 * 1024)] // 100 MB per chunk
    [ProducesResponseType<StagedFilesResponse>(StatusCodes.Status200OK)]
    // TODO(openapi-review): D20
    [ProducesResponseType<PolicyRejectedBody>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AppendBatchFiles(
        string batchId,
        [FromForm] List<IFormFile> files,
        CancellationToken ct = default)
    {
        if (files == null || files.Count == 0)
            return BadRequest(ApiError.BadRequest("No files provided."));

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
            _ => // AlreadyCommitted — 409's "start again" guidance also fits the raced-commit case
                Conflict(ApiError.Conflict("Batch already committed.")),
        };
    }

    /// <summary>POST api/import/batch/{batchId}/commit — enqueue the staged batch for background import.</summary>
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

        if (result.Outcome == BatchCommitOutcome.Queued)
            _logger.LogInformation("Committed GPX batch {BatchId} ({Count} files) for user {UserId}",
                batchId, result.QueuedFiles, userId);

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
            BatchCommitOutcome.LostCommitRace =>
                Conflict(ApiError.Conflict("Batch already committed or no longer exists. Check the import status.")),
            _ => // QueueFull
                StatusCode(503, ApiError.ServiceUnavailable("Operation queue is full. Try again shortly.")),
        };
    }

    /// <summary>POST api/import/batch/{batchId}/abort — discard an uncommitted staged batch and release the import slot.</summary>
    [HttpPost("batch/{batchId}/abort")]
    [ProducesResponseType<AbortBatchResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AbortBatch(string batchId)
    {
        var userId = _currentUser.GetUserId();
        var outcome = await _coordinator.AbortAsync(userId, batchId);

        if (outcome == BatchAbortOutcome.Aborted)
            _logger.LogInformation("Aborted GPX batch {BatchId} for user {UserId}", batchId, userId);

        return outcome switch
        {
            BatchAbortOutcome.Aborted      => Ok(new AbortBatchResponse(true)),
            BatchAbortOutcome.UnknownBatch => Ok(new AbortBatchResponse(false)),
            _ => Conflict(ApiError.Conflict("Batch already committed — cancel via the operation toast.")),
        };
    }
}
