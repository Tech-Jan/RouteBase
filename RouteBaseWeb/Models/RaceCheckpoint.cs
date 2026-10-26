using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace RouteBaseWeb.Models;

/// <summary>
/// A named point on the course at a chainage (metres along the reference polyline).
///
/// Checkpoints are the PRIMITIVE the pacing model works in, not an add-on: it always normalizes
/// per checkpoint interval, so a finish-time-only race is the one-interval case, a splits race the
/// N-interval case, and a multi-day stage race N intervals (overnight gaps never enter the race
/// clock — the per-leg import rule replaced the dropped clock_stops flag). There is no special
/// path for any of them.
///
/// Start (chainage 0) and Finish (chainage = course length) are ordinary rows — not flags, not columns.
/// </summary>
[Table("Race_Checkpoint")]
public class RaceCheckpoint
{
    [Key]
    [Column("checkpoint_ID")]
    public CheckpointId CheckpointId { get; set; }

    [Column("race_ID")]
    public RaceId RaceId { get; set; }

    /// <summary>Order along the course. Unique per race; must agree with <see cref="ChainageMeters"/> order.</summary>
    [Column("seq")]
    public int Seq { get; set; }

    [Column("checkpoint_name")]
    [MaxLength(255)]
    public string? CheckpointName { get; set; }

    /// <summary>Distance along the course reference polyline, in metres.</summary>
    [Column("chainage_m")]
    public double ChainageMeters { get; set; }

    /// <summary>
    /// Name of the interval that FOLLOWS this checkpoint — a relay/stage leg ("Road Bike", "Canoe").
    /// A leg IS the span between two consecutive checkpoints, so it needs no entity of its own. Null on
    /// the Finish row, which has no following interval.
    /// </summary>
    [Column("leg_name")]
    [MaxLength(255)]
    public string? LegName { get; set; }

    /// <summary>
    /// Colour the following interval is drawn in (course line + profile). See <see cref="LegName"/>.
    /// A validated `#rrggbb` string — it reaches a MapLibre <c>['get','color']</c> expression and a CSS
    /// swatch, so it is never free text (<see cref="Services.Races.RaceColors"/>).
    /// </summary>
    [Column("leg_color")]
    [MaxLength(32)]
    public string? LegColor { get; set; }

    /// <summary>
    /// Back-reference. [JsonIgnore] is load-bearing, not tidiness: EF's relationship fix-up populates
    /// this whenever the parent is tracked, so Race → Checkpoints → Race is a cycle System.Text.Json
    /// cannot serialize. Anything that ever hands an entity to a serializer would 500 without it.
    /// </summary>
    [JsonIgnore]
    public virtual Race? Race { get; set; }
}
