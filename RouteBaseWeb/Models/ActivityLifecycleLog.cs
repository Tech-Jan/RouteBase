using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RouteBaseWeb.Models;

/// <summary>
/// Append-only record of every lifecycle transition — the answer to "why did my ride vanish?".
///
/// Written by <see cref="Services.Lifecycle.ActivityLifecycleService"/> — plus the three guarded raw-SQL
/// transitions (see <c>Services/Sql/ActivityLifecycleSql</c>), which pair their UPDATE with this insert — in
/// the same transaction as the column change it describes, so a state can never move without leaving a trace.
/// Rows die with their activity
/// (FK cascade, registered in ChildTableRegistry): the log explains the state of activities that EXIST — a
/// permanent purge is an explicit, confirmed act, and there is nothing left for the log to explain.
/// </summary>
[Table("Activity_Lifecycle_Log")]
public class ActivityLifecycleLog
{
    [Key]
    [Column("log_id")]
    public int LogId { get; set; }

    [Column("track_id")]
    public TrackId TrackId { get; set; }

    /// <summary>Who caused it. Null for the background paths that have no HTTP user (the finalize adjudicator).</summary>
    [Column("user_id")]
    public string? UserId { get; set; }

    /// <summary>Null only for the very first row (an activity coming into existence has no prior state).</summary>
    [Column("from_state")]
    [MaxLength(20)]
    public string? FromState { get; set; }

    [Column("to_state")]
    [MaxLength(20)]
    public string ToState { get; set; } = "";

    [Column("reason")]
    [MaxLength(20)]
    public string? Reason { get; set; }

    /// <summary>The edit that caused it, when one did (a merge trashing its sources). FK → Track_Edit_Commit
    /// ON DELETE SET NULL, declared in the EF model (TrackEditCommit is an entity) and mirrored in
    /// DbSchemaManager for legacy DBs.</summary>
    [Column("commit_id")]
    public CommitId? CommitId { get; set; }

    [Column("at")]
    public DateTime At { get; set; }
}
