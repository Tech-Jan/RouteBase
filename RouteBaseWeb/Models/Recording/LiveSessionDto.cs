using RouteBaseWeb.Helpers;
using RouteBaseWeb.Services;

namespace RouteBaseWeb.Models.Recording;

/// <summary>
/// The single server→client contract for a watchable live session — the LiveView page seeds itself
/// with it and the sessions/live poll returns it (TS mirror: LiveSessionMeta). One shape, so the two
/// paths cannot drift.
/// </summary>
public sealed record LiveSessionDto(
    string SessionId,
    string DisplayName,
    bool IsSelf,
    string ActivityType,
    DateTimeOffset StartedAt,
    [property: WireValues(typeof(RecordingSessionStatus))] string Status,
    int TrackId,
    int PointCount)
{
    public static LiveSessionDto From(LiveSessionInfo s) => new(
        s.SessionId,
        s.DisplayName,
        s.IsSelf,
        s.ActivityType,
        // ToUtcOffset, not an implicit cast: the DB round-trip strips DateTimeKind, and the implicit
        // conversion would read Unspecified as server-local — skewing every elapsed clock (AR-02).
        s.StartedAt.ToUtcOffset(),
        s.Status,
        s.TrackId,
        s.PointCount);
}
