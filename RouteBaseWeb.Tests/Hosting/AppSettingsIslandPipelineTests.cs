using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RouteBaseWeb.Configuration;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using Xunit;

namespace RouteBaseWeb.Tests.Hosting;

/// <summary>
/// Gate (c3) — the only leg that pins "EVERY page delivers the settings island", end to end, through the
/// real Program.cs, real routing, real layouts.
///
/// (c1)/(c2) pin the partial and where it is rendered from. Neither can see a page that sets
/// <c>Layout = null</c>, a route that never reaches a layout, or a provider that throws mid-render — all of
/// which produce a page with no settings and no error. Only a request can.
///
/// The MapTiler credential lives in the AppCredentials table, and the test host creates its database fresh
/// under the test bin, so "no key configured" is the host's natural state — asserted below rather than
/// assumed, because if a key ever WERE present these tests would pass for the wrong reason and stop
/// guarding the thing they exist for.
///
/// R1 has its own leg. <c>ICurrentUserService.GetUserId()</c> throws when anonymous, and <c>_Layout</c> is
/// the ERROR page's layout — so a settings provider that lets that throw turns every error into an
/// unrenderable error, including for the anonymous share visitor /Error exists to serve (PI-03).
/// </summary>
[Collection(AppPipelineCollection.Name)]
public class AppSettingsIslandPipelineTests
{
    private readonly ServerModeAppFactory _serverMode;

    public AppSettingsIslandPipelineTests(ServerModeAppFactory serverMode) => _serverMode = serverMode;

    /// <summary>The id of the JSON island every page must carry.</summary>
    private const string IslandMarker = "id=\"appSettings\"";

    /// <summary>A recording session id the sweep creates, so /LiveView has something to resolve.</summary>
    private const string LiveSessionId = "app-settings-island-gate";

    /// <summary>
    /// One authenticated page per layout family and per settings CONSUMER: the dashboard, the activities
    /// grid, the segments list, the settings form itself, and the live map. LiveView is here specifically
    /// because it is a page the old delivery leaves with NO settings at all — a live recording rendering
    /// with someone's smoothing preference ignored is the bug in its most visible form.
    ///
    /// /LiveView 404s unless a session resolves, so this sweep seeds one; a page that needs a row is still
    /// a page that must carry the island.
    /// </summary>
    private static readonly string[] AuthenticatedPages =
    [
        "/",
        "/Activities",
        "/Segments",
        "/ProfileSettings",
        $"/LiveView?sessionId={LiveSessionId}",
    ];

    /// <summary>
    /// Minimal in-progress session owned by the local user, so /LiveView renders. The Activity shell comes
    /// with it — <c>RecordingSession.track_id</c> is a NOT NULL FK to <c>Activity(track_ID)</c>, and foreign
    /// keys really are enforced by this SQLite build.
    /// </summary>
    private static async Task SeedLiveSessionAsync(LocalModeAppFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RouteBaseDbContext>();

        if (await db.RecordingSessions.FindAsync(LiveSessionId) is not null) return;

        var trackId = new TrackId(990_001);
        db.Activities.Add(new Activity
        {
            TrackId = trackId,
            UserId = LocalModeOptions.UserId,
            ActivityType = "Ride",
            Source = "auto-recording",
        });
        db.RecordingSessions.Add(new RecordingSession
        {
            SessionId = LiveSessionId,
            TrackId = trackId,
            UserId = LocalModeOptions.UserId,
            Status = RecordingSessionStatus.Recording,
            ActivityType = "Ride",
            StartedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task EveryPage_CarriesTheSettingsIsland_WithNoMapTilerCredential()
    {
        try
        {
            // Local mode serves every request as the fixed local user — the only way to GET an authorized
            // page without standing up an identity provider.
            LocalSettingsFile.Write("""{ "LocalMode": { "Enabled": true } }""");
            await using var factory = new LocalModeAppFactory();
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            using (var scope = factory.Services.CreateScope())
            {
                var mapTiler = scope.ServiceProvider.GetRequiredService<IMapTilerService>();
                Assert.False(
                    await mapTiler.HasApiKeyAsync(),
                    "A MapTiler key IS configured in the test host, so this gate would pass under the very "
                    + "condition it exists to reject. Keyless is the whole point.");
            }

            await SeedLiveSessionAsync(factory);

            var missing = new List<string>();
            var unreachable = new List<string>();

            foreach (var path in AuthenticatedPages)
            {
                var response = await client.GetAsync(path);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    // Kept distinct from "no island": a route that does not render is a broken test
                    // fixture, not a delivery defect, and collapsing the two would let this gate report
                    // the wrong thing.
                    unreachable.Add($"{path} → {(int)response.StatusCode} {response.StatusCode}");
                    continue;
                }

                var html = await response.Content.ReadAsStringAsync();
                if (!html.Contains(IslandMarker, StringComparison.Ordinal)) missing.Add(path);
            }

            Assert.True(
                unreachable.Count == 0,
                "These pages did not render at all, so this gate could not observe them. Fix the route list "
                + "or the fixture — a page silently dropped from the sweep is a page nothing pins:\n  "
                + string.Join("\n  ", unreachable));

            Assert.True(
                missing.Count == 0,
                "These pages render with no settings island, so every preference they read silently falls "
                + "back to a default — a user's chart smoothing ignored because an unrelated MAP CREDENTIAL "
                + "is missing. Settings must be delivered by the layout, not decided per page:\n  "
                + string.Join("\n  ", missing));
        }
        finally
        {
            LocalSettingsFile.Delete();
        }
    }

    /// <summary>
    /// R1. Server mode, no credentials at all: the anonymous error page must RENDER. If the settings
    /// provider calls GetUserId() (which throws when anonymous) or lets a database blip escape, this page
    /// becomes an exception inside the exception handler — a blank 500 with no trace id, on the one route
    /// whose entire job is to still work when things are broken.
    /// </summary>
    [Fact]
    public async Task TheAnonymousErrorPage_Renders_AndCarriesTheSettingsIsland()
    {
        using var client = _serverMode.CreateNoRedirectClient();

        var response = await client.GetAsync("/Error");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains(IslandMarker, html, StringComparison.Ordinal);

        // Defaults only — an anonymous request has no user whose settings could be delivered. Pinning the
        // island's presence without this would permit the opposite bug (Share/View.cshtml handing an
        // anonymous viewer the OWNER's preferences).
        Assert.DoesNotContain("mapTilerApiKey", html, StringComparison.Ordinal);
    }
}
