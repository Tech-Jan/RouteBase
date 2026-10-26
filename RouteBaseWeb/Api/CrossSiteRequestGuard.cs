using System.Reflection;
using RouteBaseWeb.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace RouteBaseWeb.Api;

/// <summary>
/// A6: rejects requests a browser labels as cross-site. For the multipart/form endpoints this guards
/// (photo batch upload), a hostile page can fire a plain cross-site <c>&lt;form&gt;</c> POST — no CORS
/// preflight applies — and in localhost mode there is no auth cookie, so SameSite offers no protection:
/// any web page could push files into the local app. <c>Sec-Fetch-Site</c> is the authority when the
/// browser sends it; otherwise <c>Origin</c> is compared against the request authority. Clients that
/// send neither (curl, the Android app) pass — they are not CSRF victims. Applied in both modes: server
/// traffic is same-origin, so the check is inert there.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RejectCrossSiteBrowserRequestsAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (IsCrossSite(context.HttpContext.Request))
            context.Result = new ObjectResult(new CrossSiteRejectedBody("Cross-site requests are not allowed."))
            {
                StatusCode = StatusCodes.Status403Forbidden,
            };
    }

    /// <summary>Pure classification, pinned case-by-case in CrossSiteRequestGuardTests.</summary>
    internal static bool IsCrossSite(HttpRequest request)
    {
        // Browsers stamp Sec-Fetch-Site on every request; only "cross-site" is hostile. "same-origin"/
        // "same-site" are app traffic; "none" is user-initiated (address bar, bookmark).
        var fetchSite = request.Headers["Sec-Fetch-Site"].ToString();
        if (fetchSite.Length > 0)
            return string.Equals(fetchSite, "cross-site", StringComparison.OrdinalIgnoreCase);

        // No Sec-Fetch-Site: an older browser (sends Origin on POSTs) or a non-browser client (sends
        // neither header — allowed). Runs after UseForwardedHeaders, so scheme/host are the public ones.
        var origin = request.Headers.Origin.ToString();
        if (origin.Length == 0)
            return false;

        // "null" (sandboxed iframe, file://) and unparseable origins cannot be verified — cross-site.
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
            return true;

        int requestPort = request.Host.Port ?? (request.Scheme == Uri.UriSchemeHttps ? 443 : 80);
        return !string.Equals(originUri.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(originUri.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase)
            || originUri.Port != requestPort;
    }
}

/// <summary>403 body for a rejected cross-site request — <c>{message, code}</c> like every other api/
/// error body (D31/D32 uniformity; this was the last pre-D32 <c>{error}</c> emitter).</summary>
public sealed record CrossSiteRejectedBody(string Message, string Code = ApiErrorCodes.Forbidden);

/// <summary>
/// D1: applies the cross-site browser-request guard to EVERY state-changing verb (POST/PUT/PATCH/DELETE) on
/// the <c>RouteBaseWeb.Api</c> surface, so a new endpoint is covered by default instead of relying on
/// someone remembering the per-action <see cref="RejectCrossSiteBrowserRequestsAttribute"/>. Registered once
/// as a global MVC filter (Program.cs) — one source of truth. The classification is NOT forked: it reuses
/// <see cref="RejectCrossSiteBrowserRequestsAttribute.IsCrossSite"/>.
///
/// Scope (see <see cref="Applies"/>): controllers in the <c>RouteBaseWeb.Api</c> namespace only — the same
/// signal <c>ApiSurfaceGuardTests</c> uses; Razor Pages and other MVC keep their own antiforgery story and
/// are skipped (their descriptor is not a <see cref="ControllerActionDescriptor"/>). Safe verbs
/// (GET/HEAD/OPTIONS) are never cross-site-forgeable state changes, so they pass untouched. An endpoint that
/// legitimately serves cross-site browsers — a webhook or OAuth <c>form_post</c> callback (none exist today,
/// the Strava OAuth callback lands on the <c>/KeysAndData</c> Razor page, not here) — opts out with
/// <see cref="AllowCrossSiteBrowserRequestsAttribute"/>.
///
/// Redundant with the per-action attribute still on some controllers: harmless. Both are authorization
/// filters; the first to reject short-circuits, and an allowed request simply passes both.
/// </summary>
public sealed class CrossSiteApiGuardFilter : IAuthorizationFilter
{
    internal const string ApiNamespace = "RouteBaseWeb.Api";

    private static readonly HashSet<string> StateChangingMethods =
        new(StringComparer.OrdinalIgnoreCase) { "POST", "PUT", "PATCH", "DELETE" };

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (!Applies(context.ActionDescriptor, context.HttpContext.Request.Method))
            return;

        if (RejectCrossSiteBrowserRequestsAttribute.IsCrossSite(context.HttpContext.Request))
            context.Result = new ObjectResult(new CrossSiteRejectedBody("Cross-site requests are not allowed."))
            {
                StatusCode = StatusCodes.Status403Forbidden,
            };
    }

    /// <summary>
    /// Pure predicate (pinned in CrossSiteRequestGuardTests): the global guard covers this request iff it is
    /// a state-changing verb on an api/ controller action that has not opted out. Safe verbs, non-api
    /// controllers, Razor Pages, and opted-out actions all return false.
    /// </summary>
    internal static bool Applies(ActionDescriptor descriptor, string httpMethod)
    {
        if (!StateChangingMethods.Contains(httpMethod))
            return false;
        if (descriptor is not ControllerActionDescriptor action)
            return false;
        // The api surface is RouteBaseWeb.Api AND any sub-namespace of it (RouteBaseWeb.Api.Foo) — an exact
        // match would fail OPEN for a future api controller filed under a sub-namespace (PI-03). The `.`
        // guard keeps a sibling like RouteBaseWeb.ApiThing out.
        var ns = action.ControllerTypeInfo.Namespace;
        if (ns != ApiNamespace && ns?.StartsWith(ApiNamespace + ".", StringComparison.Ordinal) != true)
            return false;
        return action.MethodInfo.GetCustomAttribute<AllowCrossSiteBrowserRequestsAttribute>() is null
            && action.ControllerTypeInfo.GetCustomAttribute<AllowCrossSiteBrowserRequestsAttribute>() is null;
    }
}

/// <summary>
/// D1 escape hatch: marks an api/ action (or controller) that legitimately serves cross-site browser
/// requests — a webhook or OAuth <c>form_post</c> callback — so the global <see cref="CrossSiteApiGuardFilter"/>
/// leaves it alone. None exist today; it is present so a future such endpoint has a documented opt-out
/// rather than a reason to remove the global registration.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class AllowCrossSiteBrowserRequestsAttribute : Attribute;
