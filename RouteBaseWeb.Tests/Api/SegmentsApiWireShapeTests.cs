using System.Text.Json;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// Pins the wire shape of SegmentsApiController's success/conflict bodies (Models/SegmentsContracts.cs).
/// gps-summary reuses ActivityContracts' LatLngDto; full-track/bulk-tracks now serve GpsCoordinate
/// directly (was a redundant re-projection of the same three fields).
/// </summary>
public class SegmentsApiWireShapeTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static JsonElement Serialize(object value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value, Web)).RootElement.Clone();

    private static string[] Keys(JsonElement e)
    {
        var keys = new List<string>();
        foreach (var p in e.EnumerateObject()) keys.Add(p.Name);
        keys.Sort(StringComparer.Ordinal);
        return keys.ToArray();
    }

    [Fact]
    public void Summary_KeySet()
    {
        var e = Serialize(new SegmentSummaryDto(1, "Hill", 500, 50, 10, "Ride"));
        Assert.Equal(new[] { "activityType", "segmentAscent", "segmentDescent", "segmentDistance", "segmentId", "segmentName" }, Keys(e));
    }

    [Fact]
    public void AnalysisConflict_KeySet()
    {
        var e = Serialize(new SegmentAnalysisConflictBody("Analysis is already running", new SegmentAnalysisProgress(TimeProvider.System).GetStatus("u")));
        Assert.Equal(new[] { "code", "message", "status" }, Keys(e)); // D32: { code, message, status }
        Assert.Equal("conflict", e.GetProperty("code").GetString());
    }

    [Fact]
    public void AnalysisQueued_KeySetAndValue()
    {
        var e = Serialize(new AnalysisQueuedResponse("Analysis queued in background"));
        Assert.Equal(new[] { "message" }, Keys(e));
    }

    [Fact]
    public void Created_KeySet()
    {
        var e = Serialize(new CreatedSegmentResponse(1, "Hill", 500, 50, 10, "Ride", true));
        Assert.Equal(new[] { "activityType", "analysisQueued", "segmentAscent", "segmentDescent", "segmentDistance", "segmentId", "segmentName" }, Keys(e));
    }

    [Fact]
    public void ShapeSegment_KeySet()
    {
        var e = Serialize(new ShapeSegmentResponse(1, "Hill", "shapes", "Ride", true));
        Assert.Equal(new[] { "activityType", "analysisQueued", "segmentId", "segmentKind", "segmentName" }, Keys(e));
    }

    [Fact]
    public void ShapePreview_KeySetAndValues()
    {
        var e = Serialize(new ShapePreviewResponse(2, new[] { 7, 9 }));
        Assert.Equal(new[] { "count", "trackIds" }, Keys(e));
        Assert.Equal(2, e.GetProperty("count").GetInt32());
    }

    [Fact]
    public void ShapePreviewExact_KeySetAndValues()
    {
        var e = Serialize(new ShapePreviewExactResponse(2, new[] { 7, 9 }, 5, true));
        Assert.Equal(new[] { "count", "effortCount", "trackIds", "truncated" }, Keys(e));
        Assert.Equal(5, e.GetProperty("effortCount").GetInt32());
        Assert.True(e.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void EffortSummary_KeySet()
    {
        var e = Serialize(new SegmentEffortSummaryDto(
            1, 2, "Hill", 3, "Morning Ride", "2026-07-11T09:30:00", "Ride", 95, 500, 120,
            4.2, 150, 80, 200, 30, 10, 20));
        Assert.Equal(new[]
        {
            "activityDate", "activityId", "activityName", "activityType", "averageCadence",
            "averageHeartRate", "averagePower", "averageSpeed", "distance", "elapsedTime",
            "elevation", "gpsIdEntry", "gpsIdExit", "gpsInSegmentId", "matchPercentage",
            "segmentId", "segmentName",
        }, Keys(e));
    }

    [Fact]
    public void ZoneRect_KeySet()
    {
        var e = Serialize(new ZoneRectDto(47.0, 47.1, 11.0, 11.1, "zone"));
        Assert.Equal(new[] { "maxLat", "maxLong", "minLat", "minLong", "role" }, Keys(e));
    }

    [Fact]
    public void GateLine_KeySet()
    {
        var e = Serialize(new GateLineDto(47.0, 11.0, 47.1, 11.1, "gate"));
        Assert.Equal(new[] { "lat1", "lat2", "lng1", "lng2", "role" }, Keys(e));
    }

    [Fact]
    public void SegmentOutline_KeySet()
    {
        var e = Serialize(new SegmentOutlineDto(
            new[] { new ZoneRectDto(47.0, 47.1, 11.0, 11.1, "zone") },
            new[] { new GateLineDto(47.0, 11.0, 47.1, 11.1, "gate") }));
        Assert.Equal(new[] { "gates", "zones" }, Keys(e));
        Assert.Equal(1, e.GetProperty("zones").GetArrayLength());
        Assert.Equal(1, e.GetProperty("gates").GetArrayLength());
    }

    [Fact]
    public void EffortsSummary_KeySetAndNesting()
    {
        var e = Serialize(new EffortsSummaryDto(
            new[] { new EffortPolylineDto(new[] { new LatLngDto(47.0, 11.0), new LatLngDto(47.1, 11.1) }) },
            Truncated: false));
        Assert.Equal(new[] { "efforts", "truncated" }, Keys(e));
        var pts = e.GetProperty("efforts")[0].GetProperty("points");
        Assert.Equal(2, pts.GetArrayLength());
        Assert.Equal(new[] { "lat", "lng" }, Keys(pts[0]));
    }
}
