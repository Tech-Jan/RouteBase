using System.Net;
using RouteBaseWeb.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace RouteBaseWeb.Tests.Hosting;

/// <summary>
/// The local-mode mirror of <see cref="ServerModeAuthPipelineTests"/>: with the switch file present and
/// enabled, the app serves every request as the always-authenticated local user — and that user (being
/// the machine's owner) passes the admin gate. /Admin/Errors returning 200 here pins C6's approved
/// behavior: local-mode admin comes from being AUTHENTICATED AS the local user, and the page must keep
/// working after the "Enabled alone means admin" short-circuit is gone.
/// </summary>
[Collection(AppPipelineCollection.Name)]
public class LocalModeAuthPipelineTests
{
    [Fact]
    public async Task LocalMode_ServesAnonymousRequest_AsLocalUser_AndLocalUserIsAdmin()
    {
        try
        {
            LocalSettingsFile.Write("""{ "LocalMode": { "Enabled": true } }""");
            await using var factory = new LocalModeAppFactory();
            using var client = factory.CreateClient(
                new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var schemes = factory.Services.GetRequiredService<IAuthenticationSchemeProvider>();
            Assert.NotNull(await schemes.GetSchemeAsync(LocalModeOptions.AuthenticationScheme));
            Assert.True(factory.Services.GetRequiredService<LocalModeOptions>().Enabled);

            // No credentials anywhere, yet the page renders: the local scheme signed the request in.
            var home = await client.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, home.StatusCode);

            // C6: the local user is admin because it IS the authenticated local user (page 404s for
            // non-admins, so 200 = the gate passed).
            var admin = await client.GetAsync("/Admin/Errors");
            Assert.Equal(HttpStatusCode.OK, admin.StatusCode);

            // A6, end-to-end in the mode that matters (no cookie ⇒ SameSite moot): a browser-labelled
            // cross-site multipart POST bounces off the guard before any controller code runs.
            using var crossSite = new HttpRequestMessage(HttpMethod.Post, "/api/photos/batch/begin")
            {
                Content = new MultipartFormDataContent { { new StringContent("0"), "previewMaxEdge" } },
            };
            crossSite.Headers.Add("Sec-Fetch-Site", "cross-site");
            var rejected = await client.SendAsync(crossSite);
            Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);

            // API-03: /api/apikeys must WORK in local mode — the old scheme-naming [Authorize] threw
            // ("No authentication handler is registered for the scheme 'Cookies'") on every call, so
            // generating a mobile key against a localhost instance was impossible.
            var apiKeys = await client.GetAsync("/api/apikeys/status");
            Assert.Equal(HttpStatusCode.OK, apiKeys.StatusCode);
        }
        finally
        {
            LocalSettingsFile.Delete();
        }
    }
}
