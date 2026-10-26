using System.Security.Cryptography;
using RouteBaseWeb.Constants;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using RouteBaseWeb.Models.Requests;

namespace RouteBaseWeb.Api;

[ApiController]
[Route("api/strava")]
public class StravaApiController : ControllerBase
{
    private readonly IStravaAuthService _auth;
    private readonly ActivityImportProgress _importProgress;
    private readonly OperationQueue _operationQueue;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<StravaApiController> _logger;

    public StravaApiController(
        IStravaAuthService auth,
        ActivityImportProgress importProgress,
        OperationQueue operationQueue,
        ICurrentUserService currentUser,
        ILogger<StravaApiController> logger)
    {
        _auth           = auth;
        _importProgress = importProgress;
        _operationQueue = operationQueue;
        _currentUser    = currentUser;
        _logger         = logger;
    }

    [HttpPost("import-all")]
    [RejectCrossSiteBrowserRequests] // body is OPTIONAL — an empty cross-site POST binds null and executes (PI-01)
    [ProducesResponseType<StravaImportQueuedResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ApiError>(StatusCodes.Status401Unauthorized)]
    // TODO(openapi-review): D20
    [ProducesResponseType<StravaImportConflictBody>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ImportAll([FromBody] ImportAllRequest? request = null)
    {
        if (!await _auth.IsAuthenticatedAsync())
            return Unauthorized(ApiError.Unauthorized("Not authenticated with Strava"));

        string userId = _currentUser.GetUserId();
        if (!_operationQueue.TryReserveExclusiveSlot(userId))
            return Conflict(new StravaImportConflictBody("Import is already running", _importProgress.GetStatus(userId)));

        if (!_operationQueue.TryEnqueue(new OperationQueue.WorkItem
            {
                Type    = OperationQueue.OperationType.ActivityImport,
                UserId  = userId,
                Payload = new ActivityImportPayload(userId, "strava", request?.After)
            }))
        {
            _operationQueue.ReleaseExclusiveSlot(userId);
            return Conflict(ApiError.Conflict("Operation queue is full"));
        }

        _logger.LogInformation("Strava import queued via OperationQueue (after: {After})", request?.After);
        return Accepted(new StravaImportQueuedResponse("Import queued in background"));
    }

    [HttpGet("status")]
    [ProducesResponseType<StravaStatusResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStatus()
    {
        var isAuthenticated = await _auth.IsAuthenticatedAsync();
        var athlete = isAuthenticated ? await _auth.GetAthleteAsync() : null;
        return Ok(new StravaStatusResponse(isAuthenticated, athlete?.Id));
    }

    [HttpPost("auth-url")]
    [ProducesResponseType<StravaAuthUrlResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetAuthUrl(
        [FromBody] StravaAuthUrlRequest request,
        [FromServices] IConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(request.ClientId) || string.IsNullOrWhiteSpace(request.ClientSecret))
            return BadRequest(ApiError.BadRequest("Client ID and Client Secret are required."));

        await _auth.SaveCredentialsAsync(request.ClientId, request.ClientSecret);

        var host = Request.Host.Host;
        string redirectUri;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            var urls = configuration["ASPNETCORE_URLS"]
                ?? configuration["urls"]
                ?? "http://localhost:5054";

            var httpUrl = urls.Split(';')
                .Select(u => u.Trim())
                .FirstOrDefault(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                ?? "http://localhost:5054";

            redirectUri = $"{httpUrl.TrimEnd('/')}/KeysAndData";
        }
        else
        {
            redirectUri = $"{Request.Scheme}://{Request.Host}/KeysAndData";
        }

        // CSRF nonce: echoed back on the callback and verified against the session so a
        // code minted against the victim's client_id can't be injected (PS-02).
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        HttpContext.Session.SetString(SessionKeys.StravaOAuthState, state);

        var authUrl = _auth.GetAuthorizationUrl(redirectUri, request.ClientId, state);
        // Log the redirect target only — the full URL carries the fresh CSRF state nonce (PS-02),
        // which must not end up in logs the moment after it was minted to be a secret.
        _logger.LogInformation("Returning Strava OAuth URL for redirect {RedirectUri}", redirectUri);
        return Ok(new StravaAuthUrlResponse(authUrl));
    }
}
