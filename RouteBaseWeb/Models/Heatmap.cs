using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RouteBaseWeb.Models;

/// <summary>
/// Metadata for a rendered activity heatmap — an optional attachment on a "shapes" segment,
/// painted from that segment's efforts. The actual imagery lives in one packed <c>.htiles</c> file per
/// heatmap on the filesystem (HeatmapTilePackStore); this row tracks generation status, zoom range,
/// normalization max and geographic bounds.
/// </summary>
[Table("Heatmap")]
public class Heatmap
{
    [Key]
    [Column("heatmap_ID")]
    public int HeatmapId { get; set; }

    [Column("segment_ID")]
    public SegmentId SegmentId { get; set; }

    /// <summary>'pending' | 'running' | 'done' | 'stale' | 'failed' — 'stale' means tiles are still
    /// servable but were painted from effort spans a later analysis re-shaped (SD-01).</summary>
    [Column("status")]
    [MaxLength(20)]
    public string Status { get; set; } = HeatmapStatuses.Pending;

    [Column("error")]
    public string? Error { get; set; }

    [Column("min_zoom")]
    public int? MinZoom { get; set; }

    [Column("max_zoom")]
    public int? MaxZoom { get; set; }

    /// <summary>Global post-blur count maximum used for log normalization across all tiles.</summary>
    [Column("count_max")]
    public double? CountMax { get; set; }

    [Column("bounds_south")]
    public double? BoundsSouth { get; set; }

    [Column("bounds_west")]
    public double? BoundsWest { get; set; }

    [Column("bounds_north")]
    public double? BoundsNorth { get; set; }

    [Column("bounds_east")]
    public double? BoundsEast { get; set; }

    /// <summary>Comma-separated track ids resolved at generation time (for phase-2 replay).</summary>
    [Column("activity_ids")]
    public string? ActivityIds { get; set; }

    /// <summary>Comma-separated activity-type filter used at generation time (e.g. "Run,Ride").</summary>
    [Column("activity_types")]
    public string? ActivityTypes { get; set; }

    [Column("generated_at")]
    public DateTime? GeneratedAt { get; set; }

    [ForeignKey("SegmentId")]
    public virtual Segment? Segment { get; set; }
}
