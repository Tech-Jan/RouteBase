using RouteBaseWeb.Constants;
using System.Text.RegularExpressions;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Sharing;
using RouteBaseWeb.Tests.Helpers;
using Xunit;

namespace RouteBaseWeb.Tests.Pages;

/// <summary>
/// Renders the SHARED segment-statistics body as an anonymous share viewer would receive it, and asserts
/// no owner affordance survives. The segment twin of <see cref="ShareViewRenderTests"/>.
///
/// Both routes render one partial, which is what stops them drifting — but it also means a new owner-only
/// control appears on the anonymous page unless it is gated on <c>Model.Surfaces</c>. Nothing about that
/// is visible to the compiler.
///
/// ⚠️ IMPORTANT LIMIT: the efforts GRID is built client-side by segment-stats-efforts-table.ts, so its
/// filter inputs, selection checkboxes, Exclude/Restore buttons and per-effort activity LINKS are
/// invisible to this harness. Those are pinned by segment-stats-efforts-table.test.ts instead — that
/// vitest file is load-bearing, not decorative.
/// </summary>
public class SegmentShareViewRenderTests : IClassFixture<RazorPartialRenderer>
{
    private const string BodyPath = "/Pages/Segments/_SegmentStatsBody.cshtml";

    private readonly RazorPartialRenderer _renderer;

    public SegmentShareViewRenderTests(RazorPartialRenderer renderer) => _renderer = renderer;

    private static Segment SampleSegment() => new()
    {
        SegmentId = new SegmentId(7),
        UserId = "owner-user",
        SegmentName = "Test climb",
        SegmentKind = "shapes",         // the awkward case: shapes segments carry the heatmap affordances
        SegmentDistance = 2500,
        SegmentAscent = 120,
        SegmentDescent = 30,
    };

