using System.Reflection;
using RouteBaseWeb.Api;
using RouteBaseWeb.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// Convention pins for the whole Api/ surface, so the next endpoint cannot ship without them (the same
/// philosophy as the share limiter keying off the route value):
///
/// 1. D1: every state-changing verb (POST/PUT/PATCH/DELETE) on the api surface is covered by the global
///    <see cref="CrossSiteApiGuardFilter"/> by default — no per-endpoint attribute to forget. Two guards: the
///    filter's namespace scope must actually span every [ApiController] (else it silently misses one), and
///    every mutating action must resolve to covered. (Supersedes the old per-attribute pin — the guarantee
///    is now structural, not remembered case-by-case.)
/// 2. No controller may authorize by NAMING an authentication scheme that local mode doesn't register —
///    the ApiKeyController did, and every /api/apikeys call threw in local mode (API-03).
/// 3. No controller or action opts out of the authenticated-by-default FallbackPolicy with
///    <see cref="AllowAnonymousAttribute"/> — a single stray one would silently open one endpoint to
///    unauthenticated callers, the "one attribute opens everything" regression these pins exist to catch.
/// </summary>
public class ApiSurfaceGuardTests
{
    private static readonly string[] StateChangingVerbs = ["POST", "PUT", "PATCH", "DELETE"];

    private static IEnumerable<Type> ApiControllers() =>
        typeof(RejectCrossSiteBrowserRequestsAttribute).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(ControllerBase))
                     && t.GetCustomAttribute<ApiControllerAttribute>() is not null);

    // The verbs an action responds to that are state-changing (from its [HttpPost]/[HttpDelete]/… attribute).
    private static IEnumerable<string> StateChangingVerbsOf(MethodInfo action) =>
        action.GetCustomAttributes<HttpMethodAttribute>()
            .SelectMany(a => a.HttpMethods)
            .Where(v => StateChangingVerbs.Contains(v, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void EveryApiController_LivesInTheNamespace_TheGlobalGuardScopesTo()
    {
        var strays = ApiControllers()
            .Where(t => t.Namespace != CrossSiteApiGuardFilter.ApiNamespace)
            .Select(t => t.FullName!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(strays.Count == 0,
            $"[ApiController]s outside {CrossSiteApiGuardFilter.ApiNamespace} — the global cross-site guard "
            + "silently does NOT cover them: " + string.Join(", ", strays));
    }

    [Fact]
    public void EveryStateChangingApiAction_IsCoveredByTheGlobalCrossSiteGuard()
    {
        var checkedAny = false;
        var gaps = new List<string>();

        foreach (var controller in ApiControllers())
        foreach (var action in controller.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
        {
            var descriptor = new ControllerActionDescriptor
            {
                ControllerTypeInfo = controller.GetTypeInfo(),
                MethodInfo = action,
            };
            foreach (var verb in StateChangingVerbsOf(action))
            {
                checkedAny = true;
                if (!CrossSiteApiGuardFilter.Applies(descriptor, verb))
                    gaps.Add($"{controller.Name}.{action.Name} [{verb}]");
            }
        }

        Assert.True(checkedAny, "No state-changing api actions were found — the scan is broken.");
        Assert.True(gaps.Count == 0,
            "State-changing api actions NOT covered by the global cross-site guard: " + string.Join(", ", gaps));
    }

    [Fact]
    public void NoApiController_AuthorizesByNamingAnAuthenticationScheme_ExceptTheShareSurface()
    {
        // The ShareLink scheme is registered in BOTH modes, so naming it is safe — it is the one
        // deliberate exception (the share surface's whole design). Anything else (Cookie, OIDC) exists
        // only in server mode and throws in local mode.
        var offenders = typeof(RejectCrossSiteBrowserRequestsAttribute).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(ControllerBase)) && t.Namespace == "RouteBaseWeb.Api")
            .Where(t => t.GetCustomAttributes<AuthorizeAttribute>()
                .Any(a => a.AuthenticationSchemes is { Length: > 0 } s
                       && s != ShareLinkDefaults.AuthenticationScheme))
            .Select(t => t.Name)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Controllers naming a mode-dependent authentication scheme: " + string.Join(", ", offenders));
    }

    [Fact]
    public void NoApiControllerOrAction_OptsOutOfAuthentication_WithAllowAnonymous()
    {
        // The whole api surface is authenticated by the global FallbackPolicy (RequireAuthenticatedUser,
        // Program.cs) — most controllers state no [Authorize] and rely on it. A stray [AllowAnonymous] would
        // silently punch through that fallback for one endpoint. No api endpoint is anonymous today: the share
        // read surface authenticates via the ShareLink scheme (an [Authorize], not an opt-out). A future
        // endpoint that genuinely must be anonymous — a webhook, an OAuth form_post callback — adds itself to a
        // documented exception here, the same deliberate-decision pattern the scheme pin above already carries.
        var offenders = new List<string>();
        foreach (var controller in ApiControllers())
        {
            if (controller.GetCustomAttribute<AllowAnonymousAttribute>() is not null)
                offenders.Add(controller.Name);

            foreach (var action in controller.GetMethods(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                if (action.GetCustomAttribute<AllowAnonymousAttribute>() is not null)
                    offenders.Add($"{controller.Name}.{action.Name}");
        }

        Assert.True(offenders.Count == 0,
            "Api endpoints opting out of the authenticated-by-default fallback via [AllowAnonymous]: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void ApiKeyController_UsesTheInteractiveUserPolicy()
    {
        var attr = typeof(ApiKeyController).GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(attr);
        Assert.Equal(InteractiveUserPolicy.Name, attr!.Policy);
    }
}
