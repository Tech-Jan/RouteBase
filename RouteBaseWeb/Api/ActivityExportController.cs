using System.Globalization;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Requests;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace RouteBaseWeb.Api;

/// <summary>
/// Reply to POST api/activities/export. The download URL is handed back at ENQUEUE time, before the ZIP
/// exists, so a client that loses the toast (reload, another tab) still knows where its file will appear.
/// </summary>
public sealed record ActivityExportQueuedResponse(long JobId, string DownloadUrl, int RequestedCount);

[ApiController]
[Route("api/activities/export")]
public class ActivityExportController : ControllerBase
{
    private readonly IActivityQueryService _activityQuery;
    private readonly ICurrentUserService _currentUser;
    private readonly IGpxExportService _exporter;
    private readonly IActivityExportService _bulkExporter;
    private readonly OperationQueue _queue;
    private readonly ActivityExportProgress _progress;
    private readonly IActivityExportStore _store;

    public ActivityExportController(
        IGpxExportService exporter,
        IActivityExportService bulkExporter,
        IActivityQueryService activityQuery,
        ICurrentUserService currentUser,
        OperationQueue queue,
        ActivityExportProgress progress,
        IActivityExportStore store)
    {
        _exporter      = exporter;
        _bulkExporter  = bulkExporter;
        _activityQuery = activityQuery;
        _currentUser   = currentUser;
        _queue         = queue;
        _progress      = progress;
        _store         = store;
    }

    /// <summary>GET api/activities/export/{trackId} — download a single activity as GPX.</summary>
    [HttpGet("{trackId:int}")]
    // Metadata-only 200 declaration: a bare [Produces] is a result FILTER that stamps the binary content
    // type onto the error ObjectResults too, turning the declared JSON 404 below into an empty 406.
    [ProducesResponseType(typeof(void), StatusCodes.Status200OK, "application/gpx+xml")]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Export(int trackId)
    {
        var userId = _currentUser.GetUserId();
        var activity = await _activityQuery.GetActivityMetadataByIdAsync(trackId);

        // No existence oracle: a ride someone else owns answers exactly like a ride that isn't there
        // (the surface's 404-never-403 convention; the 403 also contradicted the declared responses).
        if (activity == null || activity.UserId != userId)
            return NotFound(ApiError.NotFound("Activity not found."));

        var exportResult = await _exporter.ExportAsync(new TrackId(trackId), HttpContext.RequestAborted);
        if (exportResult == null)
            return NotFound(ApiError.NotFound("Activity not found."));

        var (stream, fileName) = exportResult.Value;
        return File(stream ?? throw new InvalidOperationException("a non-null export result carries a stream"),
            "application/gpx+xml", fileName);
    }

    /// <summary>
    /// GET api/activities/export?all=true (or ?trackIds=1,2,3) — download the caller's activities as a
    /// ZIP of GPX files. The ids are an ASSERTION: naming a ride the caller doesn't own records a
    /// NotOwned skip in the manifest (API-10). For "everything the table is showing" use the POST
    /// sibling, whose id set is a restriction instead.
    /// </summary>
    [HttpGet]
    // Metadata-only 200 (see Export): [Produces] would break the JSON 400 below into an empty 406.
    [ProducesResponseType(typeof(void), StatusCodes.Status200OK, "application/zip")]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ExportAll([FromQuery] bool all = false, [FromQuery] string? trackIds = null)
    {
        var ids = ParseIds(trackIds);

        if (!all && ids.Count == 0)
            return BadRequest(ApiError.BadRequest("Specify all=true or trackIds=…"));

        return await StreamZipAsync(_currentUser.GetUserId(), all ? null : ids);
    }

