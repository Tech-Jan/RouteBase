using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace RouteBaseWeb.Models;

[Table("Segment_GPS")]
public class SegmentGps
{
    [Key]
    [Column("seg_GPS_ID")]
    public int SegGpsId { get; set; }

    [Column("segment_ID")]
    public SegmentId SegmentId { get; set; }

    [Column("GPS_lat")]
    public double? GpsLat { get; set; }

    [Column("GPS_long")]
    public double? GpsLong { get; set; }

    [Column("altitude")]
    public double? Altitude { get; set; }

    // Navigation property
    [ForeignKey("SegmentId")]
    public virtual Segment? Segment { get; set; }
}

