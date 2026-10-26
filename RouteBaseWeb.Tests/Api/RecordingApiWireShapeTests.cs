using System.Text.Json;
using RouteBaseWeb.Models.Recording;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// Pins the wire shape of POST .../retry-finalize (Models/Recording/FinalizeRetryResponse.cs) — the
/// only remaining anonymous body in RecordingApiController before this pass. Unread by the TS client
/// (live-recording/live-recording-record.ts checks resp.ok only). The rest of the controller already used named
/// DTOs (AppendPointsResponse, RecordingSessionDto, LivePointDto, LiveSessionDto) from an earlier pass.
/// </summary>
public class RecordingApiWireShapeTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static JsonElement Serialize(object value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value, Web)).RootElement.Clone();

    [Fact]
    public void FinalizeRetry_KeySetAndValue()
    {
        var e = Serialize(new FinalizeRetryResponse(false));
        var keys = new List<string>();
        foreach (var p in e.EnumerateObject()) keys.Add(p.Name);
        Assert.Equal(new[] { "finalized" }, keys, StringComparer.Ordinal);
        Assert.False(e.GetProperty("finalized").GetBoolean());
    }

    // AppendPointsResponse is the phone's highest-frequency read; a key rename would make the phone's
    // Gson default all three fields, disarming the AQ-04 stored-vs-dedup-eaten guard (PointDelivery.kt)
    // and advancing-and-deleting unstored rows. These pins are the only C# guard on the key names.
    [Fact]
    public void AppendPoints_KeySetIsExactCamelCase()
    {
        var e = Serialize(new AppendPointsResponse(3, 42, true));
        var keys = new List<string>();
        foreach (var p in e.EnumerateObject()) keys.Add(p.Name);
        Assert.Equal(
            new[] { "inserted", "lastSequence", "watched" },
            keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void AppendPoints_ValuesRoundTrip()
    {
        var e = Serialize(new AppendPointsResponse(3, 42, true));
        Assert.Equal(3, e.GetProperty("inserted").GetInt32());
        Assert.Equal(42, e.GetProperty("lastSequence").GetInt32());
        Assert.True(e.GetProperty("watched").GetBoolean());
    }
}
