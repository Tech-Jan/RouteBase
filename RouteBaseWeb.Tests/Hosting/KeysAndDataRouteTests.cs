using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace RouteBaseWeb.Tests.Hosting;

/// <summary>
/// The page was /StravaSync until 2026-10-26. The old path stays a live alias (Program.cs AddPageRoute) because a
/// Strava OAuth round-trip minted before the rename returns to it with ?code&amp;state — a 404 there would strand
/// the connect. Local mode, so both requests are authenticated and actually render the page.
/// </summary>
[Collection(AppPipelineCollection.Name)]
public class KeysAndDataRouteTests
{
    [Fact]
    public async Task NewPath_And_OldStravaSyncAlias_BothRenderThePage()
    {
        try
        {
            LocalSettingsFile.Write("""{ "LocalMode": { "Enabled": true } }""");
            await using var factory = new LocalModeAppFactory();
            using var client = factory.CreateClient(
                new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var current = await client.GetAsync("/KeysAndData");
            var alias = await client.GetAsync("/StravaSync");
            var control = await client.GetAsync("/NoSuchPageHere");

            Assert.Equal(HttpStatusCode.OK, current.StatusCode);
            Assert.Equal(HttpStatusCode.OK, alias.StatusCode);
            Assert.Contains("API Keys &amp; Database", await alias.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.NotFound, control.StatusCode);
        }
        finally
        {
            LocalSettingsFile.Delete();
        }
    }
}
