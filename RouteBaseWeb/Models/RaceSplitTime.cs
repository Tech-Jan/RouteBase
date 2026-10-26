using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace RouteBaseWeb.Models;

/// <summary>
/// A participant's elapsed time at a checkpoint. The anchor the pacing model normalizes to: whatever
/// the model does inside an interval, it hits these times exactly.
///
/// NOT named <c>Split</c> — <see cref="Split"/> is already the per-km pace-analysis DTO, and two things
/// called Split would break search for both (grep-ability rule).
///
/// For simulated participants these are IMPORTED (from the results CSV). For real participants they are
/// DERIVED from the projected chainage crossing each checkpoint, so a real rider ranks and gaps against
/// the field without its geometry being touched.
/// </summary>
[Table("Race_Split_Time")]
public class RaceSplitTime
{
    [Column("participant_ID")]
    public ParticipantId ParticipantId { get; set; }

    [Column("checkpoint_ID")]
    public CheckpointId CheckpointId { get; set; }

    /// <summary>Seconds from the race start (chainage-0 checkpoint) to this checkpoint.</summary>
    [Column("elapsed_seconds")]
    public double ElapsedSeconds { get; set; }

    [JsonIgnore]
    public virtual RaceParticipant? Participant { get; set; }
    [JsonIgnore]
    public virtual RaceCheckpoint? Checkpoint { get; set; }
}
