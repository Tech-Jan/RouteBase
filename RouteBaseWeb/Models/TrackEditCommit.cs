using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RouteBaseWeb.Models;

/// <summary>
/// One save-commit: the SCRIPT of an edit — its explanation, not its effect (what it DID is derived from the
/// rows carrying its id; see <see cref="Services.TrackEdit.TrackEditCommitStore"/>).
///
/// An EF ENTITY, deliberately, even though <see cref="Services.TrackEdit.TrackEditCommitStore"/> still owns
/// every read and write through raw ADO. The reason is schema authorship, not data access: EF authors the DDL
/// for the entities in its model and knows nothing about the raw-DDL tables, so an EF table can only declare a
/// foreign key to another EF entity. This commit is referenced by three of them — Activity.edited_commit_id,
/// Activity.consumed_commit_id, Activity_Lifecycle_Log.commit_id — and by one raw table
/// (Activity_Version.commit_id, which gets its FK for free precisely because it is raw). While this table was
/// classified as storage ("Dem_Tiles precedent"), those three FKs could not exist and their integrity had to be
/// hand-held by a trigger. It is not storage: Dem_Tiles is a relation-free tile cache, whereas a commit has
/// four inbound references and a state of its own (reverted_at). It is a domain entity, and it is modelled as
/// one so the database can enforce what a trigger was standing in for.
///
/// No DbSet: EF owns the SHAPE, the store owns the DATA. Exposing one would invite tracked writes that bypass
/// the store, which is the single writer.
/// </summary>
[Table("Track_Edit_Commit")]
public class TrackEditCommit
{
    [Key]
    [Column("commit_id")]
    public CommitId CommitId { get; set; }

    [Column("user_id")]
    public string UserId { get; set; } = "";

    [Column("committed_at")]
    public DateTime CommittedAt { get; set; }

    [Column("save_mode")]
    public string SaveMode { get; set; } = "";

    /// <summary>Provenance only — which edit session produced this commit. Deliberately NO FK to
    /// Activity_Edit_Session: nothing joins or resolves it, and a commit (an audit row, decision F)
    /// must outlive any future session cleanup without cascade or SET NULL ceremony.</summary>
    [Column("session_id")]
    public string? SessionId { get; set; }

    /// <summary>The edit script, serialized with the shared TrackEditJson.Options (invariant 8).</summary>
    [Column("script_json")]
    public string ScriptJson { get; set; } = "";

    [Column("reverted_at")]
    public DateTime? RevertedAt { get; set; }
}
