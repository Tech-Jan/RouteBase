using System.Net;
using RouteBaseWeb.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace RouteBaseWeb.Tests.Hosting;

/// <summary>
/// Pins the highest-stakes line in Program.cs: the <c>if (localMode.Enabled)</c> auth registration
/// gate. In server mode an unauthenticated request must be CHALLENGED (redirected to the identity
/// provider), never quietly served as the always-authenticated local user — and the local scheme must
/// not exist at all. A regression here (gate inverted, local scheme registered unconditionally, file
/// probe widened) turns the public deployment into a no-login app.
/// </summary>
[Collection(AppPipelineCollection.Name)]
public class ServerModeAuthPipelineTests
{
    private readonly ServerModeAppFactory _factory;

    public ServerModeAuthPipelineTests(ServerModeAppFactory factory) => _factory = factory;

    [Fact]
    public async Task AnonymousPageRequest_IsChallengedToIdentityProvider_NotServed()
    {
        using var client = _factory.CreateNoRedirectClient();

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.StartsWith(ServerModeAppFactory.AuthorizationEndpoint,
            response.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnonymousApiRequest_IsNotServed()
    {
        using var client = _factory.CreateNoRedirectClient();

        var response = await client.GetAsync("/api/photos");

        // No identity, no data — and specifically a genuine auth challenge, never a 500 dressed up as a
        // deny (NotEqual(OK) alone would let a server error pass). A browser (no X-Api-Key) is redirected
        // to the identity provider; an X-Api-Key client gets 401 (pinned elsewhere).
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.True((int)response.StatusCode < 500, $"expected an auth challenge, got {(int)response.StatusCode}");
        Assert.True(
            response.StatusCode is HttpStatusCode.Found or HttpStatusCode.Unauthorized,
            $"expected 302 or 401, got {(int)response.StatusCode}");
    }

    [Fact]
    public async Task LocalAuthScheme_IsNotRegistered_InServerMode()
    {
        var schemes = _factory.Services.GetRequiredService<IAuthenticationSchemeProvider>();

        Assert.Null(await schemes.GetSchemeAsync(LocalModeOptions.AuthenticationScheme));
        Assert.NotNull(await schemes.GetSchemeAsync(OpenIdConnectDefaults.AuthenticationScheme));
        Assert.False(_factory.Services.GetRequiredService<LocalModeOptions>().Enabled);
    }

    [Fact]
    public async Task FailedOidcCallback_RedirectsToLoginPage_NotServerError()
    {
        // A callback whose state can't be unprotected lands in the same RemoteFailure funnel as a
        // token-endpoint timeout or a stale back-button re-POST (Error_Log 2026-07-22: TaskCanceled →
        // AuthenticationFailureException → 500). Environmental failures must bounce to the manual
        // login page — which only challenges on user click, so a flaky IdP can't cause a redirect loop.
        using var client = _factory.CreateNoRedirectClient();
        using var callback = new HttpRequestMessage(HttpMethod.Post, "/signin-oidc")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["code"] = "fake-code",
                ["state"] = "not-a-protected-state",
            }),
        };

        var response = await client.SendAsync(callback);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.StartsWith("/Auth/Login", response.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Healthz_ServesAnonymously()
    {
        // Positive control: proves the challenges above are auth decisions, not a broken pipeline.
        using var client = _factory.CreateNoRedirectClient();

        var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ErrorPage_ServesAnonymously_NotAChallenge()
    {
        // PI-03: the exception handler re-executes failures to /Error, and the anonymous share surface
        // (/s/{token}) must land on the error page — not get 302'd into the identity-provider login.
        using var client = _factory.CreateNoRedirectClient();

        var response = await client.GetAsync("/Error");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
