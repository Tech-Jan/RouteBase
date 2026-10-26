using RouteBaseWeb.Helpers;
using RouteBaseWeb.Hubs;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Recording;
using RouteBaseWeb.Models.Requests;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Sharing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace RouteBaseWeb.Api;

[ApiController]
[Route("api/recording")]
public class RecordingApiController : ControllerBase
{
    private readonly IRecordingService _recordingService;
    private readonly IShareLinkService _shareLinks;
    private readonly RecordingFinalizationCoordinator _finalization;
    private readonly ICurrentUserService _currentUser;
    private readonly IVisibilityService _visibilityService;
    private readonly IRecordingRateLimiter _limiter;
    private readonly IHubContext<RecordingHub> _hubContext;
    private readonly ILiveSessionDirectory _liveSessions;
    private readonly ILiveViewerRegistry _viewers;
    private readonly ILogger<RecordingApiController> _logger;
    private readonly TimeProvider _clock;

    public RecordingApiController(
        IRecordingService recordingService,
        IShareLinkService shareLinks,
        RecordingFinalizationCoordinator finalization,
        ICurrentUserService currentUser,
        IVisibilityService visibilityService,
        IRecordingRateLimiter limiter,
        IHubContext<RecordingHub> hubContext,
        ILiveSessionDirectory liveSessions,
        ILiveViewerRegistry viewers,
        ILogger<RecordingApiController> logger,
        TimeProvider clock)
    {
        _recordingService = recordingService;
        _shareLinks = shareLinks;
        _finalization = finalization;
        _currentUser = currentUser;
        _visibilityService = visibilityService;
        _limiter = limiter;
        _hubContext = hubContext;
        _liveSessions = liveSessions;
        _viewers = viewers;
        _logger = logger;
        _clock = clock;
    }

