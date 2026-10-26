using RouteBaseWeb.Models;
using RouteBaseWeb.Services.TrackEdit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace RouteBaseWeb.Api;

/// <summary>
/// Thin HTTP mapper over the TrackEdit session services (A2 §2). Owner-scoped throughout — a
/// foreign/missing session or commit is 404 (never 403; existence is not leaked, ReviewApi precedent).
/// Expected failures return a typed <see cref="TrackEditErrorBody"/> with the right status; unexpected
/// ones hit <c>ApiExceptionFilter</c> (no try/catch here). Save/revert/commit actions land in I2c.
/// </summary>
[ApiController]
[Authorize]
[Route("api")]
public sealed class TrackEditApiController : ControllerBase
{
    private readonly EditSessionService _sessions;
    private readonly TrackEditSaveService _save;
    private readonly TrackEditRevertService _revert;
    private readonly EditSuggestionService _suggestions;

    public TrackEditApiController(
        EditSessionService sessions, TrackEditSaveService save, TrackEditRevertService revert,
        EditSuggestionService suggestions)
    {
        _sessions = sessions;
        _save = save;
        _revert = revert;
        _suggestions = suggestions;
    }

    [HttpPost("edit-sessions")]
    [ProducesResponseType<SessionStateResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<TrackEditErrorBody>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<TrackEditErrorBody>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<TrackEditErrorBody>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create([FromBody] CreateSessionRequest request, CancellationToken ct) =>
        Map(await _sessions.CreateAsync(request, ct));

    [HttpGet("edit-sessions/{id}")]
    [ProducesResponseType<SessionStateResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<TrackEditErrorBody>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<TrackEditErrorBody>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<TrackEditErrorBody>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Get(string id, CancellationToken ct) =>
        Map(await _sessions.GetAsync(id, ct));

    [HttpPost("edit-sessions/{id}/ops")]
    [ProducesResponseType<SessionStateResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<TrackEditErrorBody>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<TrackEditErrorBody>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<TrackEditErrorBody>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> AppendOps(string id, [FromBody] AppendOpsRequest request, CancellationToken ct) =>
        Map(await _sessions.AppendOpsAsync(id, request, ct));

    [HttpPost("edit-sessions/{id}/cursor")]
    [ProducesResponseType<SessionStateResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<TrackEditErrorBody>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<TrackEditErrorBody>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<TrackEditErrorBody>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> MoveCursor(string id, [FromBody] MoveCursorRequest request, CancellationToken ct) =>
        Map(await _sessions.MoveCursorAsync(id, request, ct));

    [HttpGet("edit-sessions/{id}/save-preflight")]
    [ProducesResponseType<SavePreflightResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<TrackEditErrorBody>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Preflight(string id, [FromQuery] string? mode, CancellationToken ct)
    {
        if (!TrackEditSaveModes.IsValid(mode)) return BadRequest(new TrackEditErrorBody("badMode"));
        var response = await _sessions.PreflightAsync(id, mode, ct);
        return response is null ? NotFound() : Ok(response);
    }

    [HttpPost("edit-sessions/{id}/discard")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<TrackEditErrorBody>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<TrackEditErrorBody>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<TrackEditErrorBody>(StatusCodes.Status422UnprocessableEntity)]
    [RejectCrossSiteBrowserRequests] // route-only POST — cross-site form/fetch-able without preflight (PI-01)
    public async Task<IActionResult> Discard(string id, CancellationToken ct)
    {
        var result = await _sessions.DiscardAsync(id, ct);
        return result.ErrorKind is null ? NoContent() : MapError(result.ErrorKind.Value, result.Error);
    }

    [HttpGet("edit-sessions/{id}/sources")]
    [ProducesResponseType<SessionSourcesResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<TrackEditErrorBody>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<TrackEditErrorBody>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<TrackEditErrorBody>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Sources(string id, [FromQuery] string? ids = null, CancellationToken ct = default)
    {
        var result = await _sessions.GetSourcesAsync(id, ids, ct);
        return result.ErrorKind is null ? Ok(result.Response) : MapError(result.ErrorKind.Value, result.Error);
    }

    /// <summary>Add-track offers (A4 §3.1): proximity (end ↔ candidate start ≤ 1 km) ∪ same-day, over the
    /// owner's confirmed activities, session-loaded ids excluded. All query params optional (absent class
    /// contributes nothing); owner-scoped 404 like every session endpoint.</summary>
    [HttpGet("edit-sessions/{id}/track-suggestions")]
    [ProducesResponseType<TrackSuggestionsResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> TrackSuggestions(
        string id, [FromQuery] double? endLat, [FromQuery] double? endLng, [FromQuery] string? localDate,
        CancellationToken ct)
    {
        var response = await _suggestions.GetSuggestionsAsync(id, endLat, endLng, localDate, ct);
        return response is null ? NotFound() : Ok(response);
    }

    /// <summary>Add-track search (A4 §3.1): name substring and/or date over the owner's confirmed activities,
    /// loaded ids flagged. At least one of <paramref name="q"/>/<paramref name="date"/> required (else 400).</summary>
    [HttpGet("edit-sessions/{id}/track-search")]
    [ProducesResponseType<TrackSuggestionsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<TrackEditErrorBody>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> TrackSearch(
        string id, [FromQuery] string? q, [FromQuery] string? date, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q) && string.IsNullOrWhiteSpace(date))
            return BadRequest(new TrackEditErrorBody("missingQuery"));
        var response = await _suggestions.SearchAsync(id, q, date, ct);
        return response is null ? NotFound() : Ok(response);
    }

    [HttpPost("edit-sessions/{id}/save")]
    [ProducesResponseType<SaveResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<TrackEditErrorBody>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<TrackEditErrorBody>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<TrackEditErrorBody>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Save(string id, [FromBody] SaveRequest request, CancellationToken ct)
    {
        var result = await _save.SaveAsync(id, request.Mode, request.ExpectedCounts ?? [], ct);
        return result.ErrorKind is null ? Ok(result.Response) : MapError(result.ErrorKind.Value, result.Error);
    }

    [HttpGet("edit-commits/{commitId:int}")]
    [ProducesResponseType<CommitSummaryResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> CommitSummary(int commitId, CancellationToken ct)
    {
        var summary = await _revert.GetSummaryAsync(new CommitId(commitId), ct);
        return summary is null ? NotFound() : Ok(summary);
    }

    [HttpPost("edit-commits/{commitId:int}/revert")]
    [ProducesResponseType<RevertResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<TrackEditErrorBody>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<TrackEditErrorBody>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<TrackEditErrorBody>(StatusCodes.Status422UnprocessableEntity)]
    [RejectCrossSiteBrowserRequests]
    public async Task<IActionResult> Revert(int commitId, CancellationToken ct)
    {
        var result = await _revert.RevertAsync(new CommitId(commitId), ct);
        return result.ErrorKind is null ? Ok(result.Response) : MapError(result.ErrorKind.Value, result.Error);
    }

    private IActionResult Map(SessionActionResult result) =>
        result.ErrorKind is null ? Ok(result.Response) : MapError(result.ErrorKind.Value, result.Error);

    // TODO(openapi-review): D19
    private IActionResult MapError(TrackEditErrorKind kind, TrackEditErrorBody? body) => kind switch
    {
        TrackEditErrorKind.NotFound => NotFound(),
        TrackEditErrorKind.Conflict => Conflict(body),
        TrackEditErrorKind.BadRequest => BadRequest(body),
        TrackEditErrorKind.Unprocessable => UnprocessableEntity(body),
        _ => StatusCode(500),
    };
}
