namespace RouteBaseWeb.Models.Recording;

/// <summary>
/// The response to <c>POST sessions/{id}/points</c>. Kotlin mirror: <c>AppendPointsResponse</c>
/// (ApiService.kt); TS mirror: the uploader's post-result shape (live-recording-record.ts).
///
/// It is a named type because the point-append contract used to be an anonymous object — the one untyped
/// contract in the slice, and the one that was broken (LV-01).
/// </summary>
/// <param name="Inserted">Points the server stored from this batch (0 = dedup-eaten or nothing pending).</param>
/// <param name="LastSequence">
/// AQ-S1: the session's post-append LastSequence, so a reconnecting/409-adopting client can resync its
/// clientSequence — a dedup-eaten batch is distinguishable from a stored one by <c>LastSequence</c> ≥ the
/// sequence it sent.
/// </param>
/// <param name="Watched">
/// True while at least one live viewer is joined to this session's hub group. The phone uploads at a fast
/// cadence only while someone is actually watching: at 5 s intervals the LTE modem's tail time never
/// expires, so the radio never idles for the whole ride. Recording clients that do not gate on it (the web
/// recorder — its own tab is the viewer) simply ignore the field.
/// </param>
public sealed record AppendPointsResponse(int Inserted, int LastSequence, bool Watched);
