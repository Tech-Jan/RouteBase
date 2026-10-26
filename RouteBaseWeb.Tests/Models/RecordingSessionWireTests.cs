using System.Linq;
using System.Text.Json;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Recording;
using Xunit;

namespace RouteBaseWeb.Tests;

/// <summary>
/// CR5-04 pin: the one recording-session wire shape emitted by every session endpoint. Locks the
/// exact camelCase key set and the Z-suffixed instants, and asserts the two keys the Android client
/// used to null-default around (status, lastSequence) are always present — so start/stop responses
/// (which used to omit them) now carry them. Serialized with web defaults, matching controller output.
/// </summary>
public class RecordingSessionWireTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static RecordingSession Sample() => new()
    {
        SessionId = "sess-1", TrackId = new TrackId(42), Status = "recording", ActivityType = "Ride",
        Visibility = "friends", PointCount = 120, LastSequence = 119,
        StartedAt   = new DateTime(2026, 7, 11, 10, 0, 0, DateTimeKind.Unspecified),
        StoppedAt   = new DateTime(2026, 7, 11, 11, 0, 0, DateTimeKind.Unspecified),
        LastPointAt = new DateTime(2026, 7, 11, 10, 59, 0, DateTimeKind.Unspecified),
    };

    private static JsonElement Serialize(RecordingSession s)
        => JsonDocument.Parse(JsonSerializer.Serialize(RecordingSessionDto.From(s), Web)).RootElement.Clone();

    [Fact]
    public void From_EmitsExactCamelCaseKeySet()
    {
        var keys = Serialize(Sample()).EnumerateObject().Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[]
            {
                "activityType", "lastPointAt", "lastSequence", "pointCount", "sessionId",
                "startedAt", "status", "stoppedAt", "trackId", "visibility",
            },
            keys);
    }

    [Fact]
    public void From_StampsInstantsWithTrailingZ()
    {
        var e = Serialize(Sample());
        Assert.EndsWith("Z", e.GetProperty("startedAt").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("Z", e.GetProperty("stoppedAt").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("Z", e.GetProperty("lastPointAt").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void From_NullInstants_SerializeAsNull()
    {
        var s = Sample();
        s.StoppedAt = null;
        s.LastPointAt = null;
        var e = Serialize(s);
        Assert.Equal(JsonValueKind.Null, e.GetProperty("stoppedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, e.GetProperty("lastPointAt").ValueKind);
    }

    [Fact]
    public void From_CarriesStatusAndLastSequence_TheKotlinCompensationHazards()
    {
        var e = Serialize(Sample());
        Assert.Equal("recording", e.GetProperty("status").GetString());
        Assert.Equal(119, e.GetProperty("lastSequence").GetInt32());
    }
}
