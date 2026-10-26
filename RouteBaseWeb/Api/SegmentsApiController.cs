﻿using System.Globalization;
using RouteBaseWeb.Helpers;
using RouteBaseWeb.Helpers.Grid;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Segments;
using RouteBaseWeb.Services.Segments.Rules;
using Microsoft.AspNetCore.Mvc;
using RouteBaseWeb.Models.Requests;
using RouteBaseWeb.Services.Tracks;

namespace RouteBaseWeb.Api;

[ApiController]
[Route("api/SegmentsApi")]
public class SegmentsApiController : ControllerBase
{
    private readonly ISegmentCrud _segmentCrud;
    private readonly ISegmentAnalysis _segmentAnalysis;
    private readonly ISegmentEfforts _segmentEfforts;
    private readonly IActivityGpsService _activityGps;
    private readonly SegmentAnalysisProgress _analysisProgress;
    private readonly OperationQueue _operationQueue;
    private readonly ICurrentUserService _currentUser;
    private readonly ShapeSegmentMatcher _shapeMatcher;
    private readonly IOwnerScopeService _ownerScope;
    private readonly ILogger<SegmentsApiController> _logger;
    private readonly Microsoft.Extensions.Options.IOptions<JsonOptions> _jsonOptions;

    public SegmentsApiController(
        ISegmentCrud segmentCrud,
        ISegmentAnalysis segmentAnalysis,
        ISegmentEfforts segmentEfforts,
        IActivityGpsService activityGps,
        SegmentAnalysisProgress analysisProgress,
        OperationQueue operationQueue,
        ICurrentUserService currentUser,
        ShapeSegmentMatcher shapeMatcher,
        IOwnerScopeService ownerScope,
        ILogger<SegmentsApiController> logger,
        Microsoft.Extensions.Options.IOptions<JsonOptions> jsonOptions)
    {
        _segmentCrud      = segmentCrud;
        _segmentAnalysis  = segmentAnalysis;
        _segmentEfforts   = segmentEfforts;
        _activityGps      = activityGps;
        _analysisProgress = analysisProgress;
        _operationQueue   = operationQueue;
        _currentUser      = currentUser;
        _shapeMatcher     = shapeMatcher;
        _ownerScope       = ownerScope;
        _logger           = logger;
        _jsonOptions      = jsonOptions;
    }

