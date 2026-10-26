using RouteBaseWeb.Helpers;
using RouteBaseWeb.Auth;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Sharing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace RouteBaseWeb.Api;

/// <summary>
/// Owner-facing management of share links: mint, list, revoke.
///
/// This controller is NOT part of the share read surface — it is never reachable with a share token. The
/// MintPolicy admits only an interactive human session, so neither an API key nor a share principal can
/// mint links (see ShareLinkDefaults.MintPolicy).
///
/// Every failure is 404, never 403: distinguishing "not yours" from "does not exist" would turn these
/// endpoints into an existence oracle for other people's activities and segments.
///
/// CSRF: there is no antiforgery token here, and that is load-bearing on three things holding TOGETHER —
/// if any one is changed, add one. (1) Both actions require a non-simple request: POST binds [FromBody]
/// JSON (Content-Type: application/json) and DELETE is a non-simple method, so a cross-origin attempt
/// triggers a CORS preflight. (2) The app registers no CORS policy, so that preflight is refused. (3) The
/// session cookie is SameSite=Lax (Auth/ApiCookieRedirectEvents), so it does not ride a cross-site POST
/// anyway. The outcome being defended is "a page you visit silently mints a public link to your private
/// rides", so this is worth re-checking whenever CORS or the cookie policy moves.
/// </summary>
[ApiController]
[Route("api/sharelinks")]
[Authorize(Policy = ShareLinkDefaults.MintPolicy)]
public class ShareLinksController(
    IShareLinkService shareLinks,
    ICurrentUserService currentUser) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<ShareLinkMintResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ShareErrorResponse>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Mint(
        [FromBody] ShareLinkMintRequest request, CancellationToken ct)
    {
        var result = await shareLinks.MintAsync(
            request.Kind, request.ResourceId, request.Ttl, currentUser.GetUserId(), ct);

        return result.Outcome switch
        {
            ShareMintOutcome.Ok => Ok(new ShareLinkMintResponse(
                result.Id,
                // Relative on purpose — Request.Host is spoofable here, so an absolute URL built from it
                // could hand the owner a link pointing at someone else's domain.
                $"{ShareUrlRedaction.SharePathPrefix}{result.Token}",
                result.ExpiresAt.ToUtcOffset())),
            ShareMintOutcome.BadRequest =>
                BadRequest(new ShareErrorResponse("Unknown share kind or lifetime.")),
            ShareMintOutcome.TooMany =>
                BadRequest(new ShareErrorResponse("Too many active links for this item.")),
            _ => NotFound(),
        };
    }

    [HttpGet]
    [ProducesResponseType<ShareLinkListResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] string kind, [FromQuery] int resourceId, CancellationToken ct)
    {
        // The response is built in the service: the listed rows are capped, but TotalOpens covers every
        // link the resource ever had, so only the service can say both.
        return Ok(await shareLinks.ListAsync(kind, resourceId, currentUser.GetUserId(), ct));
    }

    /// <summary>
    /// Everything the caller is publishing RIGHT NOW, across all four kinds — the question the per-resource
    /// List above structurally cannot answer, since it demands a kind and an id you must already suspect.
    /// Owner-scoped in the service's WHERE clause, like every other action here.
    /// </summary>
    [HttpGet("mine")]
    [ProducesResponseType<ShareLinkInventoryResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Mine(CancellationToken ct)
        => Ok(await shareLinks.ActiveLinksAsync(currentUser.GetUserId(), ct));

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Revoke(int id, CancellationToken ct)
        => await shareLinks.RevokeAsync(id, currentUser.GetUserId(), ct)
            ? NoContent()
            : NotFound();
}
