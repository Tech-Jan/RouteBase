using System.Linq.Expressions;
using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services.Segments;

/// <summary>
/// The shared effort read-model pieces: the exclusion anti-join and the precomputed-aggregate
/// projection. Extracted from <see cref="SegmentEfforts"/> so the share path
/// (<c>Services/Sharing/ShareSegmentReader</c>) can build the same rows from a different, owner-scoped
/// source without duplicating the projection — two copies would drift, and the copy that drifted would
/// be the one nobody looks at.
///
/// Deliberately static and identity-free: nothing here decides WHO may see a row. The gate is the only
/// thing that differs between the authenticated and the share path, so the gate is the only thing left
/// outside.
/// </summary>
public static class SegmentEffortProjection
{
    /// <summary>
    /// Item 25 read-time anti-join: an effort is excluded when a SegmentEffortExclusion row matches its
    /// (segment_id, track_id, entry_index) identity (entry = Calculated ?? Guessed). Applied by every
    /// totals/counts/chart read consumer so a durable removal survives re-analysis. Translates to one
    /// indexed NOT EXISTS on the (segment_id, track_id) index; the hot analyze-all loop never touches it.
    ///
    /// Takes the context as a parameter rather than closing over a field — that capture is precisely what
    /// made this unshareable while it lived on the service.
    /// </summary>
    public static Expression<Func<GpsInSegment, bool>> NotExcluded(RouteBaseDbContext db) =>
        g => !db.SegmentEffortExclusions.Any(x =>
            x.SegmentId == g.SegmentId && x.TrackId == g.TrackId &&
            x.EntryIndex == (g.GpsIdEntryCalculated ?? g.GpsIdEntryGuessed));

    /// <summary>True when analysis stored the full per-effort aggregate set (post avg-columns).</summary>
    public static bool HasStoredAggregates(GpsInSegment row) =>
        row.EffortDistance.HasValue && row.EffortElevation.HasValue &&
        row.EffortTime.HasValue && row.EffortSpeedAvg.HasValue;

    /// <summary>
    /// Effort summary straight from the precomputed GPSinSegment columns (filled by the analysis
    /// engine — the single source of truth for the math).
    /// </summary>
    public static SegmentEffortDetailed FromAggregates(
        GpsInSegment row, RouteBaseWeb.Models.Activity activity, Segment? segment,
        int entry, int exit, string currentUserId, bool excluded)
    {
        double? avgHr = row.EffortHrAvg
            ?? (activity.HeartRateAvg.HasValue && activity.HeartRateAvg.Value > 0
                ? activity.HeartRateAvg : null);

        if (row is not { EffortDistance: double dist, EffortTime: double time,
                         EffortSpeedAvg: double speedAvg, EffortElevation: double elev })
            throw new InvalidOperationException("caller must gate on HasStoredAggregates");

        return new SegmentEffortDetailed
        {
            GpsInSegmentId   = row.GpsInSegmentId.Value,
            SegmentId        = row.SegmentId.Value,
            SegmentName      = segment?.SegmentName ?? "Unknown",
            ActivityId       = row.TrackId.Value,
            ActivityName     = activity.TrackName ?? "Unknown",
            ActivityDate     = activity.StartDateTimeLocal,
            ActivityType     = activity.ActivityType,
            MatchPercentage  = row.SegPerc ?? 0,
            Distance         = dist,
            ElapsedTime      = time,
            AverageSpeed     = speedAvg,
            AverageHeartRate = avgHr,
            AverageCadence   = row.EffortCadenceAvg,
            AveragePower     = row.EffortPowerAvg,
            Elevation        = elev,
            GpsIdEntry       = entry,
            GpsIdExit        = exit,
            OffsetEntry      = row.OffsetEntry,
            OffsetExit       = row.OffsetExit,
            UserId           = activity.UserId,
            UserDisplayName  = activity.User?.DisplayName ?? activity.UserId,
            IsOwnEffort      = activity.UserId == currentUserId,
            Excluded         = excluded
        };
    }
}