    // POST /api/recording/sessions
    [HttpPost("sessions")]
    [ProducesResponseType<RecordingSessionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> StartSession([FromBody] StartRecordingRequest request)
    {
        if (RecordingRequestValidator.Validate(request) is { } err)
            return BadRequest(ApiError.BadRequest(err));

        var userId = _currentUser.GetUserId();
        var session = await _recordingService.StartSessionAsync(request, userId);

        if (session is null)
            return Conflict(ApiError.Conflict("You already have an active recording session. Stop or abandon it first."));

        return Ok(RecordingSessionDto.From(session));
    }

    private const int MaxPointsPerBatch = 500;

    // Caller-scoped limiter key — see the TryAcquire comment in AppendPoints. Only the owner ever
    // acquires or clears (all three Clear sites sit behind owner-gated service calls).
    private static string LimiterKey(string userId, string sessionId) => $"{userId}|{sessionId}";

    // POST /api/recording/sessions/{sessionId}/points
    [HttpPost("sessions/{sessionId}/points")]
    [ProducesResponseType<AppendPointsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiError>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> AppendPoints(string sessionId, [FromBody] RecordingPointsBatch batch)
    {
        if (batch.Points.Count > MaxPointsPerBatch)
            return BadRequest(ApiError.BadRequest($"Batch too large: max {MaxPointsPerBatch} points per request."));

        if (RecordingRequestValidator.Validate(batch, _clock.GetUtcNow().UtcDateTime) is { } err)
            return BadRequest(ApiError.BadRequest(err));

        var userId = _currentUser.GetUserId();

        // Rate limit on the server's receipt clock (not client GPS timestamps, which can be skewed).
        // Atomic check-and-reserve (RS-04): reserving before the append means a failed append burns the
        // 1 s window, which is acceptable (the next batch is ≥1 s later anyway). Keyed per CALLER, not
        // per session: sessionIds are viewer-visible (sessions/live), so a session-keyed window would
        // let any non-owner burn it and 429-starve the recording phone.
        if (!_limiter.TryAcquire(LimiterKey(userId, sessionId)))
            return StatusCode(429, ApiError.RateLimited("Rate limit: max 1 batch per second per session."));
        var points = await _recordingService.AppendPointsAsync(sessionId, batch, userId);

        if (points is null)
            return NotFound(ApiError.NotFound("Session not found, not owned by you, or not active."));

        // Broadcast new points to live viewers via SignalR. ToList, not the deferred Select: the hub
        // protocol serializes lazily, with no length lookahead and no ICollection fast path
        // (LV-03: never hand a deferred Select to a response body).
        if (points.Count > 0)
        {
            var payload = points.Select(LivePointDto.From).ToList();
            await _hubContext.Clients
                .Group(RecordingHub.GroupFor(sessionId))
                .SendAsync("ReceivePoints", sessionId, payload);
        }

        // AQ-S1: echo the post-append LastSequence so a reconnecting/409-adopting client can resync its
        // clientSequence — a dedup-eaten batch (inserted == 0) is then distinguishable from a stored one
        // by lastSequence >= the seq it sent. GetSessionAsync returns the entity already tracked by the
        // append above (same scoped context), so this is no extra query. points != null proved the
        // session existed at append time — but only for that snapshot: an abandon+delete racing in
        // another request can remove the row between the awaits, so a null here is "session just
        // deleted", not a bug. The points were stored and broadcast; answer as the append paths do.
        var refreshed = await _recordingService.GetSessionAsync(sessionId);
        if (refreshed is null)
            return NotFound(ApiError.NotFound("Session not found, not owned by you, or not active."));
        var lastSequence = refreshed.LastSequence;
        // Watched: is anyone on the live view right now? The phone uploads every 5 s only while someone is
        // watching, and drops to a 60 s cadence otherwise — at 5 s the LTE modem's tail timer never expires,
        // so the radio never idles for the whole ride.
        return Ok(new AppendPointsResponse(points.Count, lastSequence, _viewers.IsWatched(sessionId)));
    }

    // POST /api/recording/sessions/{sessionId}/pause
    [HttpPost("sessions/{sessionId}/pause")]
    [ProducesResponseType<RecordingSessionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    [RejectCrossSiteBrowserRequests] // route-only POSTs: cross-site form/fetch-able without preflight (PI-01).
                                     // The phone (OkHttp) sends no browser headers and passes untouched.
    public async Task<IActionResult> PauseSession(string sessionId)
    {
        var userId = _currentUser.GetUserId();
        var session = await _recordingService.PauseSessionAsync(sessionId, userId);

        if (session is null)
            return NotFound(ApiError.NotFound("Session not found, not owned by you, or not in 'recording' status."));

        await _hubContext.Clients
            .Group(RecordingHub.GroupFor(sessionId))
            .SendAsync("SessionPaused", sessionId);

        return Ok(RecordingSessionDto.From(session));
    }

    // POST /api/recording/sessions/{sessionId}/resume
    [HttpPost("sessions/{sessionId}/resume")]
    [ProducesResponseType<RecordingSessionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    [RejectCrossSiteBrowserRequests]
    public async Task<IActionResult> ResumeSession(string sessionId)
    {
        var userId = _currentUser.GetUserId();
        var session = await _recordingService.ResumeSessionAsync(sessionId, userId);

        if (session is null)
            return NotFound(ApiError.NotFound("Session not found, not owned by you, or not in 'paused' status."));

        await _hubContext.Clients
            .Group(RecordingHub.GroupFor(sessionId))
            .SendAsync("SessionResumed", sessionId);

        return Ok(RecordingSessionDto.From(session));
    }

    // POST /api/recording/sessions/{sessionId}/stop
    [HttpPost("sessions/{sessionId}/stop")]
    [ProducesResponseType<RecordingSessionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    [RejectCrossSiteBrowserRequests]
    public async Task<IActionResult> StopSession(string sessionId)
    {
        var userId = _currentUser.GetUserId();
        var session = await _recordingService.StopSessionAsync(sessionId, userId);

        if (session is null)
            return NotFound(ApiError.NotFound("Session not found or not owned by you."));

        _limiter.Clear(LimiterKey(userId, sessionId));

        // Finalize stats (distance, ascent, descent, time, speed) before notifying viewers.
        // Finalization failure is non-fatal: the session is already stopped.
        // CancellationToken.None: a client disconnect/timeout must not abandon an in-flight finalize —
        // the retry request then coalesces onto this same execution instead of racing a second one.
        try
        {
            await _finalization.FinalizeAsync(sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Finalization failed for session {SessionId}; stats may be missing", sessionId);
        }

        // Notify live viewers after finalization so the "View Activity" link has computed stats.
        await _hubContext.Clients
            .Group(RecordingHub.GroupFor(sessionId))
            .SendAsync("SessionStopped", sessionId, session.TrackId);

        // API-12 (accepted, latent): `session` was fetched by StopSessionAsync BEFORE FinalizeAsync ran, so
        // this body is the pre-finalize snapshot — stats fields are whatever they were at the moment of
        // stopping, not the just-computed ones. The web client reloads the page rather than trusting this
        // response for stats; a caller that doesn't would need synchronous finalize or poll semantics instead.
        return Ok(RecordingSessionDto.From(session));
    }

    // POST /api/recording/sessions/{sessionId}/retry-finalize
    // RF-02 manual Retry: re-arm a 'finalize_failed' session and attempt finalization once, now. On failure
    // it is re-parked at 'finalize_failed' so the recent-sessions row keeps its Retry/Discard actions.
    [HttpPost("sessions/{sessionId}/retry-finalize")]
    [ProducesResponseType<FinalizeRetryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    [RejectCrossSiteBrowserRequests]
    public async Task<IActionResult> RetryFinalize(string sessionId)
    {
        var userId = _currentUser.GetUserId();
        if (!await _recordingService.RetryFinalizeFailedAsync(sessionId, userId))
            return NotFound(ApiError.NotFound("Session not found, not owned by you, or not in 'finalize_failed' status."));

        try
        {
            await _finalization.FinalizeAsync(sessionId);
            return Ok(new FinalizeRetryResponse(true));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual finalize retry failed for session {SessionId}; re-parking", sessionId);
            await _recordingService.MarkFinalizeFailedAsync(sessionId, userId);
            return Ok(new FinalizeRetryResponse(false));
        }
    }

    // POST /api/recording/sessions/{sessionId}/abandon
    [HttpPost("sessions/{sessionId}/abandon")]
    [ProducesResponseType<RecordingSessionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    [RejectCrossSiteBrowserRequests]
    public async Task<IActionResult> AbandonSession(string sessionId)
    {
        var userId = _currentUser.GetUserId();
        var session = await _recordingService.AbandonSessionAsync(sessionId, userId);

        if (session is null)
            return NotFound(ApiError.NotFound("Session not found or not owned by you."));

        _limiter.Clear(LimiterKey(userId, sessionId));

        await _hubContext.Clients
            .Group(RecordingHub.GroupFor(sessionId))
            .SendAsync("SessionStopped", sessionId, session.TrackId);

        return Ok(RecordingSessionDto.From(session));
    }

    // POST /api/recording/sessions/{sessionId}/share-live
    // AND-24: let the phone (an ApiKey principal) mint a PUBLIC live-share (/s/{token}) for its OWN active
    // session. Deliberately NOT on ShareLinksController — that controller's MintPolicy bars an API key from
    // minting ANY link (a leaked key must never become a login-free window into the owner's history). This
    // seam is narrower by construction: it hardcodes kind "live" and the caller's own active session, so the
    // only thing an API key can produce here is a 24h map of a ride it is actively recording. MintPolicy and
    // ShareLinkDefaultDenyTests are unchanged.
    [HttpPost("sessions/{sessionId}/share-live")]
    [ProducesResponseType<ShareLinkMintResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    [RejectCrossSiteBrowserRequests]
    public async Task<IActionResult> ShareLive(string sessionId, CancellationToken ct)
    {
        var userId = _currentUser.GetUserId();

        var session = await _recordingService.GetSessionAsync(sessionId);
        // 404, never 403: "not yours" and "does not exist" must be indistinguishable (no existence oracle),
        // matching ShareLinksController's convention.
        if (session is null || session.UserId != userId)
            return NotFound(ApiError.NotFound("Session not found or not owned by you."));

        if (session.Status is not (RecordingSessionStatus.Recording or RecordingSessionStatus.Paused))
            return Conflict(ApiError.Conflict("Session is not active."));

        // Live-only, 24h, owner pinned by the userId PARAMETER (not ambient identity). MintAsync is not
        // MintPolicy-gated — the policy lives on ShareLinksController, not on the service — which is exactly
        // what lets this narrow, owner-scoped path exist without widening the API key's powers.
        var result = await _shareLinks.MintAsync(
            ShareKindNames.Live, session.TrackId.Value, ShareTtl.Day.Name, userId, ct);

        return result.Outcome switch
        {
            ShareMintOutcome.Ok => Ok(new ShareLinkMintResponse(
                result.Id,
                // Relative on purpose — Request.Host is spoofable behind UseForwardedHeaders.
                $"{ShareUrlRedaction.SharePathPrefix}{result.Token}",
                result.ExpiresAt.ToUtcOffset())),
            ShareMintOutcome.TooMany =>
                BadRequest(ApiError.BadRequest("Too many active share links for this ride.")),
            // NotFound/BadRequest both collapse to 404 here: the owner+has-session checks above already
            // passed, so a Mint miss means the ride is not in a mintable state — same non-oracle answer.
            _ => NotFound(ApiError.NotFound("Session not found or not owned by you.")),
        };
    }

    // GET /api/recording/sessions/active
    [HttpGet("sessions/active")]
    [ProducesResponseType<RecordingSessionDto[]>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetActiveSessions()
    {
        var userId = _currentUser.GetUserId();
        var session = await _recordingService.GetActiveSessionAsync(userId);
        return Ok(session is null ? Array.Empty<RecordingSessionDto>() : new[] { RecordingSessionDto.From(session) });
    }

    // GET /api/recording/sessions/live
    // Every live session the caller may watch (own + friends'), named for display. Unlike
    // sessions/active (self only), this is what the LiveView picker polls to catch someone
    // who started streaming after the page loaded.
    // ToList, not a deferred Select handed to Ok(): STJ would enumerate the iterator lazily with no
    // length lookahead and no ICollection fast path (LV-03). Every viewer
    // polls this every 30 s.
    [HttpGet("sessions/live")]
    [ProducesResponseType<List<LiveSessionDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetLiveSessions()
        => Ok((await _liveSessions.GetVisibleAsync()).Select(LiveSessionDto.From).ToList());

    // GET /api/recording/sessions/{sessionId}
    [HttpGet("sessions/{sessionId}")]
    [ProducesResponseType<RecordingSessionDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSession(string sessionId)
    {
        var session = await _recordingService.GetSessionAsync(sessionId);
        if (session is null) return NotFound();

        // Per-item visibility check: own session, public, or a friend's non-private session. Not-visible
        // answers the SAME 404 as not-there (the surface's no-oracle convention — DeleteSession, ShareLinks,
        // TrackEdit): a 403 confirmed a private session id exists and its owner is recording right now.
        if (!await _visibilityService.CanViewItemAsync(session.UserId, session.Visibility))
            return NotFound();

        return Ok(RecordingSessionDto.From(session));
    }

    // GET /api/recording/sessions/{sessionId}/points?after={seq}
    // The cursor is the dense staging seq, not a timestamp (LV-01): ts_utc is stored truncated to whole
    // seconds, so `ts > cursor` skipped every remaining point of the cursor's own second. -1 = everything.
    [HttpGet("sessions/{sessionId}/points")]
    [ProducesResponseType<List<LivePointDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPoints(string sessionId, [FromQuery] long? after)
    {
        var session = await _recordingService.GetSessionAsync(sessionId);
        if (session is null) return NotFound();

        // Same no-oracle 404 as GetSession.
        if (!await _visibilityService.CanViewItemAsync(session.UserId, session.Visibility))
            return NotFound();

        var points = await _recordingService.GetPointsAfterSeqAsync(sessionId, after ?? -1);

        // ToList (LV-03: no deferred Select in a response body). Fetched once per watched rider per page load, and once
        // per rider on every SignalR reconnect.
        return Ok(points.Select(LivePointDto.From).ToList());
    }

    // DELETE /api/recording/sessions/{sessionId}
    [HttpDelete("sessions/{sessionId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteSession(string sessionId)
    {
        var userId = _currentUser.GetUserId();
        var deleted = await _recordingService.DeleteAbandonedSessionAsync(sessionId, userId);

        if (!deleted)
            return NotFound(ApiError.NotFound("Session not found, not owned by you, or not in 'abandoned' status."));

        _limiter.Clear(LimiterKey(userId, sessionId));

        return NoContent();
    }

}
