using RouteBaseWeb.Models.Sync;

namespace RouteBaseWeb.Models.Recording;

/// <summary>
/// One GPS point as a live viewer receives it — the single server→client contract for both emit paths:
/// the SignalR <c>ReceivePoints</c> broadcast and the REST catch-up <c>GET sessions/{id}/points</c>.
/// TS mirror: <c>PointPayload</c> (live-recording-shared.ts) — the two change together.
///
/// It exists because it did not (LV-01). The point shape was the one contract in the slice with no C#
/// type — an anonymous object built twice — and it was the one that was broken: the two paths emitted a
/// timestamp at different precisions (whole seconds from staging, milliseconds from the client echo) and
/// the client deduped by comparing those strings. <c>Seq</c> — the dense staging ordinal — is now the
/// cursor AND the dedup key.
///
/// <c>Seq</c> is non-null for every point either emit path produces (K): it is stamped by
/// <c>RecordingService.AppendPointsCoreAsync</c> before staging, and the ONLY writer of
/// <c>Recording_Staging</c> is that same append path. Alt/Spd dropped 2026-07-17 (perf ㉑) — no TS consumer
/// read them. <c>Ts</c> dropped 2026-07-22 (perf-11): it had zero TS consumers and was ~1/3 of the payload
/// pre-compression; the LV-01/LV-02 timestamp-precision contract it used to pin is now pinned at the layer
/// that owns it, <c>RecordingStagingStore</c> (TsEpoch), by those same two RecordingApiControllerTests.
/// </summary>
public sealed record LivePointDto(
    double Lat,
    double Lng,
    long Seq)
{
    public static LivePointDto From(ExternalGpsPoint p) => new(
        p.Lat,
        p.Lng,
        p.Seq ?? throw new InvalidOperationException(
            $"LivePointDto.From: a live-view point (lat {p.Lat}, lng {p.Lng}) had no Seq — every " +
            "staged point is stamped one before it reaches this DTO."));
}
