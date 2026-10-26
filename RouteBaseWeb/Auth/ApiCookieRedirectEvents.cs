using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace RouteBaseWeb.Auth;

/// <summary>
/// CC-01: an /api/* caller is a fetch(), not a navigation. The cookie handler's default answer to
/// Challenge/Forbid is a 302 to the Login/AccessDenied HTML page — which fetch() silently FOLLOWS and
/// reads as 200 ok, so a refused write is reported to the user as saved. Answer /api/* with the status
/// code instead; page navigations keep the redirect.
/// (Requests carrying X-Api-Key resolve through the "ApiKey" scheme, which owns its own 401/403 — this
/// never sees them.)
/// </summary>
public static class ApiCookieRedirectEvents
{
    // /hubs too: SignalR's negotiate is a fetch() as well — the 302→login-HTML answer surfaced as an
    // opaque handshake error instead of a clean 401 the client can route to re-login.
    public static bool IsApiRequest(HttpRequest request) =>
        request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
        || request.Path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase);

    public static void Configure(CookieAuthenticationOptions options)
    {
        // Pin explicitly rather than rely on the framework default (also Lax today) — a later change to
        // that default, or a copy-paste to None for some other cookie, must not silently widen this one.
        // No antiforgery token on /api (deferred): Lax is the only CSRF mitigation this cookie has.
        options.Cookie.SameSite = SameSiteMode.Lax;
        // Secure pinned for the same reason: SameAsRequest makes the attribute contingent on
        // forwarded-proto handling. This configurer runs only in server mode (localhost mode never
        // registers cookie auth), and the server is always HTTPS-fronted.
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Events.OnRedirectToAccessDenied = ctx => Answer(ctx, StatusCodes.Status403Forbidden);
        options.Events.OnRedirectToLogin = ctx => Answer(ctx, StatusCodes.Status401Unauthorized);
    }

    public static Task Answer(RedirectContext<CookieAuthenticationOptions> ctx, int apiStatusCode)
    {
        if (IsApiRequest(ctx.Request))
            ctx.Response.StatusCode = apiStatusCode;
        else
            ctx.Response.Redirect(ctx.RedirectUri);
        return Task.CompletedTask;
    }
}
