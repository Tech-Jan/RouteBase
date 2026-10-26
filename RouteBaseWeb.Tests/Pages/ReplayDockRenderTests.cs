using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Sharing;
using RouteBaseWeb.Tests.Helpers;
using Xunit;

namespace RouteBaseWeb.Tests.Pages;

/// <summary>
/// Every replay renders ONE dock — [grab handle][transport bar][chart strip] — from _ReplayDock.cshtml.
///
/// This is the guard that pays for the extraction. Three hand-rolled copies drifted silently: the race's
/// grip was not part of its bar at all (it sat below the map, with a dead band between), and its seek
/// slider had lost the focus outline the other two carry. Neither the compiler nor any test could see it —
/// the pages still rendered, just wrongly, on the one page nobody had smoke-tested lately.
///
/// So the assertions are the contract, not the markup: each page's dock exists, its grip is INSIDE the bar
/// and ABOVE the transport, the strip is the LAST thing in the stack, and the shared skin classes are the
/// ones the stylesheet actually targets. A page that stops calling the partial fails here.
/// </summary>
public class ReplayDockRenderTests : IClassFixture<RazorPartialRenderer>
{
    private readonly RazorPartialRenderer _renderer;

    public ReplayDockRenderTests(RazorPartialRenderer renderer) => _renderer = renderer;

    // The three replays, each named by the ids its own TypeScript resolves.
    private const string RaceHero = "/Pages/Races/_RaceHero.cshtml";
    private const string SegmentOverlay = "/Pages/Segments/_SegmentReplayOverlay.cshtml";
    private const string ActivityBody = "/Pages/Activities/_ActivityDetailBody.cshtml";

    private static ActivityDetailView ActivityOwnerView() => new()
    {
        Activity = new Activity
        {
            TrackId = new TrackId(7),
            UserId = "owner-user",
            TrackName = "Dock render",
            ActivityType = "Ride",
            Visibility = "private",
            StartDateTimeLocal = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Unspecified),
        },
        GpsData =
        [
            new() { GpsId = 1, GpsLat = 50.0, GpsLong = 4.0, Altitude = 10, Speed = 5, Distance = 0,
                    DateTimeUtc = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc) },
        ],
        Splits = [],
        IsOwner = true,
        Surfaces = ActivityViewSurfaces.ForDetailsPage(isOwner: true),
        MapTilerApiKey = string.Empty,
    };

    private Task<string> RenderAsync(string page) => page switch
    {
        RaceHero => _renderer.RenderAsync(page, model: true),          // canEdit
        SegmentOverlay => _renderer.RenderAsync(page, new object()),   // the partial takes no model
        _ => _renderer.RenderAsync(page, ActivityOwnerView()),
    };

    /// <summary>Index of <paramref name="needle"/>, asserted to exist so ordering checks cannot pass on -1.</summary>
    private static int IndexOf(string html, string needle)
    {
        var i = html.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(i >= 0, $"Expected the rendered dock to contain '{needle}'.");
        return i;
    }

    [Theory]
    [InlineData(RaceHero, "raceChartsDrawer", "raceReplayControls", "raceCharts", "raceReplaySeek", "raceReplayPlayPause")]
    [InlineData(SegmentOverlay, "segReplayDock", "segReplayControls", "segReplayCharts", "segReplaySeek", "segReplayPlayPause")]
    [InlineData(ActivityBody, "activityReplayDock", "replayControls", "activityReplayCharts", "replaySeek", "replayPlayPause")]
    public async Task EveryReplayRendersTheSharedDock(
        string page, string dockId, string controlsId, string stripId, string seekId, string playId)
    {
        var html = await RenderAsync(page);

        Assert.Contains($"id=\"{dockId}\" class=\"replay-dock replay-drawer\"", html, StringComparison.Ordinal);

        // The stack's order IS the interaction: the grip belongs to the bar it lifts, and the strip is
        // last so that growing it pushes bar and grip up. This is the invariant the race page lost.
        var bar = IndexOf(html, "class=\"replay-dock-bar\"");
        var grip = IndexOf(html, "class=\"replay-drawer-handle\"");
        var controls = IndexOf(html, $"id=\"{controlsId}\"");
        var strip = IndexOf(html, $"id=\"{stripId}\"");
        Assert.True(bar < grip && grip < controls && controls < strip,
            $"{page}: expected dock order bar({bar}) < grip({grip}) < transport({controls}) < strip({strip}).");

        Assert.Contains("replay-dock-strip", html[strip..], StringComparison.Ordinal);

        // The shared skin is applied by CLASS — replay-controls.css stopped targeting these by id, so a
        // dock that lost them would render an unstyled slider and a square play button.
        Assert.Contains($"id=\"{seekId}\" class=\"replay-seek\"", html, StringComparison.Ordinal);
        Assert.Contains($"id=\"{playId}\" type=\"button\" class=\"replay-play\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRaceStripArrivesThroughItsRuntimeResolvedPartial()
    {
        // The race strip arrives through StripPartial — a path resolved at RUNTIME, which the compiler
        // cannot check. It used to be TWO canvases, an elevation profile over a separate field-speed
        // chart; the speed is a trace on the profile now (core/profile-chart's ProfileTrace), so one
        // canvas is the whole strip and a second one reappearing would be the regression.
        var html = await RenderAsync(RaceHero);

        Assert.Contains("id=\"raceProfile\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"raceSpeedChart\"", html, StringComparison.Ordinal);
        Assert.Contains("click to split a section", html, StringComparison.Ordinal);   // canEdit: true
    }

    [Fact]
    public async Task TheRaceStripDropsTheEditHintForAShareViewer()
    {
        var html = await _renderer.RenderAsync(RaceHero, model: false);

        Assert.Contains("id=\"raceProfile\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("click to split a section", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSegmentDockKeepsItsOwnTwoControls()
    {
        // The mode toggle and the sharpening-progress text exist on this replay only; the shared partial
        // must not have flattened them away.
        var html = await RenderAsync(SegmentOverlay);

        Assert.Contains("id=\"segReplayMode\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"segReplayLoadingStatus\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheActivityDockStaysHiddenUntilPlayback()
    {
        // Its dock exists only while the replay runs — activity-replay-ui.ts flips both displays.
        var html = await RenderAsync(ActivityBody);

        Assert.Contains("id=\"activityReplayDock\" class=\"replay-dock replay-drawer\"\n     style=\"display: none;\"",
            html.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("id=\"replayControls\" class=\"replay-controls\"", html, StringComparison.Ordinal);
    }
}
