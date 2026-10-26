using System.Globalization;
using System.Security.Claims;
using RouteBaseWeb.Auth;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace RouteBaseWeb.Tests.Auth;

/// <summary>
/// The share slice's default-deny properties. A failure in this file is a vulnerability, not a bug — it
/// means a share token is accepted somewhere it was never meant to reach.
///
/// The policies are rebuilt here exactly as Program.cs's AddAuthorization block builds them. That is a
/// deliberate duplication: these tests pin the SHAPE the block must have, and PolicyShapeMirrorsProgramCs
/// keeps the copy honest by asserting against the real Program.cs source text.
/// </summary>
public class ShareLinkDefaultDenyTests
{
    // Mirrors Program.cs RealUserOnly().
    private static AuthorizationPolicy RealUserOnly() =>
        new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => !ShareLinkPrincipal.IsShare(ctx.User))
            .Build();

    // Mirrors Program.cs's ShareLinkRead policy.
    private static AuthorizationPolicy ShareRead() =>
        new AuthorizationPolicyBuilder()
            .AddAuthenticationSchemes(ShareLinkDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => ShareLinkPrincipal.IsShare(ctx.User))
            .Build();

    // Mirrors Program.cs's ShareLinkMint policy: authenticated, not a share principal, no ApiKey identity.
    private static AuthorizationPolicy Mint() =>
        new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx =>
                !ShareLinkPrincipal.IsShare(ctx.User) &&
                ctx.User.Identities.All(i =>
                    i.AuthenticationType != ApiKeyDefaults.AuthenticationScheme))
            .Build();

    private static ClaimsPrincipal ApiKeyPrincipal(string userId = "key-user") =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId)],
            ApiKeyDefaults.AuthenticationScheme));

    private static ClaimsPrincipal SharePrincipal(int tokenId = 1) =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, ShareLinkDefaults.ViewerUserId),
            new Claim(ShareLinkDefaults.TokenIdClaim, tokenId.ToString(CultureInfo.InvariantCulture)),
        ], ShareLinkDefaults.AuthenticationScheme));

    private static ClaimsPrincipal CookiePrincipal(string userId = "real-user") =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId)],
            CookieAuthenticationDefaults.AuthenticationScheme));

    /// <summary>
    /// Evaluates a policy without a host. Both requirements used here (DenyAnonymous, Assertion) are
    /// self-handling — they implement IAuthorizationHandler — which is precisely what the framework's
    /// internal PassThroughAuthorizationHandler relies on.
    /// </summary>
    private static async Task<bool> SucceedsAsync(AuthorizationPolicy policy, ClaimsPrincipal user)
    {
        var context = new AuthorizationHandlerContext(policy.Requirements, user, resource: null);
        foreach (var handler in policy.Requirements.OfType<IAuthorizationHandler>())
            await handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    // ---- the identity predicate ----

    [Fact]
    public void IsShare_TrueForAShareIdentity_FalseForARealOne()
    {
        Assert.True(ShareLinkPrincipal.IsShare(SharePrincipal()));
        Assert.False(ShareLinkPrincipal.IsShare(CookiePrincipal()));
        Assert.False(ShareLinkPrincipal.IsShare(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.False(ShareLinkPrincipal.IsShare(null));
    }

    [Fact]
    public void IsShare_DetectsAShareIdentityMergedBehindARealOne()
    {
        // ASP.NET can merge identities from several schemes onto one principal. Reading only .Identity
        // would miss the share identity here and let the merged principal past the fallback policy.
        var merged = new ClaimsPrincipal(CookiePrincipal().Identities.First());
        merged.AddIdentity((ClaimsIdentity)SharePrincipal().Identity!);

        Assert.True(ShareLinkPrincipal.IsShare(merged));
    }

    [Fact]
    public void IsShare_DetectsATokenClaimCarriedByAnyIdentityType()
    {
        // Defence in depth: an identity bearing the token-id claim is a share identity whatever its
        // AuthenticationType claims to be.
        var spoofed = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ShareLinkDefaults.TokenIdClaim, "7")],
            CookieAuthenticationDefaults.AuthenticationScheme));

        Assert.True(ShareLinkPrincipal.IsShare(spoofed));
    }

    // ---- default-deny ----

    [Fact]
    public async Task FallbackAndDefaultPolicies_RejectAShareParticipant()
    {
        // The property: a share token reaching ANY ordinary endpoint is refused by authorization, whether
        // that endpoint has no metadata (fallback) or a bare [Authorize] (default).
        Assert.False(await SucceedsAsync(RealUserOnly(), SharePrincipal()));
    }

    [Fact]
    public async Task FallbackAndDefaultPolicies_StillAdmitARealUser()
    {
        // The hardening must not cost normal users anything.
        Assert.True(await SucceedsAsync(RealUserOnly(), CookiePrincipal()));
    }

    [Fact]
    public void RealUserOnlyPolicy_ListsNoAuthenticationSchemes()
    {
        // THE regression guard for the login-breaking shape. If someone "hardens" the fallback policy by
        // listing schemes, AuthorizationMiddlewareResultHandler challenges every one of them on failure —
        // an anonymous navigation would emit a Cookie 302, an OIDC 302 and an ApiKey 401 on one response.
        Assert.Empty(RealUserOnly().AuthenticationSchemes);
    }

    // ---- the one policy that admits a share principal ----

    [Fact]
    public async Task ShareReadPolicy_AdmitsAShareParticipant()
        => Assert.True(await SucceedsAsync(ShareRead(), SharePrincipal()));

    [Fact]
    public async Task ShareReadPolicy_RejectsANormalCookieUser()
    {
        // A logged-in user cannot ride a share endpoint on their session: the policy names only the
        // ShareLink scheme, so PolicyEvaluator re-authenticates and the cookie principal is not a share one.
        Assert.False(await SucceedsAsync(ShareRead(), CookiePrincipal()));
    }

    [Fact]
    public async Task ShareReadPolicy_RejectsAnonymous()
        => Assert.False(await SucceedsAsync(ShareRead(), new ClaimsPrincipal(new ClaimsIdentity())));

    [Fact]
    public void ShareReadPolicy_NamesOnlyTheShareLinkScheme()
    {
        // Naming the scheme is what makes PolicyEvaluator REPLACE HttpContext.User for the request, so a
        // share endpoint sees the anonymous view even when the visitor happens to be the logged-in owner.
        Assert.Equal([ShareLinkDefaults.AuthenticationScheme], ShareRead().AuthenticationSchemes);
    }

    // ---- the mint policy: interactive human only ----

    [Fact]
    public async Task MintPolicy_RejectsAShareParticipant()
        // Share invariant: a link holder must never mint further links from the link they arrived on.
        => Assert.False(await SucceedsAsync(Mint(), SharePrincipal()));

    [Fact]
    public async Task MintPolicy_RejectsAnApiKeyPrincipal()
        // An uploader credential (GpsRecorder) is not an interactive human session.
        => Assert.False(await SucceedsAsync(Mint(), ApiKeyPrincipal()));

    [Fact]
    public async Task MintPolicy_RejectsAnApiKeyIdentityMergedBehindACookieOne()
    {
        // Identities.All: a merged principal carrying ANY ApiKey identity is refused, not just one whose
        // primary identity is the key.
        var merged = new ClaimsPrincipal(CookiePrincipal().Identities.First());
        merged.AddIdentity((ClaimsIdentity)ApiKeyPrincipal().Identity!);

        Assert.False(await SucceedsAsync(Mint(), merged));
    }

    [Fact]
    public async Task MintPolicy_AdmitsARealUser()
        => Assert.True(await SucceedsAsync(Mint(), CookiePrincipal()));

    [Fact]
    public async Task MintPolicy_RejectsAnonymous()
        => Assert.False(await SucceedsAsync(Mint(), new ClaimsPrincipal(new ClaimsIdentity())));

    // ---- the structural layer, asserted against the real Program.cs ----

    [Fact]
    public void ForwardDefaultSelector_NeverReturnsTheShareLinkScheme()
    {
        // Layer 1, and the load-bearing one: if ShareLink were reachable from the "Auto" selector,
        // HttpContext.User would be a share principal on every endpoint and the fallback policy would
        // accept it. Asserted against source text because the selector is a closure inside a top-level
        // program and cannot be resolved from the test host.
        var program = ReadProgramSource();
        // Comments stripped: the selector carries a long comment explaining why ShareLink is absent, and
        // asserting over raw text would match that prose instead of the code.
        // The selector lambda closes with "};" — anchoring on "});" would run past it into the scheme
        // registrations below, which legitimately do name ShareLinkAuthenticationHandler.
        var selector = StripComments(Between(program, "options.ForwardDefaultSelector", "};"));

        // Anti-vacuity: prove we extracted the real selector before asserting on what it does NOT contain.
        // An empty or mis-anchored extraction would satisfy DoesNotContain forever.
        Assert.Contains("ApiKeyDefaults.AuthenticationScheme", selector, StringComparison.Ordinal);
        Assert.Contains("CookieAuthenticationDefaults.AuthenticationScheme", selector, StringComparison.Ordinal);

        Assert.DoesNotContain(ShareLinkDefaults.AuthenticationScheme, selector, StringComparison.Ordinal);
        Assert.DoesNotContain("ShareLinkDefaults", selector, StringComparison.Ordinal);
    }

    [Fact]
    public void StaticAssetEndpoints_AreAnonymous()
    {
        // MapStaticAssets registers ENDPOINTS for fingerprinted asset URLs, and an endpoint carrying no
        // authorization metadata inherits the global FallbackPolicy — so without AllowAnonymous every
        // script and stylesheet 302s to the identity provider. That is invisible on authenticated pages
        // and breaks the anonymous share view completely (scripts blocked by CORS on the redirect).
        //
        // It grants nothing: UseStaticFiles runs before UseAuthentication, so the same bytes are already
        // public at their un-fingerprinted URLs.
        var program = StripComments(ReadProgramSource());

        Assert.Contains("app.MapStaticAssets().AllowAnonymous();", program, StringComparison.Ordinal);
    }

    [Fact]
    public void PolicyShapeMirrorsProgramCs()
    {
        // Keeps the mirrored policies above honest: if Program.cs stops rejecting share principals on the
        // fallback/default policies, or starts listing schemes on them, this fails even though the
        // hand-built copies in this file would still pass.
        var program = ReadProgramSource();
        var block = Between(program, "builder.Services.AddAuthorization(", "\n});");

        Assert.Contains("RequireAssertion(ctx => !ShareLinkPrincipal.IsShare(ctx.User))", block, StringComparison.Ordinal);
        Assert.Contains("options.FallbackPolicy = RealUserOnly();", block, StringComparison.Ordinal);
        Assert.Contains("options.DefaultPolicy = RealUserOnly();", block, StringComparison.Ordinal);
        // The symbol, not its value: the source references the constant, never the literal "ShareLinkRead".
        Assert.Contains("ShareLinkDefaults.ReadPolicy", block, StringComparison.Ordinal);

        // RealUserOnly must not list schemes (see RealUserOnlyPolicy_ListsNoAuthenticationSchemes).
        var realUserOnly = Between(block, "static AuthorizationPolicy RealUserOnly()", ".Build();");
        Assert.DoesNotContain("AddAuthenticationSchemes", realUserOnly, StringComparison.Ordinal);

        // Keeps the Mint() mirror above honest, the same way. The body now lives in InteractiveHumanOnly()
        // — one builder registered under BOTH the mint policy and the api-key-management policy (API-03),
        // so the shape is asserted once and both registrations are pinned to it. Deliberately shapeless on
        // schemes: naming the cookie scheme is the documented WRONG shape (breaks in local mode, see
        // ShareLinkDefaults / InteractiveUserPolicy).
        var interactive = Between(block, "static AuthorizationPolicy InteractiveHumanOnly()", ".Build();");
        Assert.Contains("RequireAuthenticatedUser", interactive, StringComparison.Ordinal);
        Assert.Contains("!ShareLinkPrincipal.IsShare(ctx.User)", interactive, StringComparison.Ordinal);
        Assert.Contains("ApiKeyDefaults.AuthenticationScheme", interactive, StringComparison.Ordinal);
        Assert.DoesNotContain("AddAuthenticationSchemes", interactive, StringComparison.Ordinal);
        Assert.Contains("options.AddPolicy(ShareLinkDefaults.MintPolicy, InteractiveHumanOnly());",
            block, StringComparison.Ordinal);
        Assert.Contains("options.AddPolicy(InteractiveUserPolicy.Name, InteractiveHumanOnly());",
            block, StringComparison.Ordinal);
    }

    private static string ReadProgramSource()
    {
        // Walk up from the test bin/ to the repo root, then into the web project.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RouteBase.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "RouteBaseWeb", "Program.cs");
        Assert.True(File.Exists(path), $"Program.cs not found at {path}");
        return File.ReadAllText(path);
    }

    /// <summary>Drops <c>//</c> line comments so source assertions match code, not prose about the code.</summary>
    private static string StripComments(string source) =>
        string.Join('\n', source.Split('\n').Select(line =>
        {
            var idx = line.IndexOf("//", StringComparison.Ordinal);
            return idx >= 0 ? line[..idx] : line;
        }));

    private static string Between(string haystack, string start, string end)
    {
        var from = haystack.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"Anchor not found in Program.cs: {start}");
        var to = haystack.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"Closing anchor not found after {start}: {end}");
        return haystack[from..to];
    }
}