    [HttpGet]
    [ProducesResponseType<List<SegmentSummaryDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllSegments([FromQuery] int? activityId = null)
    {
        var segments = activityId.HasValue
            ? await _segmentCrud.GetSegmentsForActivityAsync(new TrackId(activityId.Value))
            : await _segmentCrud.GetAllSegmentsAsync();

        var segmentDtos = segments.Select(s => new SegmentSummaryDto(
            s.SegmentId.Value, s.SegmentName, s.SegmentDistance, s.SegmentAscent, s.SegmentDescent, s.ActivityType)).ToList();
        return Ok(segmentDtos);
    }

    /// <summary>The dashboard card list: the first N visible segments, projected server-side.</summary>
    [HttpGet("cards")]
    [ProducesResponseType<List<SegmentCardDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetSegmentCards([FromQuery] int limit = 3)
    {
        // A card list is small by definition; an unbounded limit here would just be the full-list
        // endpoint wearing a different name (and a way to page past the widget's intent).
        if (limit < 1 || limit > 20)
            return BadRequest(ApiError.BadRequest("limit must be between 1 and 20."));

        return Ok(await _segmentCrud.GetTopSegmentCardsAsync(limit));
    }

    [HttpPost("analyze-all")]
    [RejectCrossSiteBrowserRequests] // bodyless POST — cross-site form/fetch-able without preflight (PI-01)
    [ProducesResponseType<AnalysisQueuedResponse>(StatusCodes.Status202Accepted)]
    // TODO(openapi-review): D20
    [ProducesResponseType<SegmentAnalysisConflictBody>(StatusCodes.Status409Conflict)]
    public IActionResult AnalyzeAllSegments()
    {
        string userId = _currentUser.GetUserId();
        if (_analysisProgress.IsRunning(userId))
        {
            return Conflict(new SegmentAnalysisConflictBody("Analysis is already running", _analysisProgress.GetStatus(userId)));
        }

        if (!_operationQueue.TryEnqueue(new OperationQueue.WorkItem
            {
                Type    = OperationQueue.OperationType.SegmentAnalysis,
                UserId  = userId,
                Payload = new SegmentAnalysisPayload(userId)
            }))
        {
            return Conflict(ApiError.Conflict("Operation queue is full"));
        }

        return Accepted(new AnalysisQueuedResponse("Analysis queued in background"));
    }

    [HttpPost]
    [RequestSizeLimit(128 * 1024 * 1024)] // 1M points ≈ 65 MB JSON — override Kestrel's 30 MB default
    [ProducesResponseType<CreatedSegmentResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateSegment([FromBody] CreateSegmentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SegmentName))
            return BadRequest(ApiError.BadRequest("Segment name is required"));

        if (request.GpsPoints == null || request.GpsPoints.Count < 2)
            return BadRequest(ApiError.BadRequest("At least 2 GPS points are required"));

        if (request.GpsPoints.Any(p => p.Lat < -90 || p.Lat > 90 || p.Lng < -180 || p.Lng > 180))
            return BadRequest(ApiError.BadRequest("GPS points contain out-of-range coordinates (lat must be [-90,90], lng [-180,180])"));

        // SA-04: RDP's StackOverflow / O(n²) risk is bounded server-side by StrideDownsample, which caps
        // the geometry RDP sees to SegmentGeometry.RdpWorkingCap regardless of input length — so a segment
        // of any length is safe to accept. This ceiling only guards raw request memory / JSON parse (the
        // [RequestSizeLimit] above raises the body limit to fit it). No real activity approaches 1M points.
        if (request.GpsPoints.Count > 1_000_000)
            return BadRequest(ApiError.BadRequest("Too many GPS points (max 1000000)"));

        var segment = await _segmentCrud.CreateSegmentAsync(
            request.SegmentName,
            request.ActivityType ?? "",
            request.GpsPoints);

        // Queue analysis in the background — client gets the segment immediately. SA-09: report the
        // queue outcome so the client can prompt "press Re-analyze later" when it was full.
        string userId = _currentUser.GetUserId();
        bool analysisQueued = _operationQueue.TryEnqueue(new OperationQueue.WorkItem
        {
            Type    = OperationQueue.OperationType.SingleSegmentAnalysis,
            UserId  = userId,
            Payload = new SingleSegmentAnalysisPayload(userId, segment.SegmentId.Value, null)
        });
        if (!analysisQueued)
            _logger.LogWarning("Operation queue full — analysis for segment {SegmentId} not queued", segment.SegmentId);

        var segmentDto = new CreatedSegmentResponse(
            segment.SegmentId.Value, segment.SegmentName, segment.SegmentDistance, segment.SegmentAscent,
            segment.SegmentDescent, segment.ActivityType, analysisQueued);

        // 201: a new resource was created. No Location header — the GET-by-id action it would have
        // pointed at was deleted 2026-07-17 (dead: no client ever fetched a single segment by id).
        return StatusCode(StatusCodes.Status201Created, segmentDto);
    }

    // -------------------------------------------------------------------------
    // Shape-defined segments: a rule document instead of a drawn polyline.
    // -------------------------------------------------------------------------

    [HttpPost("shapes")]
    [ProducesResponseType<ShapeSegmentResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateShapeSegment([FromBody] CreateShapeSegmentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SegmentName))
            return BadRequest(ApiError.BadRequest("Segment name is required"));

        if (ValidateRules(request) is not { } doc)
            return BadRequest(ApiError.BadRequest(RulesError(request)));

        var scope   = await _ownerScope.ResolveAsync(request.Owner);
        var segment = await _segmentCrud.CreateShapeSegmentAsync(
            request.SegmentName, request.ActivityType ?? "", FoldOwner(doc, scope).Serialize());

        // Queue analysis in the background — the shape matcher runs there. SA-09: report the outcome.
        string userId = _currentUser.GetUserId();
        bool analysisQueued = _operationQueue.TryEnqueue(new OperationQueue.WorkItem
        {
            Type    = OperationQueue.OperationType.SingleSegmentAnalysis,
            UserId  = userId,
            Payload = new SingleSegmentAnalysisPayload(userId, segment.SegmentId.Value, null)
        });
        if (!analysisQueued)
            _logger.LogWarning("Operation queue full — analysis for segment {SegmentId} not queued", segment.SegmentId);

        // 201: shape segment created. No Location header — the GET-by-id action was deleted 2026-07-17 (㉑).
        return StatusCode(StatusCodes.Status201Created,
            new ShapeSegmentResponse(segment.SegmentId.Value, segment.SegmentName, segment.SegmentKind, segment.ActivityType, analysisQueued));
    }

