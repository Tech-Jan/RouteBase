using RouteBaseWeb.Auth;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace RouteBaseWeb.Api;

[ApiController]
[Route("api/apikeys")]
// Policy, not a named scheme: the cookie scheme does not exist in local mode, where naming it threw on
// every call (API-03). The policy also locks out API-key and share principals — keys cannot manage keys.
[Authorize(Policy = InteractiveUserPolicy.Name)]
public class ApiKeyController(
    IApiKeyService apiKeyService,
    ICurrentUserService currentUser,
    ILogger<ApiKeyController> logger)
    : ControllerBase
{
    [HttpGet("status")]
    [ProducesResponseType<ApiKeyStatusResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStatus()
    {
        var userId = currentUser.GetUserId();
        var status = await apiKeyService.GetStatusAsync(userId);
        return Ok(new ApiKeyStatusResponse(status.HasKey, status.CreatedAt));
    }

    [HttpPost("generate")]
    [RejectCrossSiteBrowserRequests] // bodyless POST — a drive-by regenerate would break the phone's key (PI-01)
    [ProducesResponseType<ApiKeyGenerateResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Generate()
    {
        var userId = currentUser.GetUserId();
        var result = await apiKeyService.GenerateAsync(userId);
        return Ok(new ApiKeyGenerateResponse(result.Plaintext, result.CreatedAt));
    }

    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Revoke()
    {
        var userId = currentUser.GetUserId();
        await apiKeyService.RevokeAsync(userId);
        return NoContent();
    }
}