    private static List<SegmentEffortDetailed> SampleEfforts() =>
    [
        new()
        {
            GpsInSegmentId = 1, SegmentId = 7, ActivityId = 100, ActivityName = "Morning ride",
            ActivityType = "Ride", ElapsedTime = 720, Distance = 2500, Elevation = 120,
            AverageSpeed = 3.5, AverageHeartRate = 150, MatchPercentage = 99,
            ActivityDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
        },
        new()
        {
            GpsInSegmentId = 2, SegmentId = 7, ActivityId = 101, ActivityName = "Evening ride",
            ActivityType = "Ride", ElapsedTime = 751, Distance = 2500, Elevation = 120,
            AverageSpeed = 3.3, AverageHeartRate = 148, MatchPercentage = 98,
            ActivityDate = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Unspecified),
        },
    ];

    private static SegmentDetailView View(SegmentViewSurfaces surfaces, Heatmap? heatmap) => new()
    {
        Segment = SampleSegment(),
        IsOwner = false,
        Surfaces = surfaces,
        DetailedEfforts = SampleEfforts(),
        EffortTotals = new SegmentEffortTotals
        {
            EffortCount = 2, TotalDistance = 5000, TotalElevation = 240, TotalTime = 1471,
        },
        Heatmap = heatmap,
        DefaultMatchThreshold = 80,
    };

    private Task<string> RenderShareAsync() =>
        _renderer.RenderAsync(BodyPath, View(SegmentViewSurfaces.ReadOnlyShare, heatmap: null));

    private Task<string> RenderOwnerAsync() =>
        _renderer.RenderAsync(BodyPath,
            View(SegmentViewSurfaces.ForStatisticsPage(isOwner: true),
                 new Heatmap { SegmentId = new SegmentId(7), Status = "done" }));

    // ---- per-id theory: the controls we already know are owner-only ----

    [Theory]
    [InlineData("matchThresholdInput")]   // owner panel: re-analysis threshold
    [InlineData("resetMatchThreshold")]   // an <a>, so only the zero-href assertion would otherwise catch it
    [InlineData("segReanalyzeBtn")]
    [InlineData("segDeleteBtn")]
    [InlineData("segHeatmapGenBtn")]
    [InlineData("shareActivityBtn")]      // minting is owner-only; a recipient must not re-share
    [InlineData("shareLinkDialog")]
    [InlineData("data-heatmap-view")]     // the heatmap map-view menu items
    public async Task ShareView_OmitsOwnerAffordance(string marker)
    {
        var html = await RenderShareAsync();

        Assert.DoesNotContain(marker, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OwnerView_StillOffersEverything()
    {
        // Anti-vacuity for the theory above: if the body stopped rendering the owner panel entirely, every
        // omission case would pass while the feature was broken.
        var html = await RenderOwnerAsync();

        foreach (var marker in new[]
                 { "matchThresholdInput", "segReanalyzeBtn", "segDeleteBtn", "segHeatmapGenBtn",
                   "shareActivityBtn", "shareLinkDialog", "data-heatmap-view" })
            Assert.Contains(marker, html, StringComparison.Ordinal);
    }

    // ---- structural guard: the control nobody thought of ----

    /// <summary>
    /// Every interactive control the anonymous segment share view may render. All of these are
    /// client-side view controls — no network, no identity, no mutation.
    /// </summary>
    private static readonly HashSet<string> AllowedShareControlIds = new(StringComparer.Ordinal)
    {
        // Chart controls: tab chips carry no id; these are the x-axis and progress-metric radios.
        "chartXTime", "chartXPercent", "chartAggregateToggle",
        "progressYTime", "progressYSpeed", "progressYPace", "progressYHr", "progressYPower", "progressYElev",
        "progressXEqual", "progressXDate",
        // Map toolbar + hero.
        "segReplayBtn", "segMapGrowBtn", "segMapShrinkBtn", "segMapResetBtn", "segMapExpandBtn",
        // Replay overlay chrome, including its seek slider and the hidden speed source-of-truth.
        // All local playback state: the replay animates data already on the page and issues no request
        // that is not already covered by the share-mounted tracks/performance endpoints.
        "segReplaySeek", "segReplaySpeed", "segReplayPlayPause", "segReplaySpeedSlower",
        "segReplaySpeedFaster", "segReplayExitFloat", "segReplayMode", "replayFollowToggle",
        // The elevation strip's drawer handle: show/hide (and drag-resize) a canvas drawn from the
        // effort data already on the page. Local view state in localStorage, no request of its own.
        "segReplayChartsHandle",
        // Speed chart Recorded/Smoothed toggle: re-derives the same effort points already on the page
        // with or without a smoothing pass. No request, no identity; preference is per-browser.
        "segSpeedModeToggle",
    };

    private static IEnumerable<string> ControlIdsIn(string html) =>
        Regex.Matches(html, "<(?:button|select|input|textarea)\\b[^>]*\\bid=\"(?<id>[^\"]+)\"", RegexOptions.None, RegexDefaults.MatchTimeout)
             .Select(m => m.Groups["id"].Value);

    private static IEnumerable<string> HrefsIn(string html) =>
        Regex.Matches(html, "\\bhref=\"(?<href>[^\"]*)\"", RegexOptions.None, RegexDefaults.MatchTimeout).Select(m => m.Groups["href"].Value);

    [Fact]
    public async Task ShareView_RendersNoUnreviewedControl()
    {
        // The id-by-id theory only catches controls someone already thought of; this catches one nobody
        // thought of, which is the actual risk as the segment page evolves.
        var html = await RenderShareAsync();

        var unexpected = ControlIdsIn(html).Where(id => !AllowedShareControlIds.Contains(id))
            .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();

        Assert.True(unexpected.Length == 0,
            "The anonymous segment share view rendered control(s) not on the reviewed list: "
            + $"[{string.Join(", ", unexpected)}].\n"
            + "If this is an owner action, gate it on Model.Surfaces. If an anonymous viewer genuinely "
            + "should have it, add the id to AllowedShareControlIds with a reason.");
    }

    [Fact]
    public async Task ShareView_RendersNoLinkAtAll()
    {
        // Every plausible link target (/Segments, /Activities/…) is an authenticated route that would
        // bounce an anonymous visitor into the login flow.
        var html = await RenderShareAsync();

        var hrefs = HrefsIn(html).Distinct(StringComparer.Ordinal).ToArray();

        Assert.True(hrefs.Length == 0,
            $"The anonymous segment share view rendered link(s): [{string.Join(", ", hrefs)}]. Gate them "
            + "on Model.Surfaces, or add them here if they are genuinely public.");
    }

    [Fact]
    public void TheStructuralGuardIsNotVacuous()
    {
        const string sample =
            "<button id=\"a\">x</button><a href=\"/b\">y</a>"
            + "<select id=\"c\"></select><input id=\"d\" /><textarea id=\"e\"></textarea>";

        Assert.Equal(["a", "c", "d", "e"], ControlIdsIn(sample), StringComparer.Ordinal);
        Assert.Equal(["/b"], HrefsIn(sample), StringComparer.Ordinal);
    }

    [Fact]
    public async Task OwnerAndShare_RenderTheSameCoreContent()
    {
        // Both routes must render one markup. If the share view ever stops showing the map, the charts or
        // the efforts grid mount, the partials have forked.
        var share = await RenderShareAsync();
        var owner = await RenderOwnerAsync();

        foreach (var marker in new[]
                 { "seg-kpi-strip", "id=\"map\"", "performanceChart", "segEffortsGrid", "segmentReplayOverlay" })
        {
            Assert.Contains(marker, share, StringComparison.Ordinal);
            Assert.Contains(marker, owner, StringComparison.Ordinal);
        }
    }
}
