using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RouteBaseWeb.Models;

/// <summary>
/// A durable "this effort is hidden" marker for the segment-stats page (item 25). Analysis wipes and
/// rebuilds GPSinSegment on every re-analyze, so a removal can't live as a column there — it lives here
/// and is applied as a read-time anti-join. Identity is <c>(segment_id, track_id, entry_index)</c> with
/// <c>entry_index = GpsIdEntryCalculated ?? GpsIdEntryGuessed</c> (see <see cref="GpsInSegment.ExclusionEntryIndex"/>),
/// stable across deterministic re-analysis. Restore = delete the row. No FK cascade (legacy no-cascade
/// convention); rows are purged explicitly at the segment/track/reset delete seams.
/// </summary>
[Table("SegmentEffortExclusion")]
public class SegmentEffortExclusion
{
    [Key]
    [Column("exclusion_id")]
    public int ExclusionId { get; set; }

    [Column("segment_id")]
    public SegmentId SegmentId { get; set; }

    [Column("track_id")]
    public TrackId TrackId { get; set; }

    /// <summary>The effort's entry GPS id at exclusion time (GpsIdEntryCalculated ?? GpsIdEntryGuessed).</summary>
    [Column("entry_index")]
    public int EntryIndex { get; set; }

    /// <summary>ISO-8601 UTC timestamp the exclusion was created (TEXT, like the other app-era tables).</summary>
    [Column("excluded_at")]
    public string ExcludedAt { get; set; } = string.Empty;
}
