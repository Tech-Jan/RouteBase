using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RouteBaseWeb.Models;

/// <summary>
/// A race: one course + a field of participants (real activities and/or simulated finishers).
///
/// The course is an <see cref="Activity"/> — its head track version supplies the geometry. It is NOT a
/// Segment: segments exist to MATCH tracks, a course exists to REPLAY a field, and the two carry
/// different invariants (a shapes-kind segment isn't even guaranteed to be one ordered line). The FK is
/// also the only one available: Track_Object is raw-ADO with no EF entity, and an EF entity may only FK
/// another EF entity.
///
/// Multi-day / merged courses need no special support here — the track editor already stitches
/// activities (AppendOp + TimeMode) into one new Activity, so a merged course is just another
/// <see cref="CourseTrackId"/>.
/// </summary>
[Table("Race")]
public class Race
{
    [Key]
    [Column("race_ID")]
    public RaceId RaceId { get; set; }

    [Column("race_name")]
    [MaxLength(255)]
    public string? RaceName { get; set; }

    [Column("race_date")]
    public DateTime? RaceDate { get; set; }

    /// <summary>
    /// The activity whose track is the course. Cascades: a race without a course is meaningless.
    /// Column is <c>track_id</c>, not <c>course_track_id</c>, on purpose — that is the codebase's spelling
    /// for "the Activity this row hangs off" (Photos, RecordingSession, Activity_Version), and
    /// ChildTableRegistryParityTests only recognises a table as track-keyed by that exact name. A prettier
    /// column name here would make the race invisible to the delete-seam rot guard.
    /// </summary>
    [Column("track_id")]
    public TrackId CourseTrackId { get; set; }

    /// <summary>
    /// The real effort whose terrain response fits this race's grade→cost curve. Null → fall back to
    /// <see cref="CourseTrackId"/>, which is the old behaviour and still the common case.
    ///
    /// It exists because a course need not carry usable timing: a merged or drawn route has no
    /// timestamps, so fitting from the course yields nothing and the field silently inherits a DEFAULT
    /// curve — a mountain-bike one. That paces a relay's ski, canoe and kayak legs with bike physics.
    /// Naming any real activity over the course fixes it without inventing a "sport" enum nobody can
    /// map their race onto.
    ///
    /// SET NULL on delete, NOT cascade: purging the reference ride must leave the race replayable (it
    /// falls back to the course). Contrast <see cref="CourseTrackId"/>, which cascades — the course IS
    /// the race.
    /// </summary>
    [Column("reference_track_id")]
    public TrackId? ReferenceTrackId { get; set; }

    [Column("user_id")]
    public string? UserId { get; set; }

    [Column("visibility")]
    [MaxLength(20)]
    public string Visibility { get; set; } = Visibilities.Default;

    /// <summary>
    /// Who may EDIT this race — a separate axis from <see cref="Visibility"/> (who may view). One of
    /// <c>owner</c> | <c>friends</c> | <c>public</c>; default <c>owner</c>, so a race stays owner-only
    /// until its owner opens it up. Edit never exceeds view: a collaborator must also be able to see the
    /// race (enforced in <see cref="Services.Races.RaceAccess.EditableRaceAsync"/>).
    /// </summary>
    [Column("edit_policy")]
    [MaxLength(20)]
    public string EditPolicy { get; set; } = "owner";

    [Column("notes")]
    public string? Notes { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    public virtual ICollection<RaceCheckpoint> Checkpoints { get; set; } = new List<RaceCheckpoint>();
    public virtual ICollection<RaceParticipant> Participants { get; set; } = new List<RaceParticipant>();
}
