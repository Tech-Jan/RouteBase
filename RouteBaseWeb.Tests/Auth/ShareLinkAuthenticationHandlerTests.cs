using System.Security.Claims;
using System.Text.Encodings.Web;
using RouteBaseWeb.Auth;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Sharing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace RouteBaseWeb.Tests.Auth;

/// <summary>
/// <see cref="ShareLinkAuthenticationHandler"/>: route-only token extraction, fail-closed identity, and a
/// challenge that is indistinguishable from a wrong URL.
/// </summary>
public class ShareLinkAuthenticationHandlerTests
{
    private const string Owner = "owner-user";

    /// <summary>Records what it was asked to resolve, so tests can assert the DB was never consulted.</summary>
    private sealed class StubAccess(ShareGrant? grant = null) : IShareLinkAccess
    {
        public int ResolveCalls { get; private set; }

        public Task<ShareGrant?> ResolveAsync(string token, CancellationToken ct)
        {
            ResolveCalls++;
            return Task.FromResult(grant);
        }

        public Task<ShareGrant?> CurrentAsync(CancellationToken ct) => Task.FromResult(grant);

        public Task<T?> WidenAsync<T>(ShareKind kind, int id, Func<Task<T?>> read, CancellationToken ct)
            where T : class
            => throw new NotSupportedException();
    }

    private static async Task<(ShareLinkAuthenticationHandler Handler, HttpContext Context)> BuildAsync(
        IShareLinkAccess access, string? routeToken)
    {
        var handler = new ShareLinkAuthenticationHandler(
            new OptionsMonitorStub(),
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            access);

        var context = new DefaultHttpContext();
        if (routeToken is not null)
            context.Request.RouteValues[ShareLinkDefaults.RouteTokenKey] = routeToken;

        await handler.InitializeAsync(
            new AuthenticationScheme(
                ShareLinkDefaults.AuthenticationScheme, null, typeof(ShareLinkAuthenticationHandler)),
            context);

        return (handler, context);
    }

