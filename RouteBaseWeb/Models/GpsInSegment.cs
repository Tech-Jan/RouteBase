using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RouteBaseWeb.Models;

[Table("GPSinSegment")]
public class GpsInSegment
{
    [Key]
    [Column("GPSinSegment_ID")]
    public GpsInSegmentId GpsInSegmentId { get; set; }

    [Column("track_id")]
    public TrackId TrackId { get; set; }

    [Column("segment_id")]
    public SegmentId SegmentId { get; set; }

    [Column("seg_gps_count")]
    public int? SegGpsCount { get; set; }

    [Column("seg_total_count")]
    public int? SegTotalCount { get; set; }

    [Column("seg_perc")]
    public int? SegPerc { get; set; }

    [Column("gps_id_entry_guessed")]
    public int? GpsIdEntryGuessed { get; set; }

    [Column("gps_id_exit_guessed")]
    public int? GpsIdExitGuessed { get; set; }

    [Column("gps_id_count")]
    public int? GpsIdCount { get; set; }

    [Column("Offset_entry")]
    public double? OffsetEntry { get; set; }

    [Column("gps_id_entry_calculated")]
    public int? GpsIdEntryCalculated { get; set; }

    [Column("Offset_exit")]
    public double? OffsetExit { get; set; }

    [Column("gps_id_exit_calculated")]
    public int? GpsIdExitCalculated { get; set; }

    // Precomputed effort aggregates over the final entry→exit window — filled by the effort-math step
    // of analysis. NULL means "not yet computed" (legacy / pre-analysis); the totals read path falls
    // back to scanning GPS for those.

    /// <summary>Effort distance in metres (cumulative-distance span over the window).</summary>
    [Column("effort_distance")]
    public double? EffortDistance { get; set; }

    /// <summary>Effort elevation gain in metres (sum of positive altitude deltas).</summary>
    [Column("effort_elevation")]
    public double? EffortElevation { get; set; }

    /// <summary>Effort elapsed time in seconds (last − first GPS timestamp).</summary>
    [Column("effort_time")]
    public double? EffortTime { get; set; }

    /// <summary>Avg speed in m/s over points with a speed sample.</summary>
    [Column("effort_speed_avg")]
    public double? EffortSpeedAvg { get; set; }

    /// <summary>Avg heart rate over points with HR &gt; 0; NULL when the effort has none.</summary>
    [Column("effort_hr_avg")]
    public double? EffortHrAvg { get; set; }

    /// <summary>Avg power over points with a power sample; NULL when the effort has none.</summary>
    [Column("effort_power_avg")]
    public double? EffortPowerAvg { get; set; }

    /// <summary>Avg cadence over power-bearing points; NULL without power.</summary>
    [Column("effort_cadence_avg")]
    public double? EffortCadenceAvg { get; set; }

    // Navigation properties
    [ForeignKey("TrackId")]
    public virtual Activity? Activity { get; set; }

    [ForeignKey("SegmentId")]
    public virtual Segment? Segment { get; set; }

    /// <summary>
    /// Preferred GPS window for this effort: Calculated span if present, else Guessed.
    /// Returns (null, null) when neither bound is set; orders entry ≤ exit.
    /// </summary>
    public (int? entry, int? exit) ResolveGpsRange()
    {
        int? entry = GpsIdEntryCalculated ?? GpsIdEntryGuessed;
        int? exit  = GpsIdExitCalculated  ?? GpsIdExitGuessed;
        if (!entry.HasValue || !exit.HasValue) return (null, null);
        if (entry.Value > exit.Value) (entry, exit) = (exit, entry);
        return (entry, exit);
    }

    /// <summary>
    /// The stable entry id that keys a <see cref="SegmentEffortExclusion"/> to this effort — the raw
    /// preferred entry (Calculated ?? Guessed), NOT the min/max-normalized <see cref="ResolveGpsRange"/>
    /// value, so the identity survives deterministic re-analysis. -1 when neither bound is set.
    /// </summary>
    public int ExclusionEntryIndex => GpsIdEntryCalculated ?? GpsIdEntryGuessed ?? -1;
}