    /// <summary>
    /// Overwrites an existing shape-defined segment in place (the user edited it in paint mode and
    /// kept the same name). Replaces its name/rule document and re-queues match analysis.
    /// Renaming is handled client-side as a "save as copy" via the create endpoint instead.
    /// </summary>
    [HttpPut("{id}/shapes")]
    [ProducesResponseType<ShapeSegmentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateShapeSegment(int id, [FromBody] CreateShapeSegmentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SegmentName))
            return BadRequest(ApiError.BadRequest("Segment name is required"));

        if (ValidateRules(request) is not { } doc)
            return BadRequest(ApiError.BadRequest(RulesError(request)));

        var scope   = await _ownerScope.ResolveAsync(request.Owner);
        var segment = await _segmentCrud.UpdateShapeSegmentAsync(
            new SegmentId(id), request.SegmentName, request.ActivityType ?? "", FoldOwner(doc, scope).Serialize());
        if (segment == null)
            return NotFound();

        // Rebuild efforts in the background — the analysis step clears prior efforts first. SA-09:
        // a full queue leaves STALE efforts (old rules) with no client signal; report the outcome.
        string userId = _currentUser.GetUserId();
        bool analysisQueued = _operationQueue.TryEnqueue(new OperationQueue.WorkItem
        {
            Type    = OperationQueue.OperationType.SingleSegmentAnalysis,
            UserId  = userId,
            Payload = new SingleSegmentAnalysisPayload(userId, segment.SegmentId.Value, null)
        });
        if (!analysisQueued)
            _logger.LogWarning("Operation queue full — analysis for segment {SegmentId} not queued", segment.SegmentId);

