using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using RouteBaseWeb.Services.Lifecycle;

namespace RouteBaseWeb.Models;

[Table("Activity")]
public class Activity
{
    [Key]
    [Column("track_ID")]
    public TrackId TrackId { get; set; }

    [Column("api_ID")]
    public long? ApiId { get; set; }

    [Column("track_name")]
    [MaxLength(255)]
    public string? TrackName { get; set; }

    [Column("activity_type")]
    [MaxLength(100)]
    public string? ActivityType { get; set; }

    [Column("start_datetime_loc")]
    public DateTime? StartDateTimeLocal { get; set; }

    [Column("start_datetime_utc")]
    public DateTime? StartDateTimeUtc { get; set; }

    [Column("datetime_timezone")]
    public float? DateTimeTimezone { get; set; }

    [Column("start_gps_lat")]
    public double? StartGpsLat { get; set; }

    [Column("end_gps_lat")]
    public double? EndGpsLat { get; set; }

    [Column("start_gps_long")]
    public double? StartGpsLong { get; set; }

    [Column("end_gps_long")]
    public double? EndGpsLong { get; set; }

    [Column("time_total")]
    public double? TimeTotal { get; set; }

    [Column("time_elapsed")]
    public double? TimeElapsed { get; set; }

    [Column("time_moving")]
    public double? TimeMoving { get; set; }

    [Column("total_distance")]
    public double? TotalDistance { get; set; }

    [Column("total_ascent")]
    public double? TotalAscent { get; set; }

    [Column("total_descent")]
    public double? TotalDescent { get; set; }

    [Column("speed_avg")]
    public double? SpeedAvg { get; set; }

    [Column("speed_max")]
    public double? SpeedMax { get; set; }

    [Column("heart_rate_avg")]
    public double? HeartRateAvg { get; set; }

    [Column("heart_rate_max")]
    public double? HeartRateMax { get; set; }

    [Column("cadence_avg")]
    public double? CadenceAvg { get; set; }

    [Column("cadence_max")]
    public double? CadenceMax { get; set; }

    [Column("power_avg")]
    public double? PowerAvg { get; set; }

    [Column("power_max")]
    public double? PowerMax { get; set; }

    // Legacy columns from the pre-web schema — no feature reads or writes these today.
    // Kept so fresh and legacy DBs stay identical; if a comments feature lands, these are its
    // data seam. ME-05. (The sibling legacy Pictures table was replaced by the Photos feature.)
    [Column("power_20min")]
    public double? Power20Min { get; set; }

    [Column("friends")]
    [MaxLength(500)]
    public string? Friends { get; set; }

    [Column("comments")]
    public string? Comments { get; set; }

    [Column("calories")]
    public double? Calories { get; set; }

    [Column("source")]
    [MaxLength(50)]
    public string? Source { get; set; }

    [Column("multi_day_ID")]
    public int? MultiDayId { get; set; }

    [Column("user_id")]
    public string? UserId { get; set; }

    [Column("visibility")]
    [MaxLength(20)]
    public string Visibility { get; set; } = Visibilities.Default;

    // Lifecycle axis (ActivityLifecycle Batch 2) — 'in-work' | 'released' | 'trashed'. THE gate: visibility,
    // stats, maps and segments all funnel through VisibilityService.VisibleActivities, whose single clause is
    // Lifecycle == 'released'. Replaces review_state, which carried three unrelated concepts (review status,
    // the auto-recording trash, and edit-hiding) in one column. Written by ActivityLifecycleService — plus
    // exactly three guarded raw-SQL transitions whose WHERE clause IS the state guard (merge-consume /
    // merge-restore in ActivityEditSql, adjudicator discard in ActivityRecordingSql); each pairs its UPDATE
    // with the ActivityLifecycleSql log insert so the audit trail stays complete.
    [Column("lifecycle")]
    [MaxLength(20)]
    public string Lifecycle { get; set; } = LifecycleStates.Released;

    // WHY it is in that state. Never gates behaviour (invariant 2) — retention policy and UI copy only.
    [Column("lifecycle_reason")]
    [MaxLength(20)]
    public string? LifecycleReason { get; set; }

    // Where restore returns it to: a transition is from→to, so trashing records where it came from. A rejected
    // auto ride restores to in-work; a deleted one to released — without this the restore target is a guess.
    [Column("lifecycle_prev")]
    [MaxLength(20)]
    public string? LifecyclePrev { get; set; }

    [Column("lifecycle_at")]
    public DateTime? LifecycleAt { get; set; }

    // Merged into this activity (a replace-save spliced this ride's points into that one). Cleared on restore.
    // Self-FK with ON DELETE SET NULL: purging the merge PRODUCT un-supersedes its sources — they are no longer
    // merged into anything — rather than leaving them pointing at a row that is gone.
    [Column("superseded_by")]
    public TrackId? SupersededBy { get; set; }

    // Track editor (CommitPath D-A2-6). NULL = never edited; set to the newest non-reverted Track_Edit_Commit
    // that replaced/created this activity, retargeted (or nulled) on revert. Nullable → no HasDefaultValue.
    [Column("edited_commit_id")]
    public CommitId? EditedCommitId { get; set; }

    // Content axis (ActivityLifecycle): which commit consumed this activity — a replace-save spliced its
    // points into another track, so it is hidden until that commit is reverted. NULL = live. Replaces
    // Track_Edit_Commit.trashed_ids (a JSON id list that could neither be joined nor kept honest).
    // The track's CURRENT points are not pointed at from here: they are the Activity_Version row flagged
    // is_head, so a dangling head cannot exist.
    [Column("consumed_commit_id")]
    public CommitId? ConsumedCommitId { get; set; }

    // CHECKED OUT: the open edit session this ride is loaded into (slot 0), or NULL when it is free. This
    // column IS the lock — one open edit per ride, structurally, claimed by a guarded UPDATE ... WHERE
    // in_edit_session_id IS NULL (EditSessionService.CreateAsync). It is also the resume pointer and the
    // "being edited" badge. FK ON DELETE SET NULL: deleting the session row (abort) releases the ride.
    [Column("in_edit_session_id")]
    public string? InEditSessionId { get; set; }

    // Navigation properties
    [ForeignKey("UserId")]
    public virtual AppUser? User { get; set; }

    [ForeignKey("MultiDayId")]
    public virtual MultiDayTour? MultiDayTour { get; set; }

    // Photos assigned to this activity (Photos feature). Collection navigation over the existing
    // Photos.track_id FK — metadata only, no schema change. Enables the grid's "photos" count column
    // to sort/filter via an indexed COUNT subquery without denormalising a counter onto this table.
    public virtual ICollection<Photo> Photos { get; set; } = new List<Photo>();
}
