using System.Reflection;
using RouteBaseWeb.Api;
using RouteBaseWeb.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// A6 / D1: the cross-site guard. Two layers:
/// <list type="number">
/// <item><b>Classification</b> (<see cref="RejectCrossSiteBrowserRequestsAttribute.IsCrossSite"/>):
/// Sec-Fetch-Site rules when present (only "cross-site" rejects); otherwise Origin must match the request
/// authority; clients sending neither header (curl, the Android app) pass.</item>
/// <item><b>Global coverage</b> (<see cref="CrossSiteApiGuardFilter"/>): every state-changing verb
/// (POST/PUT/PATCH/DELETE) on the <c>RouteBaseWeb.Api</c> surface is guarded by default — the photo/import
/// batch endpoints no longer carry the per-action attribute (removed in D1) yet stay covered, and endpoints
/// that were never attributed (required-JSON-body POSTs, DELETEs) are now covered too. Safe verbs, non-api
/// controllers, and <see cref="AllowCrossSiteBrowserRequestsAttribute"/> opt-outs are skipped.</item>
/// </list>
/// </summary>
public class CrossSiteRequestGuardTests
{
    private static HttpRequest Request(
        string? secFetchSite = null, string? origin = null,
        string host = "app.example", int? port = null, string scheme = "https")
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.Host = port is { } p ? new HostString(host, p) : new HostString(host);
        if (secFetchSite is not null) context.Request.Headers["Sec-Fetch-Site"] = secFetchSite;
        if (origin is not null) context.Request.Headers.Origin = origin;
        return context.Request;
    }

    // ── Sec-Fetch-Site is the authority when present ────────────────────────────

    [Theory]
    [InlineData("cross-site", true)]
    [InlineData("Cross-Site", true)] // header values are case-insensitive
    [InlineData("same-origin", false)]
    [InlineData("same-site", false)]
    [InlineData("none", false)] // address bar / bookmark
    public void SecFetchSite_Classifies(string value, bool expectCrossSite) =>
        Assert.Equal(expectCrossSite,
            RejectCrossSiteBrowserRequestsAttribute.IsCrossSite(Request(secFetchSite: value)));

    [Fact]
    public void SecFetchSite_Wins_OverMatchingOrigin() =>
        Assert.True(RejectCrossSiteBrowserRequestsAttribute.IsCrossSite(
            Request(secFetchSite: "cross-site", origin: "https://app.example")));

    // ── Origin fallback (no Sec-Fetch-Site) ─────────────────────────────────────

    [Fact]
    public void NoHeadersAtAll_IsAllowed() // non-browser clients: curl, the Android app
        => Assert.False(RejectCrossSiteBrowserRequestsAttribute.IsCrossSite(Request()));

    [Theory]
    [InlineData("https://app.example", "app.example", null, "https", false)] // exact match (default port)
    [InlineData("http://localhost:5054", "localhost", 5054, "http", false)]  // localhost mode
    [InlineData("https://evil.example", "app.example", null, "https", true)] // foreign host
    [InlineData("https://app.example.evil.example", "app.example", null, "https", true)]
    [InlineData("http://app.example", "app.example", null, "https", true)]   // scheme downgrade
    [InlineData("https://app.example:8443", "app.example", null, "https", true)] // port mismatch
    [InlineData("null", "app.example", null, "https", true)]  // opaque origin (sandbox, file://)
    [InlineData("not a url", "app.example", null, "https", true)]
    public void OriginFallback_ComparesAgainstRequestAuthority(
        string origin, string host, int? port, string scheme, bool expectCrossSite) =>
        Assert.Equal(expectCrossSite, RejectCrossSiteBrowserRequestsAttribute.IsCrossSite(
            Request(origin: origin, host: host, port: port, scheme: scheme)));

    // ── Filter behavior ─────────────────────────────────────────────────────────

    private static AuthorizationFilterContext FilterContext(HttpRequest request) =>
        new(new ActionContext(request.HttpContext, new RouteData(), new ActionDescriptor()), []);

    [Fact]
    public void CrossSiteRequest_Gets403_WithNamedErrorBody()
    {
        var context = FilterContext(Request(secFetchSite: "cross-site"));

        new RejectCrossSiteBrowserRequestsAttribute().OnAuthorization(context);

        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        // The wire body is {message, code} like every other api/ error (D31/D32) — this was the
        // last pre-D32 {error} emitter.
        var body = Assert.IsType<CrossSiteRejectedBody>(result.Value);
        Assert.Equal(ApiErrorCodes.Forbidden, body.Code);
        Assert.False(string.IsNullOrWhiteSpace(body.Message));
    }

    [Fact]
    public void SameOriginRequest_PassesThrough()
    {
        var context = FilterContext(Request(secFetchSite: "same-origin"));

        new RejectCrossSiteBrowserRequestsAttribute().OnAuthorization(context);

        Assert.Null(context.Result);
    }

    // ── Global guard covers the api/ surface (D1) ───────────────────────────────
    //
    // These batch actions used to CARRY [RejectCrossSiteBrowserRequests] (the old per-action pin). D1 moved
    // the guarantee to the global CrossSiteApiGuardFilter and removed the attributes from both controllers;
    // the guard.Applies predicate is the new pin that the coverage did not regress.

    private static ControllerActionDescriptor Descriptor(Type controllerType, string action) =>
        new() { ControllerTypeInfo = controllerType.GetTypeInfo(), MethodInfo = controllerType.GetMethod(action)! };

    [Theory]
    [InlineData(nameof(PhotosApiController.BeginBatch))]
    [InlineData(nameof(PhotosApiController.AppendBatchFiles))]
    [InlineData(nameof(PhotosApiController.CommitBatch))]
    [InlineData(nameof(PhotosApiController.AbortBatch))]
    public void PhotoBatchEndpoints_CoveredByGlobalGuard(string action) =>
        Assert.True(CrossSiteApiGuardFilter.Applies(Descriptor(typeof(PhotosApiController), action), "POST"));

    [Theory]
    [InlineData(nameof(ImportApiController.BeginBatch))]
    [InlineData(nameof(ImportApiController.AppendBatchFiles))]
    [InlineData(nameof(ImportApiController.CommitBatch))]
    [InlineData(nameof(ImportApiController.AbortBatch))]
    public void ImportBatchEndpoints_CoveredByGlobalGuard(string action) =>
        Assert.True(CrossSiteApiGuardFilter.Applies(Descriptor(typeof(ImportApiController), action), "POST"));

    [Fact] // required-JSON-body POST — never attributed before D1 (preflight-guarded), now covered anyway
    public void PreviouslyUnattributedPost_IsNowCovered() =>
        Assert.True(CrossSiteApiGuardFilter.Applies(
            Descriptor(typeof(PhotosApiController), nameof(PhotosApiController.DeleteMany)), "POST"));

    [Fact] // DELETE — never attributed before D1, now covered
    public void DeleteVerb_IsCovered() =>
        Assert.True(CrossSiteApiGuardFilter.Applies(
            Descriptor(typeof(PhotosApiController), nameof(PhotosApiController.Delete)), "DELETE"));

    [Fact] // GET is not a cross-site-forgeable state change
    public void SafeVerb_IsNotCovered() =>
        Assert.False(CrossSiteApiGuardFilter.Applies(
            Descriptor(typeof(PhotosApiController), nameof(PhotosApiController.GetImage)), "GET"));

    [Fact] // scope is the api namespace only — Razor Pages / non-api MVC keep their own antiforgery story
    public void NonApiController_IsNotCovered() =>
        Assert.False(CrossSiteApiGuardFilter.Applies(
            Descriptor(typeof(Fakes.GuardProbeNonApiController), nameof(Fakes.GuardProbeNonApiController.Mutating)),
            "POST"));

    [Fact] // PI-03: a controller in a SUB-namespace of the api surface must not fall through the scope check
    public void SubNamespaceApiController_IsCovered() =>
        Assert.True(CrossSiteApiGuardFilter.Applies(
            Descriptor(typeof(RouteBaseWeb.Api.Internal.GuardProbeSubNamespaceApiController),
                nameof(RouteBaseWeb.Api.Internal.GuardProbeSubNamespaceApiController.Mutating)),
            "POST"));

    [Fact] // the documented escape hatch for a genuinely cross-site endpoint (webhook / OAuth form_post)
    public void OptedOutAction_IsNotCovered() =>
        Assert.False(CrossSiteApiGuardFilter.Applies(
            Descriptor(typeof(GuardProbeApiController), nameof(GuardProbeApiController.OptedOut)), "POST"));

    // ── Global filter behavior: cross-site rejected, everything legitimate passes ─

    private static AuthorizationFilterContext GlobalContext(
        ControllerActionDescriptor descriptor, string method,
        string? secFetchSite = null, string? origin = null, string host = "app.example", string scheme = "https")
    {
        var http = new DefaultHttpContext();
        http.Request.Scheme = scheme;
        http.Request.Host = new HostString(host);
        http.Request.Method = method;
        if (secFetchSite is not null) http.Request.Headers["Sec-Fetch-Site"] = secFetchSite;
        if (origin is not null) http.Request.Headers.Origin = origin;
        return new AuthorizationFilterContext(new ActionContext(http, new RouteData(), descriptor), []);
    }

    [Fact] // the task's named example — recording pause, a route-only POST
    public void GlobalGuard_RejectsCrossSitePost_ToPreviouslyUnguardedEndpoint()
    {
        var context = GlobalContext(
            Descriptor(typeof(RecordingApiController), nameof(RecordingApiController.PauseSession)),
            "POST", secFetchSite: "cross-site");

        new CrossSiteApiGuardFilter().OnAuthorization(context);

        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.IsType<CrossSiteRejectedBody>(result.Value);
    }

    [Fact]
    public void GlobalGuard_RejectsCrossSiteDelete()
    {
        var context = GlobalContext(
            Descriptor(typeof(PhotosApiController), nameof(PhotosApiController.Delete)),
            "DELETE", secFetchSite: "cross-site");

        new CrossSiteApiGuardFilter().OnAuthorization(context);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(context.Result).StatusCode);
    }

    [Theory]
    [InlineData("same-origin", null)]       // app's own page
    [InlineData("none", null)]              // address bar / bookmark
    [InlineData(null, null)]                // non-browser client (Android app, curl): no headers
    [InlineData(null, "https://app.example")] // older browser, matching Origin
    public void GlobalGuard_PassesLegitimateStateChange(string? secFetchSite, string? origin)
    {
        var context = GlobalContext(
            Descriptor(typeof(RecordingApiController), nameof(RecordingApiController.PauseSession)),
            "POST", secFetchSite: secFetchSite, origin: origin);

        new CrossSiteApiGuardFilter().OnAuthorization(context);

        Assert.Null(context.Result);
    }

    [Fact] // a cross-site GET is not a state change — the global guard leaves it alone
    public void GlobalGuard_IgnoresCrossSiteSafeVerb()
    {
        var context = GlobalContext(
            Descriptor(typeof(PhotosApiController), nameof(PhotosApiController.GetImage)),
            "GET", secFetchSite: "cross-site");

        new CrossSiteApiGuardFilter().OnAuthorization(context);

        Assert.Null(context.Result);
    }
}