        return Ok(new ShapeSegmentResponse(segment.SegmentId.Value, segment.SegmentName, segment.SegmentKind, segment.ActivityType, analysisQueued));
    }

    /// <summary>
    /// Full editable payload for a shape-defined segment: name, activity type, owner scope, and the
    /// rule document verbatim. Used to re-hydrate paint mode when editing (the lighter
    /// <see cref="GetSegmentShapes"/> is geometry-only and can't round-trip).
    /// </summary>
    [HttpGet("{id}/shape-segment")]
    [ProducesResponseType<ShapeSegmentEditResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetShapeSegmentForEdit(int id)
    {
        // SA-03: owner-only — the payload exposes the raw rule document + owner friend-id list, and only
        // the owner can save edits. 404-no-leak (matches Delete/Update) for a mere viewer.
        var segment = await _segmentCrud.GetOwnedSegmentAsync(new SegmentId(id));
        if (segment == null || segment.SegmentKind != SegmentKinds.Shapes)
            return NotFound();

        // Pre-rework segments carry no document and can't be edited — only re-drawn.
        if (segment.MatchRulesJson is not { } rulesJson || RuleDocument.TryParse(rulesJson) is not { } doc)
            return NotFound();

        // Owner scope, computed doc-first: the document's owner filter wins over the legacy column
        // (doc owner ?? column CSV ?? null = own-only). Frontend consumes this unchanged.
        var docOwnerIds = doc.TrackFilters.OfType<OwnerFilter>().FirstOrDefault()?.UserIds;
        string[]? matchOwnerIds =
            docOwnerIds != null                     ? docOwnerIds.ToArray()
          : string.IsNullOrEmpty(segment.MatchOwnerIds) ? null
          : segment.MatchOwnerIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return Ok(new ShapeSegmentEditResponse(
            SegmentName: segment.SegmentName,
            ActivityType: segment.ActivityType,
            MatchOwnerIds: matchOwnerIds,
            // Stored canonical JSON, returned verbatim (no re-serialization round-trip).
            Rules: System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(rulesJson)));
    }

    /// <summary>
    /// Fast, approximate live count of activities matching the rule document (bbox pre-filter only).
    /// Drives the count shown while the user is still painting; the precise set is computed on save.
    /// </summary>
    [HttpPost("shapes/preview")]
    [ProducesResponseType<ShapePreviewResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> PreviewShapeSegment([FromBody] CreateShapeSegmentRequest request)
    {
        if (request.ParseRules() is not { } doc || doc.Shapes is not { Count: > 0 })
            return Ok(new ShapePreviewResponse(0, Array.Empty<int>()));

        if (doc.Validate() is { Count: > 0 } errors)
            return BadRequest(ApiError.BadRequest(string.Join("; ", errors)));

        var scope = await _ownerScope.ResolveAsync(request.Owner);
        var ids = await _shapeMatcher.PreviewCandidateTrackIdsAsync(
            FoldOwner(doc, scope), scope.CurrentUserId, scope.FriendIds);
        return Ok(new ShapePreviewResponse(ids.Count, ids));
    }

    /// <summary>
    /// Precise live count of activities matching the rule document (evidence + rule evaluation,
    /// identical to what saving produces). Heavier than <see cref="PreviewShapeSegment"/>; the
    /// client fires it in the background to refine the fast bbox estimate. Honours request
    /// cancellation so superseded requests stop scanning GPS instead of piling up.
    /// </summary>
    [HttpPost("shapes/preview/exact")]
    [ProducesResponseType<ShapePreviewExactResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> PreviewShapeSegmentExact([FromBody] CreateShapeSegmentRequest request, CancellationToken ct)
    {
        // No shapes → the full exact-shape zero body, so every 200 from this endpoint is one schema (D9 cleanup).
        if (request.ParseRules() is not { } doc || doc.Shapes is not { Count: > 0 })
            return Ok(new ShapePreviewExactResponse(0, Array.Empty<int>(), EffortCount: 0, Truncated: false));

        if (doc.Validate() is { Count: > 0 } errors)
            return BadRequest(ApiError.BadRequest(string.Join("; ", errors)));

        try
        {
            var scope = await _ownerScope.ResolveAsync(request.Owner);
            var result = await _shapeMatcher.PreviewExactTrackIdsAsync(
                FoldOwner(doc, scope), scope.CurrentUserId, scope.FriendIds, ct);
            return Ok(new ShapePreviewExactResponse(result.TrackIds.Count, result.TrackIds, result.EffortCount, result.Truncated));
        }
        catch (OperationCanceledException)
        {
            // Client aborted (a newer paint superseded this request) — nothing to return.
            return StatusCode(499);
        }
    }

    // Fold the resolved owner scope into the rule document (owner is server-authored, never client-authored):
    // strip any incoming owner filter, then add the resolved ids — unless the scope is own-only (null), in
    // which case the owner filter stays absent and the matcher falls back to own-only.
    // Invariant: owner scope has three representations with fixed precedence — doc OwnerFilter (written
    // only here) → legacy match_owner_ids CSV (read-only fallback) → own-only. CandidateSqlFilters.Build
    // and the GET mapping must resolve in that same order; never write owner ids anywhere else.
    private static RuleDocument FoldOwner(RuleDocument doc, OwnerScope scope)
    {
        var filters = doc.TrackFilters.Where(f => f is not OwnerFilter).ToList();
        if (scope.OwnerUserIds != null)
            filters.Add(new OwnerFilter { UserIds = scope.OwnerUserIds.ToList() });
        return doc with { TrackFilters = filters };
    }

    /// <summary>The request's rule document when parseable AND valid; null otherwise (→ <see cref="RulesError"/>).</summary>
    private static RuleDocument? ValidateRules(CreateShapeSegmentRequest request) =>
        request.ParseRules() is { } doc && doc.Validate().Count == 0 ? doc : null;

    private static string RulesError(CreateShapeSegmentRequest request) =>
        request.ParseRules() is { } doc
            ? string.Join("; ", doc.Validate())
            : "Rules document missing or malformed";

    /// <summary>
    /// Sets who can see this segment. Owner-only (SegmentCrud gates on OwnedSegments), and not-yours is
    /// the same 404 as not-there so this cannot enumerate other people's segment ids.
    ///
    /// Visibility is independent of share links: a private segment is still shareable, exactly as a
    /// private activity is — the grant widens past visibility by design.
    /// </summary>
    [HttpPatch("{id}/visibility")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateVisibility(int id, [FromBody] UpdateSegmentVisibilityRequest request)
    {
        // Validated HERE (the activities PATCH precedent): the crud's false also means "not yours /
        // not there", so letting a bad value fall through surfaced as 404 "segment gone" instead of 400.
        if (!Visibilities.IsValid(request.Visibility))
            return BadRequest(ApiError.BadRequest("visibility must be private, friends or public"));

        var ok = await _segmentCrud.UpdateSegmentVisibilityAsync(new SegmentId(id), request.Visibility);
        return ok ? NoContent() : NotFound();
    }

    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteSegment(int id)
    {
        var result = await _segmentCrud.DeleteSegmentAsync(new SegmentId(id));
        if (!result)
            return NotFound();

        return NoContent();
    }

    // Item 25: durably excludes the effort (owner-only; RemoveEffortAsync gates on OwnedSegments →
    // false → 404 for non-owners). No longer a hard delete — the exclusion survives re-analysis.
    [HttpDelete("efforts/{effortId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RemoveEffort(int effortId)
    {
        var result = await _segmentEfforts.RemoveEffortAsync(new GpsInSegmentId(effortId));
        if (!result)
            return NotFound();

        return NoContent();
    }

    // Item 25: restores a durably excluded effort (owner-only, 404 for non-owners).
    [HttpPost("efforts/{effortId}/restore")]
    [RejectCrossSiteBrowserRequests]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RestoreEffort(int effortId)
    {
        var result = await _segmentEfforts.RestoreEffortAsync(new GpsInSegmentId(effortId));
        if (!result)
            return NotFound();

        return NoContent();
    }

    [HttpPost("{id}/analyze")]
    [RejectCrossSiteBrowserRequests] // body is OPTIONAL — an empty cross-site POST binds null and executes
    [ProducesResponseType<AnalysisQueuedResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AnalyzeSegment(int id, [FromBody] AnalyzeSegmentRequest? request = null)
    {
        // 404 (not 403 — no existence leak, matches DeleteSegment). Shape segments are owner-only:
        // their re-analysis rebuilds ALL users' efforts from the invoker's perspective.
        if (!await _segmentAnalysis.CanAnalyzeSegmentAsync(new SegmentId(id)))
            return NotFound(ApiError.NotFound("Segment not found"));

        int threshold = request?.MatchThresholdPercent ?? SegmentAnalysis.DefaultMatchThresholdPercent;
        if (threshold < SegmentAnalysis.MinMatchThresholdPercent || threshold > SegmentAnalysis.MaxMatchThresholdPercent)
            return BadRequest(ApiError.BadRequest($"matchThresholdPercent must be between {SegmentAnalysis.MinMatchThresholdPercent} and {SegmentAnalysis.MaxMatchThresholdPercent}."));

        // Queue the re-analysis so it runs on the shared worker and surfaces in the progress toast,
        // instead of blocking the request. Progress is reported via SegmentAnalysisProgress.
        string userId = _currentUser.GetUserId();
        if (!_operationQueue.TryEnqueue(new OperationQueue.WorkItem
            {
                Type    = OperationQueue.OperationType.SingleSegmentAnalysis,
                UserId  = userId,
                Payload = new SingleSegmentAnalysisPayload(userId, id, threshold)
            }))
        {
            return Conflict(ApiError.Conflict("Operation queue is full"));
        }

        return Accepted(new AnalysisQueuedResponse("Segment analysis queued in background"));
    }

    /// <summary>
    /// Chunked performance data: returns chart data for just the requested efforts (by GPSinSegment
    /// id). The chart's progressive loader calls this in chunks so a popular segment with hundreds of
    /// efforts streams in instead of arriving as one multi-MB payload.
    /// </summary>
    [HttpPost("{id}/performance-data/bulk")]
    [ProducesResponseType<List<EffortPerformanceData>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetSegmentPerformanceDataBulk(int id, [FromBody] PerfBulkRequest request, CancellationToken ct)
    {
        try
        {
            if (request.EffortIds == null || request.EffortIds.Count == 0)
                return Ok(Array.Empty<EffortPerformanceData>());

            if (request.EffortIds.Count > 500)
                return BadRequest(ApiError.BadRequest("Too many effort IDs (max 500)"));

            // 0 = full resolution; positive budgets are clamped like the gps-data endpoint.
            var maxPts = request.MaxPointsPerEffort <= 0 ? 0 : Math.Clamp(request.MaxPointsPerEffort, 10, 5000);

            var data = await _segmentEfforts.GetPerformanceDataForEffortsAsync(
                new SegmentId(id), request.EffortIds.Select(v => new GpsInSegmentId(v)).ToHashSet(), maxPts, ct);
            return Ok(data);
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499);
        }
    }

    [HttpGet("activities/{trackId}/full-track")]
    [ProducesResponseType<List<TrackPointDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFullActivityTrack(int trackId, [FromQuery] int maxPoints = 0, CancellationToken ct = default)
    {
        try
        {
            // Cap unlimited requests so a single call cannot load tens of thousands of GPS rows.
            var clampedMax = maxPoints > 0 ? Math.Min(maxPoints, 5000) : 2000;
            var coordinates = await _activityGps.GetTrackCoordinatesAsync(new TrackId(trackId), clampedMax, ct);
            return Ok(coordinates.Select(TrackPointDto.From).ToList());
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499);
        }
    }

    /// <summary>
    /// Bulk-fetch GPS coordinates for multiple activity tracks in a single query.
    /// Used by the segments map to load all effort tracks at once instead of N+1 fetches.
    /// </summary>
    [HttpPost("activities/bulk-tracks")]
    [ProducesResponseType<Dictionary<string, List<TrackPointDto>>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetBulkActivityTracks([FromBody] BulkTrackRequest request, CancellationToken ct)
    {
        try
        {
            if (request.TrackIds == null || request.TrackIds.Count == 0)
                return BadRequest(ApiError.BadRequest("No track IDs provided"));

            if (request.TrackIds.Count > 1000)
                return BadRequest(ApiError.BadRequest("Too many track IDs (max 1000)"));

            // SA-05: mirror the single-track rule — 0 (or negative) means the 2000-pt cap, NOT full
            // resolution. Otherwise one request could force full-res decode of up to 1000 tracks
            // (hundreds of MB + minutes of CPU on the weak single server).
            var maxPoints = request.MaxPointsPerTrack > 0 ? Math.Min(request.MaxPointsPerTrack, 2000) : 2000;
            var bulkCoords = await _activityGps.GetBulkTrackCoordinatesAsync(
                request.TrackIds, maxPoints, ct);

            // Return as dictionary keyed by trackId (as a string — JS object keys are always strings)
            // for easy JS consumption. This is the payload where precision costs most: bounded at
            // 1000 tracks x 2000 points, so every wasted digit is multiplied two million times.
            var result = bulkCoords.ToDictionary(
                kvp => kvp.Key.ToString(CultureInfo.InvariantCulture),
                kvp => kvp.Value.Select(TrackPointDto.From).ToList(),
                StringComparer.Ordinal);

            return Ok(result);
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499);
        }
    }

    [HttpGet("{id}/efforts-detailed")]
    [ProducesResponseType<List<SegmentEffortSummaryDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSegmentEffortsDetailed(int id, CancellationToken ct)
    {
        try
        {
            var efforts = await _segmentEfforts.GetSegmentEffortsDetailedAsync(new SegmentId(id), ct);

            var effortDtos = efforts.Select(e => new SegmentEffortSummaryDto(
                e.GpsInSegmentId, e.SegmentId, e.SegmentName, e.ActivityId, e.ActivityName, e.ActivityDate.ToWallClockIso(),
                e.ActivityType, e.MatchPercentage, e.Distance, e.ElapsedTime, e.AverageSpeed, e.AverageHeartRate,
                e.AverageCadence, e.AveragePower, e.Elevation, e.GpsIdEntry, e.GpsIdExit)).ToList();

            return Ok(effortDtos);
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499);
        }
    }

    /// <summary>
    /// Get a simplified GPS summary for a segment (for minimap previews).
    /// Returns up to 50 downsampled lat/lng points.
    /// Uses a lightweight projection — does not load the full GPS entity.
    /// </summary>
    [HttpGet("{id}/gps-summary")]
    [Produces("application/json")] // ConditionalJson.ETagged (FileResult); schema declared via metadata (D14 closure)
    [ProducesResponseType<List<LatLngDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSegmentGpsSummary(int id)
    {
        var points = await _segmentCrud.GetSegmentGpsSummaryAsync(new SegmentId(id), maxPoints: 50);
        if (points.Count == 0 && !await SegmentExistsAsync(id))
            return NotFound();

        // Minimap thumbnail — the segments index fires one of these per card on every revisit. The geometry
        // is immutable, so an ETag over the body + short max-age skips the re-fetch within the window.
        return ConditionalJson.ETagged(this,
            points.Select(p => new LatLngDto(GpsPrecision.Coord(p.Lat), GpsPrecision.Coord(p.Lng))).ToList(),
            _jsonOptions.Value.JsonSerializerOptions);
    }

    /// <summary>
    /// Get the drawable outline of a shape-defined segment — zone rectangles AND gate lines (both empty
    /// for polyline segments and pre-rework shape segments). Geometry only; the minimap preview draws
    /// this as the fallback when a shape/gate segment has no matched efforts yet.
    /// </summary>
    [HttpGet("{id}/shapes")]
    [ProducesResponseType<SegmentOutlineDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSegmentShapes(int id)
    {
        var rules = await _segmentCrud.GetSegmentRulesAsync(new SegmentId(id));   // visibility-scoped, no GPS graph
        var doc = rules?.SegmentKind == SegmentKinds.Shapes ? RuleDocument.TryParse(rules.MatchRulesJson) : null;
        var zones = (doc?.ZoneRects() ?? []).Select(r => new ZoneRectDto(
            MinLat: Math.Min(r.South, r.North),
            MaxLat: Math.Max(r.South, r.North),
            MinLong: Math.Min(r.West, r.East),
            MaxLong: Math.Max(r.West, r.East),
            Role: Services.Segments.Rules.ShapeKinds.Zone)).ToList();
        var gates = (doc?.GateLines() ?? []).Select(g => new GateLineDto(
            g.Lat1, g.Lng1, g.Lat2, g.Lng2, Role: Services.Segments.Rules.ShapeKinds.Gate)).ToList();
        return Ok(new SegmentOutlineDto(zones, gates));
    }

    // Preview caps (documented, not silent — see EffortsSummaryDto.Truncated): a popular segment can have
    // hundreds of efforts; the tiny canvas needs only a coarse trace, so cap the count and the per-effort
    // point budget hard.
    private const int PreviewMaxEfforts = 150;
    private const int PreviewMaxPointsPerEffort = 28;

    /// <summary>
    /// Every visible matched effort's simplified track through a shape/gate segment — the list minimap
    /// preview (these segments have no Segment_GPS, so gps-summary is empty for them). Server-cached,
    /// capped, and downsampled; empty for polyline segments (their thumbnail draws gps-summary instead).
    /// </summary>
    [HttpGet("{id}/efforts-summary")]
    [ProducesResponseType<EffortsSummaryDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSegmentEffortsSummary(int id, CancellationToken ct)
    {
        try
        {
            var summary = await _segmentEfforts.GetEffortPolylinesAsync(
                new SegmentId(id), PreviewMaxEfforts, PreviewMaxPointsPerEffort, ct);
            return Ok(summary);
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499);
        }
    }

    /// <summary>
    /// Cheap existence check — stops at the first matching row, no GPS join.
    /// Used to distinguish "segment has no GPS points" from "segment does not exist".
    /// </summary>
    private Task<bool> SegmentExistsAsync(int segmentId) =>
        _segmentCrud.SegmentExistsAsync(new SegmentId(segmentId));
}
