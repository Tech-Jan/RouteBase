using RouteBaseWeb.Helpers;

namespace RouteBaseWeb.Models.Recording;

/// <summary>
/// The single server→client contract for a recording session (CR5-04). Every session-returning
/// endpoint emits this exact shape, replacing three ad-hoc anonymous shapes (start/stop 3-field,
/// full get) that the Android client compensated for with nullable-default Gson workarounds (AQ-08).
/// It is a <b>superset</b> of the old shapes — every key the old start/stop/get responses carried
/// remains, casing unchanged via web JSON defaults — so existing Gson clients (which ignore unknown
/// keys) are unaffected. This gives the contract a C# type to grep and pin.
/// </summary>
public sealed record RecordingSessionDto(
    string SessionId,
    int TrackId,
    [property: WireValues(typeof(RecordingSessionStatus))] string Status,
    string ActivityType,
    [property: WireValues(typeof(Visibilities))] string Visibility,
    [property: System.Text.Json.Serialization.JsonConverter(typeof(UtcZuluDateTimeOffsetConverter))]
    DateTimeOffset StartedAt,
    [property: System.Text.Json.Serialization.JsonConverter(typeof(UtcZuluDateTimeOffsetConverter))]
    DateTimeOffset? StoppedAt,
    [property: System.Text.Json.Serialization.JsonConverter(typeof(UtcZuluDateTimeOffsetConverter))]
    DateTimeOffset? LastPointAt,
    int PointCount,
    int LastSequence)
{
    public static RecordingSessionDto From(RecordingSession s) => new(
        s.SessionId,
        s.TrackId.Value,
        s.Status,
        s.ActivityType,
        s.Visibility,
        // ToUtcOffset, not an implicit cast: the DB round-trip strips DateTimeKind, and the implicit
        // conversion would read Unspecified as server-local (AR-02). The Zulu converter on the properties
        // keeps the Android-facing wire on the trailing-Z form.
        s.StartedAt.ToUtcOffset(),
        s.StoppedAt.ToUtcOffset(),
        s.LastPointAt.ToUtcOffset(),
        s.PointCount,
        s.LastSequence);
}
