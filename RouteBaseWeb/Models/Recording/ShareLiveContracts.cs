namespace RouteBaseWeb.Models.Recording;

// Wire + seed contracts for the PUBLIC live-share view (/s/{token}, kind = "live"). Each mirrors a TS
// interface in share-live-view.ts — edit both in the same commit. Serialized camelCase (web defaults).

/// <summary>
/// The static seed embedded in the share page (<c>&lt;script id="lv-share-data"&gt;</c>), the way the
/// authenticated LiveView seeds itself with <see cref="LiveSessionDto"/>. Carries only what a map-only
/// public view may show: the owner's display name, the activity type, when it started, and the last-known
/// status. Deliberately NO other rider, NO friends' data, NO PII beyond the display name, NO MapTiler key —
/// the same "publish nothing extra" stance as <see cref="Sharing.SegmentViewSurfaces.ReadOnlyShare"/>.
///
/// <c>ApiBase</c> is the token-scoped poll root (<c>/s/{token}/api/recording</c>) so the client never has
/// to reconstruct the plaintext token itself.
/// </summary>
public sealed record ShareLiveSeedDto(
    string ApiBase,
    string DisplayName,
    string ActivityType,
    DateTimeOffset StartedAt,
    // Null while active. Set once stopped/finalized so a viewer who opens the link AFTER the ride shows the
    // real recorded duration (stopped − started), not "now − started".
    DateTimeOffset? StoppedAt,
    [property: WireValues(typeof(RecordingSessionStatus))] string Status,
    int PointCount);

/// <summary>
/// One poll of the public live-share (<c>GET /s/{token}/api/recording/live?after={seq}</c>): the current
/// status (so the client can freeze + show its "finished" banner), the running point count, and any new
/// GPS points since the cursor. Reuses <see cref="LivePointDto"/> so the client's point consumer is the
/// same as the authenticated LiveView's.
/// </summary>
public sealed record ShareLiveUpdateDto(
    [property: WireValues(typeof(RecordingSessionStatus))] string Status,
    int PointCount,
    IReadOnlyList<LivePointDto> Points);
