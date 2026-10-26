using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RouteBaseWeb.Models;

/// <summary>
/// One edit session: a ride CHECKED OUT into the editor. The working state is the op script — the session is
/// what is open, not what it will produce (the revision is born at check-in; see TrackEditSaveService).
///
/// An EF ENTITY for its SHAPE only, exactly like <see cref="TrackEditCommit"/> — no DbSet;
/// <see cref="Services.TrackEdit.EditSessionStore"/> still owns every read and write through raw ADO. The
/// reason is schema authorship: EF authors the DDL for the entities in its model and knows nothing about the
/// raw-DDL tables, so an EF table can only declare a foreign key to another EF entity. <c>Activity
/// .in_edit_session_id</c> — the checkout pointer, and the lock — is such an FK, and could not exist while
/// this table was raw DDL. That FK's <c>ON DELETE SET NULL</c> is also what makes abort one statement:
/// deleting the session row releases the ride.
///
/// DDL parity: DbSchemaManager.EnsureTrackEditTablesAsync (legacy DBs).
/// </summary>
[Table("Activity_Edit_Session")]
public class ActivityEditSession
{
    [Key]
    [Column("session_id")]
    public string SessionId { get; set; } = "";

    [Column("user_id")]
    public string UserId { get; set; } = "";

    /// <summary>active | saved. A discarded session is DELETED, not flagged — the abort releases the lock.</summary>
    [Column("status")]
    public string Status { get; set; } = ActivityEditSessionStatuses.Active;

    /// <summary>The edit script, serialized with the shared TrackEditJson.Options (invariant 8).</summary>
    [Column("script_json")]
    public string ScriptJson { get; set; } = "";

    [Column("cursor")]
    public int Cursor { get; set; }

    [Column("stamps_json")]
    public string StampsJson { get; set; } = "[]";

    /// <summary>Effective edits behind the cursor (the resume banner's count), re-derived on every write.</summary>
    [Column("edit_count")]
    public int EditCount { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
