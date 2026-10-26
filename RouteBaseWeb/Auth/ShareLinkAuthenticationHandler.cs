using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using RouteBaseWeb.Services.Sharing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace RouteBaseWeb.Auth;

/// <summary>
/// Authenticates a share-link visitor from the token in the ROUTE.
///
/// Route-only is the whole no-ambient-authority property: never a header, never a cookie, so a share grant
/// cannot ride along on a request the visitor did not explicitly address to a <c>/s/{token}/…</c> URL.
/// (It also means an <c>&lt;img src&gt;</c> works without any client-side header plumbing — the token is
/// simply part of the URL.) Route values are available here because <c>UseAuthentication</c> runs after
/// <c>UseRouting</c>; that ordering is load-bearing.
///
/// This scheme is deliberately NOT reachable from the "Auto" policy scheme's ForwardDefaultSelector. If it
/// were, HttpContext.User would be a share principal on every endpoint and the global
/// RequireAuthenticatedUser fallback policy would accept it. Left out, a share principal exists only where
/// an endpoint names the scheme explicitly — that IS the default-deny.
/// </summary>
public sealed class ShareLinkAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IShareLinkAccess access)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Context.Request.RouteValues[ShareLinkDefaults.RouteTokenKey] is not string token
            || !ShareTokens.IsWellFormed(token))
            return AuthenticateResult.NoResult();

        var grant = await access.ResolveAsync(token, Context.RequestAborted);
        if (grant is null)
            return AuthenticateResult.NoResult();   // unknown | expired | revoked | malformed row

        // NameIdentifier is the CONSTANT share:anon, never the owner's id — see ShareLinkDefaults.
        // The grant's identity rides in TokenIdClaim and is used only to resolve authority.
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, ShareLinkDefaults.ViewerUserId),
            new Claim("sub", ShareLinkDefaults.ViewerUserId),
            new Claim(ShareLinkDefaults.TokenIdClaim,
                      grant.TokenId.ToString(CultureInfo.InvariantCulture)),
        ], ShareLinkDefaults.AuthenticationScheme);

        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    /// <summary>
    /// A bad, expired or revoked token must be indistinguishable from a wrong URL — 404, never 401 and
    /// never a login redirect. A redirect tells a prober "that token was real, just not live", and hands a
    /// crawler a 302 to follow into the OIDC flow.
    /// </summary>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // Failures carry the share hygiene headers too (share invariant): without no-store, a
        // shared cache could hold this 404 keyed on the /s/{token} URL and a live link would read as
        // permanently broken for everyone behind it.
        ShareResponseHeaders.Apply(Response);
        Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }

    /// <inheritdoc cref="HandleChallengeAsync" />
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        ShareResponseHeaders.Apply(Response);
        Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }
}