    private sealed class OptionsMonitorStub : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        public AuthenticationSchemeOptions CurrentValue { get; } = new();
        public AuthenticationSchemeOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }

    private static ShareGrant LiveGrant(int tokenId = 7, int resourceId = 42) =>
        new(tokenId, ShareKind.Activity, resourceId, Owner, DateTime.UtcNow.AddDays(1));

    // ---- route-only extraction ----

    [Fact]
    public async Task NoRouteToken_IsNoResult_AndNeverTouchesTheStore()
    {
        // The no-ambient-authority property: a request that did not address a /s/{token} URL carries no
        // grant, and costs no lookup.
        var access = new StubAccess(LiveGrant());
        var (handler, _) = await BuildAsync(access, routeToken: null);

        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        // NoResult, not Fail: Fail surfaces a reason some handlers echo back. NoResult + a 404 challenge
        // keeps every rejection uniformly opaque.
        Assert.True(result.None);
        Assert.Equal(0, access.ResolveCalls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("too-short")]
    [InlineData("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!")]   // right length, wrong alphabet
    public async Task MalformedRouteToken_IsNoResult_AndNeverTouchesTheStore(string token)
    {
        // Property: unbounded/garbage input is rejected on shape before it costs a hash or a query.
        var access = new StubAccess(LiveGrant());
        var (handler, _) = await BuildAsync(access, token);

        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(0, access.ResolveCalls);
    }

    [Fact]
    public async Task OverlongRouteToken_IsRejectedOnShape()
    {
        var access = new StubAccess(LiveGrant());
        var (handler, _) = await BuildAsync(access, new string('a', 100_000));

        Assert.False((await handler.AuthenticateAsync()).Succeeded);
        Assert.Equal(0, access.ResolveCalls);
    }

    [Fact]
    public async Task DeadToken_IsNoResult()
    {
        // Unknown, expired and revoked all arrive here identically: the access seam already collapsed them
        // to null, so the handler cannot accidentally distinguish them in a response.
        var access = new StubAccess(grant: null);
        var (handler, _) = await BuildAsync(access, ShareTokens.New());

        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        // NoResult, not Fail: a Fail("dead token") would leak that the token was real (and echo a reason).
        // A dead token must be indistinguishable from a wrong URL — same NoResult + 404 challenge as no token.
        Assert.True(result.None);
        Assert.Equal(1, access.ResolveCalls);
    }

    // ---- the minted principal ----

    [Fact]
    public async Task LiveToken_MintsTheConstantAnonymousViewer_NotTheOwner()
    {
        // THE fail-closed property. If this ever returns the owner's id, every Owned*/Visible* path in the
        // app silently starts answering as the owner for anyone holding a link.
        var (handler, _) = await BuildAsync(new StubAccess(LiveGrant()), ShareTokens.New());

        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        var user = result.Principal!;
        Assert.Equal(ShareLinkDefaults.ViewerUserId, user.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal(ShareLinkDefaults.ViewerUserId, user.FindFirstValue("sub"));
        Assert.NotEqual(Owner, user.FindFirstValue(ClaimTypes.NameIdentifier), StringComparer.Ordinal);
    }

    [Fact]
    public async Task LiveToken_CarriesTheTokenIdClaim_SoTheGrantIsResolvable()
    {
        var (handler, _) = await BuildAsync(new StubAccess(LiveGrant(tokenId: 7)), ShareTokens.New());

        var result = await handler.AuthenticateAsync();

        Assert.Equal(7, ShareLinkPrincipal.TokenId(result.Principal!));
    }

    [Fact]
    public async Task LiveToken_ProducesAPrincipalTheDefaultDenyPoliciesRecognise()
    {
        var (handler, _) = await BuildAsync(new StubAccess(LiveGrant()), ShareTokens.New());

        var result = await handler.AuthenticateAsync();

        Assert.True(ShareLinkPrincipal.IsShare(result.Principal));
    }

    [Fact]
    public async Task TwoDifferentTokens_ShareOneCacheIdentity()
    {
        // Pins the anti-bloat property: viewer id is a CONSTANT, so N live tokens do not create N cache
        // namespaces in the viewer-keyed caches (ActivityGpsService, SegmentEfforts). Regressing to
        // share:{tokenId} fails here.
        var (h1, _) = await BuildAsync(new StubAccess(LiveGrant(tokenId: 1)), ShareTokens.New());
        var (h2, _) = await BuildAsync(new StubAccess(LiveGrant(tokenId: 2)), ShareTokens.New());

        var a = (await h1.AuthenticateAsync()).Principal!.FindFirstValue(ClaimTypes.NameIdentifier);
        var b = (await h2.AuthenticateAsync()).Principal!.FindFirstValue(ClaimTypes.NameIdentifier);

        Assert.Equal(a, b);
    }

    [Fact]
    public void ViewerId_CannotCollideWithARealUserId()
    {
        // ':' is not present in an OIDC sub or in the local-mode user id, so the synthetic viewer can never
        // be mistaken for — or collide in a cache key with — a real account.
        Assert.Contains(':', ShareLinkDefaults.ViewerUserId);
        Assert.NotEqual(RouteBaseWeb.Configuration.LocalModeOptions.UserId, ShareLinkDefaults.ViewerUserId, StringComparer.Ordinal);
    }

    // ---- the response to a dead token ----

    [Fact]
    public async Task Challenge_Is404_WithNoRedirectAndNoAuthenticateHeader()
    {
        // Property: a real-but-dead token is indistinguishable from a wrong URL. A 401/302 would confirm
        // the token existed, and would hand a crawler a redirect to follow into the OIDC flow.
        var (handler, context) = await BuildAsync(new StubAccess(grant: null), ShareTokens.New());

        await handler.ChallengeAsync(new AuthenticationProperties());

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Location"));
        Assert.False(context.Response.Headers.ContainsKey("WWW-Authenticate"));
    }

    [Fact]
    public async Task Forbid_Is404_Too()
    {
        var (handler, context) = await BuildAsync(new StubAccess(LiveGrant()), ShareTokens.New());

        await handler.ForbidAsync(new AuthenticationProperties());

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Location"));
    }
}
