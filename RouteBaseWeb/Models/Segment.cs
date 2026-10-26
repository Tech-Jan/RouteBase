using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace RouteBaseWeb.Models;

[Table("Segment")]
public class Segment
{
    [Key]
    [Column("segment_ID")]
    public SegmentId SegmentId { get; set; }

    [Column("segment_name")]
    [MaxLength(255)]
    public string? SegmentName { get; set; }

    [Column("segment_distance")]
    public double? SegmentDistance { get; set; }

    [Column("segment_ascent")]
    public double? SegmentAscent { get; set; }

    [Column("segment_descent")]
    public double? SegmentDescent { get; set; }

    [Column("activity_type")]
    [MaxLength(100)]
    public string? ActivityType { get; set; }

    /// <summary>
    /// How the segment is defined and matched:
    ///   "polyline" (default) — a drawn path in <see cref="GpsData"/>, matched by the 6-stage pipeline.
    ///   "shapes"             — rule document in <see cref="MatchRulesJson"/>, matched by ShapeSegmentMatcher.
    ///                          May carry an optional <see cref="Heatmap"/> attachment rendered from its efforts.
    /// </summary>
    [Column("segment_kind")]
    [MaxLength(20)]
    public string SegmentKind { get; set; } = SegmentKinds.Polyline;

    /// <summary>
    /// For shape-defined segments only: the versioned JSON rule document (shapes + extent +
    /// membership rules) that fully defines the segment. Parsed/validated by
    /// <see cref="Services.Segments.Rules.RuleDocument"/>; stored verbatim. Null for polyline
    /// segments and for pre-rework shape segments (those are skipped by the matcher).
    /// </summary>
    [Column("match_rules_json")]
    public string? MatchRulesJson { get; set; }

    /// <summary>
    /// For shape-defined segments only: the comma-separated owner user-ids whose visible tracks this
    /// segment matches against. Null (the default for every legacy segment) means "the owner's own tracks
    /// only" — the historical behaviour. When set, the matcher re-authorises these ids against the current
    /// visibility rules on every run, so a revoked friendship / changed visibility drops those tracks even
    /// though the stored list is unchanged. Ignored for polyline segments.
    /// </summary>
    [Column("match_owner_ids")]
    public string? MatchOwnerIds { get; set; }

    /// <summary>
    /// When the segment was created (UTC). Null for legacy segments created before this column existed —
    /// they have no recorded timestamp (SegmentId order is the only creation-order signal). Drives the
    /// "Created" column + its date-range filter on the Segments index grid.
    /// </summary>
    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("user_id")]
    public string? UserId { get; set; }

    [Column("visibility")]
    [MaxLength(20)]
    public string Visibility { get; set; } = Visibilities.Default;

    // Navigation properties
    [ForeignKey("UserId")]
    public virtual AppUser? User { get; set; }
    public virtual ICollection<SegmentGps> GpsData { get; set; } = new List<SegmentGps>();
}
