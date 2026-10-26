using System.Text.Json;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Sharing;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// F2b wire-shape pins for the #segmentStatisticsConfig island, written BEFORE the TS switch to the
/// generated type. The load-bearing fact: hasHeatmap/heatmapStatus are ABSENT (not false/null) when
/// the heatmap surface is off — heatmap-controls computes status 'none' from the absence and never
/// starts its 2s×300 poll. Serialized exactly as the page does (bare Serialize, no naming policy).
/// </summary>
public class SegmentStatsConfigWireShapeTests
{
    private static SegmentDetailView View(bool showHeatmap, Heatmap? heatmap = null) => new()
    {
        Segment = new Segment { SegmentId = new SegmentId(7), SegmentKind = SegmentKinds.Shapes, SegmentName = "S" },
        Surfaces = showHeatmap
            ? SegmentViewSurfaces.ForStatisticsPage(isOwner: true)
            : SegmentViewSurfaces.ReadOnlyShare,
        Heatmap = heatmap,
        MapTilerApiKey = "",
        ApiBase = "/api/SegmentsApi",
    };

    private static JsonElement Serialize(SegmentDetailView view) =>
        JsonDocument.Parse(JsonSerializer.Serialize(view.GetConfigForJavaScript())).RootElement;

    private static string[] Keys(JsonElement e) =>
        e.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    [Fact]
    public void HeatmapSurfaceOff_OmitsHasHeatmapAndStatusEntirely()
    {
        var json = Serialize(View(showHeatmap: false));

        Assert.False(json.TryGetProperty("hasHeatmap", out _), "hasHeatmap must be ABSENT, not false");
        Assert.False(json.TryGetProperty("heatmapStatus", out _), "heatmapStatus must be ABSENT, not null");
        Assert.False(json.GetProperty("showHeatmap").GetBoolean());

        // The always-present key set — a shrink or a casing change here breaks the page boot.
        // defaultMapStyle and chartDetail are deliberately NOT here: both are account settings and reach
        // the page as #appSettings. A page-config copy of one is the drift class this island no longer has.
        Assert.Equal(
            new[]
            {
                "apiBase", "canExcludeEfforts", "canLinkToActivities", "canManageSegment",
                "hasEfforts", "hasGpsData", "hasSegment",
                "kind", "mapTilerApiKey", "segmentId", "showHeatmap", "showRiderIdentity",
            },
            Keys(json));

        Assert.False(json.TryGetProperty("defaultMapStyle", out _),
            "the basemap default belongs to #appSettings, not to this island");

        // Removed 2026-08-02, same reason as defaultMapStyle: the duplicate copy had actually drifted —
        // the client fell back to "normal" while the real default is "auto".
        Assert.False(json.TryGetProperty("chartDetail", out _),
            "the chart-detail default belongs to #appSettings, not to this island");
    }

    [Fact]
    public void HeatmapSurfaceOn_NoAttachment_SendsHasHeatmapFalse_NoStatus()
    {
        var json = Serialize(View(showHeatmap: true));

        Assert.False(json.GetProperty("hasHeatmap").GetBoolean());
        Assert.False(json.TryGetProperty("heatmapStatus", out _), "no attachment → no status key");
    }

    [Fact]
    public void HeatmapSurfaceOn_WithAttachment_SendsStatus()
    {
        var json = Serialize(View(showHeatmap: true, new Heatmap { Status = HeatmapStatuses.Done }));

        Assert.True(json.GetProperty("hasHeatmap").GetBoolean());
        Assert.Equal(HeatmapStatuses.Done, json.GetProperty("heatmapStatus").GetString());
    }
}