    /// <summary>
    /// POST api/activities/export — QUEUE a ZIP of the owned subset of an explicit id set. The activities
    /// page sends its current filteredIds here, so the ZIP is exactly what the table is showing, minus
    /// other people's rides (the "Export GPX (N)" button).
    ///
    /// Queued rather than streamed: a few hundred rides take minutes to zip, which is too long to hold a
    /// response open — the user could not leave the page and a proxy timeout would throw the work away.
    /// The reply is the job's download URL, which 404s until the op-toast reports the job complete.
    ///
    /// A body, not a query string: the filtered set runs to four figures and a CSV of it exceeds the 8 KB
    /// request line.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<ActivityExportQueuedResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public IActionResult QueueExport([FromBody] ActivityExportRequest request)
    {
        if (request.TrackIds.Count == 0)
            return BadRequest(ApiError.BadRequest("Specify at least one trackId."));

        var userId = _currentUser.GetUserId();

        // One export at a time per user. A second click while the first is building would overwrite this
        // user's single progress state, leaving the toast describing a job whose file it can no longer name.
        if (_progress.IsRunning(userId))
            return Conflict(ApiError.Conflict("An export is already running. Wait for it to finish."));

        var jobId = _store.NextJobId();
        var queued = _queue.TryEnqueue(new OperationQueue.WorkItem
        {
            Type = OperationQueue.OperationType.ActivityExport,
            UserId = userId,
            Payload = new ActivityExportPayload(userId, request.TrackIds.Distinct().ToList(), jobId),
        });
        if (!queued)
            return Conflict(ApiError.Conflict("The operation queue is full. Try again shortly."));

        // Ownership is NOT resolved here: it is the handler's job, on the queue, so a four-figure id set
        // doesn't run a filter query on the request thread just to answer "accepted".
        return Accepted(new ActivityExportQueuedResponse(
            jobId, DownloadUrlFor(jobId), request.TrackIds.Count));
    }

    /// <summary>
    /// GET api/activities/export/download/{jobId} — collect a finished export. 404 until the job lands, and
    /// 404 (never 403) for another user's job: the store keys the file by owner, so a foreign id simply
    /// resolves to no file.
    /// </summary>
    [HttpGet("download/{jobId:long}")]
    [ProducesResponseType(typeof(void), StatusCodes.Status200OK, "application/zip")]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public IActionResult Download(long jobId)
    {
        var stream = _store.OpenRead(_currentUser.GetUserId(), jobId);
        if (stream == null)
            return NotFound(ApiError.NotFound("Export not found. It may have expired — run it again."));

        return File(stream, "application/zip", ActivityExportProgress.ExportFileName);
    }

    private static string DownloadUrlFor(long jobId) =>
        $"/api/activities/export/download/{jobId.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Builds the ZIP into a temp file, then async-copies it to the response: ZipArchive.Dispose flushes
    /// the central directory with synchronous writes, which Kestrel forbids on the network stream (and
    /// AllowSynchronousIO doesn't reach it under the response-compression wrapper). Buffering to disk keeps
    /// peak memory low, sets Content-Length, and lets a mid-build error surface as a real 500.
    /// <paramref name="ids"/> null = every activity the caller owns.
    /// </summary>
    private async Task<IActionResult> StreamZipAsync(string userId, IReadOnlyList<int>? ids)
    {
        var ct = HttpContext.RequestAborted;
        var tempPath = Path.GetTempFileName();
        try
        {
            await using (var temp = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite))
            {
                var manifest = await _bulkExporter.WriteAllAsZipAsync(temp, userId, ids, ct);
                temp.Position = 0;

                Response.ContentType = "application/zip";
                Response.Headers.ContentDisposition = "attachment; filename=\"activities-gpx.zip\"";
                // API-10: the full breakdown lives in the ZIP's own manifest.json; these headers let a
                // caller notice a partial export (e.g. a stale/foreign id in the request) without unzipping.
                Response.Headers["X-Export-Included-Count"] = manifest.IncludedIds.Count.ToString(CultureInfo.InvariantCulture);
                Response.Headers["X-Export-Skipped-Count"] = manifest.Skipped.Count.ToString(CultureInfo.InvariantCulture);
                Response.ContentLength = temp.Length;
                await temp.CopyToAsync(Response.Body, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Routine client abort (tab closed mid-download). Once the response has started no status can
            // be set — letting the OCE reach ApiExceptionFilter made it call StatusCode(499) on a started
            // response, which threw again and logged secondary noise on every large-export cancel
            // (API-08). Before the response starts, 499 like the GPS endpoints.
            if (!Response.HasStarted) return StatusCode(499);
        }
        finally
        {
            System.IO.File.Delete(tempPath);
        }

        return new EmptyResult();
    }

    private static List<int> ParseIds(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? []
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Select(s => int.TryParse(s, CultureInfo.InvariantCulture, out var n) ? n : (int?)null)
                 .OfType<int>().Distinct().ToList();
}
