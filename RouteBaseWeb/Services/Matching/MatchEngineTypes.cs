using RouteBaseWeb.Models;
namespace RouteBaseWeb.Services.Matching;

/// <summary>
/// Per-segment geometry + spatial index, precomputed once per analysis run. Pure data —
/// no DbContext, no I/O. Built by <see cref="SegmentGeometry.Create"/> (see SegmentGeometry.cs).
///
/// All zone math is in seg_GPS_ID space (not ordinals), matching Stage 2 of the SQL pipeline
/// (SegmentService.Pipeline.cs:95-126) bug-for-bug.
/// </summary>
public sealed partial class SegmentGeometry
{
    public required SegmentId SegmentId { get; init; }

    /// <summary>Segment point seg_GPS_IDs, ascending. Ordinal j ↔ SegGpsIds[j].</summary>
    public required int[] SegGpsIds { get; init; }
    public required double[] Lat { get; init; }
    public required double[] Lng { get; init; }

    /// <summary>COUNT(*) of segment points (≡ COUNT(DISTINCT seg_GPS_ID), PK).</summary>
    public required int TotalPoints { get; init; }

    public required long SegStartId { get; init; }   // MIN(seg_GPS_ID)
    public required long SegEndId { get; init; }      // MAX(seg_GPS_ID)
    public required long StartZoneEnd { get; init; }
    public required long EndZoneStart { get; init; }

    /// <summary>MAX(300, MIN(TotalPoints*10, 1500)) — GPS_ID-unit gap threshold (S5.1).</summary>
    public required int GapThreshold { get; init; }

    /// <summary>Packed cell → seg-point ordinals whose 9-neighbour set contains that cell.
    /// A GPS point's Pack(lat,lng) probes this to get its candidate segment points (S3.1).</summary>
    public required Dictionary<long, List<int>> CellIndex { get; init; }
}

/// <summary>
/// One qualified crossing window's outputs — the pre-gate row shape that Stage 6 produces
/// and the INSERT gate consumes (SegmentService.Pipeline.cs:276-375). GPS_ID addressing is
/// preserved (project invariant 6): entry/exit are raw GPS_ID values.
/// </summary>
public sealed record QualifiedCrossing(
    int TrackId,
    int SegmentId,
    int GpsIdEntry,
    int GpsIdExit,
    int MatchedSegPoints,
    int TotalPoints,
    int MatchPerc,
    double EffortDistM,
    int StartZoneHits,
    int EndZoneHits);

// ---------------------------------------------------------------------------
// EffortMathCore I/O records (TE-15). Pure inputs/outputs — no DbContext.
// ---------------------------------------------------------------------------

/// <summary>Offsets input: one own effort's guessed GPS_ID window (S8.1). Entry/exit are raw
/// GPS_ID values (project invariant 6).</summary>
public sealed record EffortWindow(int GpsInSegmentId, int TrackId, int EntryGuessed, int ExitGuessed);

/// <summary>Offsets output: the calculated entry/exit GPS_IDs and their Round5 pin offsets.
/// A winner sets both offsets to finite band-d values; the fallback sets calc = guessed and an
/// offset is null when the exact guessed GPS_ID is missing or has NaN coords (S8.10–S8.12).</summary>
public sealed record OffsetResult(int GpsInSegmentId, int EntryCalc, double? OffsetEntry, int ExitCalc, double? OffsetExit);

/// <summary>Aggregates input: one effort's normalized window (any owner). Lo/Hi are the
/// swap-normalized GPS_ID bounds `MIN/MAX(COALESCE(calc,guess) entry, exit)` from S9.1 —
/// the caller guarantees Lo ≤ Hi.</summary>
public sealed record AggregateWindow(int GpsInSegmentId, int TrackId, int Lo, int Hi);

/// <summary>Aggregates output: the 7 effort_* columns. Distance/elevation/time/speed are
/// COALESCE-0 (never null once a row is produced); hr/power/cadence are null when the window has
/// no qualifying samples (S9.3–S9.11).</summary>
public sealed record AggregateResult(
    int GpsInSegmentId,
    double Dist,
    double Elev,
    double TimeS,
    double SpeedAvg,
    double? HrAvg,
    double? PowerAvg,
    double? CadenceAvg);
