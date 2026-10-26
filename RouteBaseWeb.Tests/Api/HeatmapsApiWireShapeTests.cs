using System.Text.Json;
using RouteBaseWeb.Models;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// Pins the wire shape of HeatmapsApiController's success bodies (Models/HeatmapContracts.cs),
/// formerly anonymous objects. The GET metadata shape must stay byte-identical: it mirrors the
/// TS HeatmapMeta interface (segment-stats/heatmap-layer.ts), whose consumers read every key.
/// The 202 Accepted body is unread by both TS callers.
/// </summary>
public class HeatmapsApiWireShapeTests
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

    private static readonly string[] MetaKeys =
    {
        "activityTypes", "bounds", "countMax", "error", "generatedAt",
        "layer", "maxZoom", "minZoom", "segmentId", "status",
    };

    [Fact]
    public void Accepted_KeySetAndValue()
    {
        var e = Serialize(new HeatmapAcceptedResponse(17));
        Assert.Equal(new[] { "segmentId" }, Keys(e));
        Assert.Equal(17, e.GetProperty("segmentId").GetInt32());
    }

    [Fact]
    public void Meta_Done_KeySetAndValues()
    {
        var e = Serialize(new HeatmapMetaResponse(
            SegmentId: 17,
            Status: "done",
            Error: null,
            Layer: "count_log",
            MinZoom: 6,
            MaxZoom: 15,
            CountMax: 42.5,
            Bounds: new HeatmapBoundsDto(South: 47.1, West: 8.2, North: 47.9, East: 9.3),
            ActivityTypes: "Run,Ride",
            GeneratedAt: new DateTimeOffset(2026, 7, 20, 1, 2, 3, TimeSpan.Zero)));

        Assert.Equal(MetaKeys, Keys(e));
        Assert.Equal(17, e.GetProperty("segmentId").GetInt32());
        Assert.Equal("done", e.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, e.GetProperty("error").ValueKind);
        Assert.Equal("count_log", e.GetProperty("layer").GetString());
        Assert.Equal(6, e.GetProperty("minZoom").GetInt32());
        Assert.Equal(15, e.GetProperty("maxZoom").GetInt32());
        Assert.Equal(42.5, e.GetProperty("countMax").GetDouble());
        Assert.Equal("Run,Ride", e.GetProperty("activityTypes").GetString());
        // AR-02: DateTimeOffset serializes the offset explicitly (+00:00, not Z). The one consumer
        // (heatmap-layer.ts) uses this as an opaque cache-buster string, so the form is free to pin.
        Assert.Equal("2026-07-20T01:02:03+00:00", e.GetProperty("generatedAt").GetString());

        var b = e.GetProperty("bounds");
        Assert.Equal(new[] { "east", "north", "south", "west" }, Keys(b));
        Assert.Equal(47.1, b.GetProperty("south").GetDouble());
        Assert.Equal(8.2, b.GetProperty("west").GetDouble());
        Assert.Equal(47.9, b.GetProperty("north").GetDouble());
        Assert.Equal(9.3, b.GetProperty("east").GetDouble());
    }

    [Fact]
    public void Meta_Pending_NullsPresentNotOmitted()
    {
        // Pre-render rows have no zoom/normalization/bounds/generatedAt yet — the keys must
        // still be on the wire as nulls (web defaults don't skip nulls; TS types them nullable).
        var e = Serialize(new HeatmapMetaResponse(
            SegmentId: 3,
            Status: "pending",
            Error: null,
            Layer: "count_log",
            MinZoom: null,
            MaxZoom: null,
            CountMax: null,
            Bounds: null,
            ActivityTypes: null,
            GeneratedAt: null));

        Assert.Equal(MetaKeys, Keys(e));
        Assert.Equal("pending", e.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, e.GetProperty("bounds").ValueKind);
        Assert.Equal(JsonValueKind.Null, e.GetProperty("minZoom").ValueKind);
        Assert.Equal(JsonValueKind.Null, e.GetProperty("generatedAt").ValueKind);
    }

    [Fact]
    public void Meta_Failed_CarriesErrorMessage()
    {
        var e = Serialize(new HeatmapMetaResponse(
            SegmentId: 3,
            Status: "failed",
            Error: "Operation queue is full — try again later.",
            Layer: "count_log",
            MinZoom: null,
            MaxZoom: null,
            CountMax: null,
            Bounds: null,
            ActivityTypes: null,
            GeneratedAt: null));

        Assert.Equal(MetaKeys, Keys(e));
        Assert.Equal("Operation queue is full — try again later.", e.GetProperty("error").GetString());
    }
}
