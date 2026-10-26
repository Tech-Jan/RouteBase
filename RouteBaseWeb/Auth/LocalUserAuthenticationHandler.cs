using System.Security.Claims;
using System.Text.Encodings.Web;
using RouteBaseWeb.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace RouteBaseWeb.Auth;

/// <summary>
/// Localhost-mode auth: every request authenticates as the single fixed local user. Registered as the
/// default scheme (in place of Authentik OIDC) only when <see cref="LocalModeOptions.Enabled"/>, so the
/// global <c>RequireAuthenticatedUser</c> fallback policy is satisfied with no login. The stamped claims
/// match what OIDC would supply (<see cref="ClaimTypes.NameIdentifier"/> / <c>sub</c> + display name), so
/// <c>CurrentUserService.GetUserId()</c> and every call site work unchanged.
/// </summary>
public class LocalUserAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, LocalModeOptions.UserId),
            new Claim("sub", LocalModeOptions.UserId),
            new Claim("preferred_username", LocalModeOptions.DisplayName),
            new Claim("name", LocalModeOptions.DisplayName),
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
