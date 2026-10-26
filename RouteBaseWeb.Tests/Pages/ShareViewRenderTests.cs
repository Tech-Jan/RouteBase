using RouteBaseWeb.Constants;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Sharing;
using RouteBaseWeb.Tests.Helpers;
using Xunit;

namespace RouteBaseWeb.Tests.Pages;

/// <summary>
/// Renders the SHARED activity-detail body as an anonymous share viewer would receive it, and asserts no
/// owner affordance survives.
///
/// This is the guard the architecture could not provide on its own. Both routes render one partial, which
/// is what stops the two from drifting — but it also means a new owner-only control added to that partial
/// appears on the anonymous page unless it is gated on <c>Model.Surfaces</c>. Nothing about that is visible
/// to the compiler, and no other test renders HTML.
///
/// Nothing here is a security boundary: the endpoint allowlist and the fail-closed <c>share:anon</c>
/// identity mean a leaked control could not actually DO anything. These assertions protect the share
/// page from shipping visibly broken — dead links and console 404s.
/// </summary>
public class ShareViewRenderTests : IClassFixture<RazorPartialRenderer>
{
    private const string BodyPath = "/Pages/Activities/_ActivityDetailBody.cshtml";

    private readonly RazorPartialRenderer _renderer;

    public ShareViewRenderTests(RazorPartialRenderer renderer) => _renderer = renderer;

