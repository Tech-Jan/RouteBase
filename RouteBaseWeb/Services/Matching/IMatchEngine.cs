using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Sql;

namespace RouteBaseWeb.Services.Matching;

/// <summary>
/// The segment-match stages 1–6 + the INSERT gate. Runs inside the caller's pinned analysis connection
/// and writes GPSinSegment rows keyed by GPS_ID (project invariant 6), invariant 2.
/// </summary>
public interface IMatchEngine
{
    /// <summary>
    /// Matches the scope's own activities against <paramref name="scope"/>'s segment and inserts
    /// the qualifying crossings into GPSinSegment. Downstream direction/offset/aggregate steps run
    /// unchanged on the inserted rows. Must not open/close the pinned connection (S0.4).
    /// </summary>
    Task RunAsync(AnalysisScope scope, int matchThresholdPercent, CancellationToken ct = default);

    /// <summary>
    /// Track-restricted variant of <see cref="RunAsync"/> for the incremental per-import path: matches a
    /// single <paramref name="trackId"/> against the scope's segment and inserts only that track's
    /// qualifying crossings, reusing the frozen <c>MatchEngineCore</c>. The caller deletes that
    /// (track, segment) pair's rows first (idempotency). Must not open/close the pinned connection (S0.4).
    /// </summary>
    Task RunForTrackAsync(AnalysisScope scope, TrackId trackId, int matchThresholdPercent, CancellationToken ct = default);
}
