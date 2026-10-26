using RouteBaseWeb.Services.Sql;

namespace RouteBaseWeb.Services.Matching;

/// <summary>
/// The post-match effort math — the pair-coherence offset search + per-effort aggregates. Runs inside
/// the caller's pinned analysis connection and writes the GPSinSegment calculated-id / offset / effort_*
/// columns (invariant 2). Must not open/close the pinned connection (S0.4).
/// </summary>
public interface IEffortMath
{
    /// <summary>
    /// Calculates entry/exit offsets for the scope's OWN efforts on the segment (S8.1 own-effort
    /// scope) and runs the two verbatim quality-guard DELETEs (S8.13, exit → entry).
    /// <paramref name="onlyTrackId"/> (incremental per-import path) further restricts both the compute
    /// set and the guard DELETEs to that one track; null = whole-segment (analyze-all, byte-identical).
    /// </summary>
    Task CalculateOffsetsAsync(AnalysisScope scope, int? onlyTrackId = null, CancellationToken ct = default);

    /// <summary>
    /// Fills the seven <c>effort_*</c> aggregate columns for ALL efforts on the segment — any owner,
    /// friends included (S9.1) — over the final [entry..exit] window.
    /// <paramref name="onlyTrackId"/> (incremental per-import path) restricts to that one track;
    /// null = whole-segment (analyze-all, byte-identical).
    /// </summary>
    Task PopulateAggregatesAsync(AnalysisScope scope, int? onlyTrackId = null, CancellationToken ct = default);
}