    private static Activity SampleActivity() => new()
    {
        TrackId = new TrackId(42),
        UserId = "owner-user",
        TrackName = "Morning ride",
        ActivityType = "Ride",
        Visibility = "private",          // the awkward case: a deliberately shared PRIVATE ride
        TotalDistance = 42_000,
        TotalAscent = 500,
        TimeTotal = 3600,
        TimeMoving = 3400,
        SpeedAvg = 8,
        SpeedMax = 14,
        HeartRateAvg = 140,
        HeartRateMax = 175,
        StartDateTimeLocal = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Unspecified),
    };

    private static List<GpsData> SampleTrack() =>
    [
        new() { GpsId = 1, GpsLat = 50.0, GpsLong = 4.0, Altitude = 10, Speed = 5, Distance = 0,
                DateTimeUtc = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc), HeartRate = 130 },
        new() { GpsId = 2, GpsLat = 50.001, GpsLong = 4.001, Altitude = 12, Speed = 6, Distance = 500,
                DateTimeUtc = new DateTime(2026, 1, 1, 10, 1, 0, DateTimeKind.Utc), HeartRate = 145 },
    ];

    private static ActivityDetailView ShareView() => new()
    {
        Activity = SampleActivity(),
        GpsData = SampleTrack(),
        Splits = [],
        IsOwner = false,
        Surfaces = ActivityViewSurfaces.ReadOnlyShare,
        MapTilerApiKey = string.Empty,
        PhotosApiBase = "/s/testtoken/api/photos",
    };

    private static ActivityDetailView OwnerView() => new()
    {
        Activity = SampleActivity(),
        GpsData = SampleTrack(),
        Splits = [],
        IsOwner = true,
        Surfaces = ActivityViewSurfaces.ForDetailsPage(isOwner: true),
        MapTilerApiKey = "maptiler-secret",
    };

    private Task<string> RenderShareAsync() => _renderer.RenderAsync(BodyPath, ShareView());
    private Task<string> RenderOwnerAsync() => _renderer.RenderAsync(BodyPath, OwnerView());

    // ---- harness sanity ----

    [Fact]
    public async Task TheHarnessRendersRealMarkup()
    {
        // Anti-vacuity: every "does not contain" assertion below is worthless if rendering silently
        // produced nothing.
        var html = await RenderShareAsync();

        Assert.Contains("kpi-strip", html, StringComparison.Ordinal);
        Assert.Contains("Morning ride", html, StringComparison.Ordinal);
        Assert.Contains("det-map-hero", html, StringComparison.Ordinal);
    }

    // ---- the share view offers no owner actions ----

    [Theory]
    [InlineData("editActivityBtn", "inline rename/type/visibility editing")]
    [InlineData("saveActivityBtn", "save of an inline edit")]
    [InlineData("deleteActivityBtn", "delete")]
    [InlineData("refreshElevationBtn", "elevation recompute")]
    [InlineData("shareActivityBtn", "minting further share links")]
    [InlineData("visibilitySelect", "changing who can see the ride")]
    [InlineData("activityTypeSelect", "changing the activity type")]
    [InlineData("editNameInput", "renaming")]
    [InlineData("shareLinkDialog", "the share-management dialog")]
    public async Task ShareView_OmitsOwnerControl(string elementId, string what)
    {
        var html = await RenderShareAsync();

        Assert.False(html.Contains(elementId, StringComparison.Ordinal),
            $"The anonymous share view rendered '{elementId}' ({what}). Gate it on Model.Surfaces.");
    }

    [Fact]
    public async Task ShareView_OffersNoSegmentCreation()
    {
        // Both the tab and the panel: the panel's TS fires GET /api/SegmentsApi on page load, which a
        // share viewer has no grant for.
        var html = await RenderShareAsync();

        Assert.DoesNotContain("detSegmentTab", html, StringComparison.Ordinal);
        Assert.DoesNotContain("segmentCreator", html, StringComparison.Ordinal);
        Assert.DoesNotContain("det-custom-panel", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShareView_OffersNoPhotoUpload()
    {
        var html = await RenderShareAsync();

        Assert.DoesNotContain("/Import", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Add photos", html, StringComparison.Ordinal);
    }

    // ---- no dead links into the authenticated app ----

    [Fact]
    public async Task ShareView_LinksNowhereIntoTheApp()
    {
        // An anonymous visitor following any of these lands on a login redirect, which reads as the share
        // link being broken.
        var html = await RenderShareAsync();

        Assert.DoesNotContain("href=\"/Activities\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/Races?courseTrackId", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/activities/export", html, StringComparison.Ordinal);
        Assert.DoesNotContain("page-breadcrumb", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShareView_DoesNotAnnounceTheRidesVisibility()
    {
        // The sample activity is PRIVATE and deliberately shared. Rendering a "Private" badge to the very
        // person the owner sent the link to is actively misleading.
        var html = await RenderShareAsync();

        Assert.DoesNotContain("visibilityBadge", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Friend's activity", html, StringComparison.Ordinal);
    }

    // ---- but the actual content is all there ----

    [Fact]
    public async Task ShareView_KeepsTheContentWorthSharing()
    {
        var html = await RenderShareAsync();

        Assert.Contains("id=\"map\"", html, StringComparison.Ordinal);          // map
        Assert.Contains("speedChart", html, StringComparison.Ordinal);          // charts
        Assert.Contains("heartRateChart", html, StringComparison.Ordinal);      // HR chart (data present)
        Assert.Contains("replayControls", html, StringComparison.Ordinal);      // replay chrome
        Assert.Contains("playReplayBtn", html, StringComparison.Ordinal);       // replay entry point
        Assert.Contains("detPhotosGrid", html, StringComparison.Ordinal);       // photo grid
        Assert.Contains("detMapStyleSwitcher", html, StringComparison.Ordinal); // basemap switcher
    }

    // ---- the owner page is unaffected ----

    [Fact]
    public async Task OwnerView_StillOffersEverything()
    {
        // The counterpart to the gates above: proves they are conditional, not accidental deletions.
        var html = await RenderOwnerAsync();

        foreach (var expected in new[]
                 {
                     "editActivityBtn", "deleteActivityBtn", "refreshElevationBtn", "shareActivityBtn",
                     "visibilityBadge", "detSegmentTab", "det-custom-panel", "page-breadcrumb",
                     "/api/activities/export",
                     // The three hidden edit-mode inputs. Listed explicitly because they are CSS-hidden:
                     // a gate that removed them outright would go unnoticed in a manual UI check, and
                     // inline editing would silently stop working.
                     "editNameInput", "activityTypeSelect", "visibilitySelect",
                 })
            Assert.True(html.Contains(expected, StringComparison.Ordinal),
                $"The owner's page lost '{expected}' — a Surfaces gate is too aggressive.");
    }

    // ---- the structural guard: NEW affordances cannot slip through ----

    /// <summary>
    /// Every interactive control the anonymous share view is allowed to render — buttons, selects and
    /// inputs alike. This is the reviewed surface: a new control appearing here means someone added it to
    /// the shared partials without a Surfaces gate, and it must be justified before joining this list.
    ///
    /// Selects and inputs matter as much as buttons and are easier to miss, because the owner-edit ones
    /// are CSS-hidden — two of them (editNameInput, activityTypeSelect) shipped onto the anonymous page
    /// and were invisible to manual inspection until this harness rendered the HTML.
    /// </summary>
    private static readonly HashSet<string> AllowedShareControlIds = new(StringComparer.Ordinal)
    {
        // Map toolbar — all client-side, no network, no identity.
        "playReplayBtn", "colorNoneBtn", "colorSpeedBtn", "colorHrBtn", "colorElevBtn", "colorCadenceBtn",
        "detPhotosMapToggle", "mapGrowBtn", "mapShrinkBtn", "mapResetBtn", "mapFullscreenBtn",
        // Replay chrome, including its seek slider and the hidden speed source-of-truth.
        "replayPlayPause", "replaySpeedSlower", "replaySpeedFaster", "replayExit", "replayFollowToggle",
        "replaySeek", "replaySpeed",
        // The elevation strip's drag handle: pulls a canvas drawn from the GPS already on the page up and
        // down. Local view state in localStorage, no request of its own — and it only unhides when the
        // strip mounts at all.
        "activityReplayChartsHandle",
        // Photos panel header (the grid itself is populated client-side).
        "detPhotosTab",
        // Speed chart Recorded/Smoothed/Full toggle. Every view is derived in the browser from the
        // GPS points already delivered to it — "Recorded" is the same channel the smoothed line is
        // built from, and the spread band is its min/max. No request, no identity, nothing the viewer
        // cannot already read off the chart. Preference lives in this browser's localStorage.
        "speedChartModeToggle",
    };

    /// <summary>Ids of every interactive element in the markup: button, select, input, textarea.</summary>
    private static IEnumerable<string> ControlIdsIn(string html) =>
        System.Text.RegularExpressions.Regex
            .Matches(html, "<(?:button|select|input|textarea)\\b[^>]*\\bid=\"(?<id>[^\"]+)\"", System.Text.RegularExpressions.RegexOptions.None, RegexDefaults.MatchTimeout)
            .Select(m => m.Groups["id"].Value);

    private static IEnumerable<string> HrefsIn(string html) =>
        System.Text.RegularExpressions.Regex
            .Matches(html, "\\bhref=\"(?<href>[^\"]*)\"", System.Text.RegularExpressions.RegexOptions.None, RegexDefaults.MatchTimeout)
            .Select(m => m.Groups["href"].Value);

    [Fact]
    public async Task ShareView_RendersNoUnreviewedControl()
    {
        // THE guard for the open-ended case. The id-by-id theory above only catches controls someone
        // already thought of; this catches a control nobody thought of, which is the actual risk when the
        // details page evolves. Verified by injecting an ungated button and watching it fail.
        var html = await RenderShareAsync();

        var unexpected = ControlIdsIn(html).Where(id => !AllowedShareControlIds.Contains(id))
            .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();

        Assert.True(unexpected.Length == 0,
            "The anonymous share view rendered control(s) that are not on the reviewed list: "
            + $"[{string.Join(", ", unexpected)}].\n"
            + "If this is an owner action, gate it on Model.Surfaces. If an anonymous viewer genuinely "
            + "should have it, add the id to AllowedShareControlIds with a reason.");
    }

    [Fact]
    public async Task ShareView_RendersNoLinkAtAll()
    {
        // Simpler and stricter than an allowlist: the share body currently links nowhere, and every
        // plausible link target (/, /Activities, /Import, /Races, the GPX export) is an authenticated
        // route that would bounce an anonymous visitor to a login page.
        var html = await RenderShareAsync();

        var hrefs = HrefsIn(html).Distinct(StringComparer.Ordinal).ToArray();

        Assert.True(hrefs.Length == 0,
            $"The anonymous share view rendered link(s): [{string.Join(", ", hrefs)}]. Anonymous visitors "
            + "cannot follow app routes — gate the link on Model.Surfaces, or add it here if it is public.");
    }

    [Fact]
    public void TheStructuralGuardIsNotVacuous()
    {
        // Proves the regexes match the markup shape actually rendered; if they silently stopped matching,
        // both tests above would pass while checking nothing.
        const string sample =
            "<button id=\"a\">x</button><a href=\"/b\">y</a>"
            + "<select id=\"c\"></select><input id=\"d\" /><textarea id=\"e\"></textarea>";

        Assert.Equal(["a", "c", "d", "e"], ControlIdsIn(sample), StringComparer.Ordinal);
        Assert.Equal(["/b"], HrefsIn(sample), StringComparer.Ordinal);
    }

    [Fact]
    public async Task OwnerAndShare_RenderTheSameCoreContent()
    {
        // Both routes must render one markup. If the share view ever stops showing the map or the charts,
        // the partials have forked.
        var share = await RenderShareAsync();
        var owner = await RenderOwnerAsync();

        foreach (var shared in new[] { "kpi-strip", "id=\"map\"", "speedChart", "replayControls" })
        {
            Assert.Contains(shared, share, StringComparison.Ordinal);
            Assert.Contains(shared, owner, StringComparison.Ordinal);
        }
    }
}
