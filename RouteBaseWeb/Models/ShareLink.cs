using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RouteBaseWeb.Models;

/// <summary>Which resource kind a <see cref="ShareLink"/> grants. Stored as the string values in
/// <see cref="ShareKindNames"/> so the column is greppable, never an int nobody can read in a DB dump.</summary>
public enum ShareKind
{
    Activity,
    Segment,

    /// <summary>The live (or just-finished) recording behind an Activity — a map-only public view of an
    /// in-progress ride. Keyed by <c>track_id</c> like <see cref="Activity"/> (a session is 1:1 with its
    /// Activity shell), but resolves to the RecordingSession and serves only the live map, never the
    /// activity detail page.</summary>
    LiveRecording,

    /// <summary>A race replay — the course, the simulated field and the owner's own ride, watch-only.
    /// Keyed by its OWN <c>race_id</c>, unlike <see cref="LiveRecording"/>: several races can share one
    /// course Activity, so a race is not 1:1 with a track and <c>track_id</c> could not name it.</summary>
    Race,
}

/// <summary>The on-disk spellings of <see cref="ShareKind"/>. One place, so a typo is a compile error.</summary>
public static class ShareKindNames
{
    public const string Activity = "activity";
    public const string Segment = "segment";
    public const string Live = "live";
    public const string Race = "race";
}

/// <summary>
/// One capability URL: an unguessable token granting anonymous, read-only, time-limited access to a single
/// activity or segment. The plaintext token is NEVER stored — only its SHA-256 (<see cref="TokenHash"/>),
/// exactly like <c>AppCredentials.ApiKey</c>. It is shown to the owner once, at mint, and is unrecoverable
/// afterwards; the management UI can therefore only list and revoke, never re-display.
///
/// THREE nullable FK columns rather than one polymorphic <c>resource_id</c>, for two reasons:
///   1. ChildTableRegistryParityTests recognises a table as track-/segment-/race-keyed only by the exact
///      column names <c>track_id</c>/<c>segment_id</c>/<c>race_id</c> (see Race.cs) — a polymorphic column
///      would make share links invisible to the delete-seam rot guard.
///   2. It buys the invariant for free: <c>ON DELETE CASCADE</c> on all three means purging a shared
///      activity, segment or race STRUCTURALLY revokes every link pointing at it. No service code to
///      remember, and no window in which a live token resolves to a resource that is gone.
///
/// <see cref="Kind"/> is redundant with which FK is set; it is kept as the explicit discriminator the
/// resolver reads. "Exactly one of TrackId/SegmentId/RaceId is set, matching Kind" is enforced in
/// ShareLinkService and pinned by a test rather than a SQL CHECK — EF's HasCheckConstraint emits text that
/// must match the hand DDL byte-for-byte or DbSchemaParityTests fails on an unreadable diff.
/// </summary>
[Table("Share_Link")]
public class ShareLink
{
    /// <summary>
    /// AUTOINCREMENT, not a plain INTEGER PRIMARY KEY: the expiry sweep DELETEs rows, and a bare rowid gets
    /// REUSED after a delete. This id is the grant's stable identity (it rides in the principal's token-id
    /// claim), so reuse across a delete + re-mint would let a stale claim name a different grant.
    /// </summary>
    [Key]
    [Column("share_link_ID")]
    public int ShareLinkId { get; set; }

    /// <summary>SHA-256 (lowercase hex) of the plaintext token. Uniquely indexed — this is the lookup key.</summary>
    [Column("token_hash")]
    [MaxLength(64)]
    public string TokenHash { get; set; } = "";

    /// <summary>One of <see cref="ShareKindNames"/>.</summary>
    [Column("kind")]
    [MaxLength(20)]
    public string Kind { get; set; } = ShareKindNames.Activity;

    /// <summary>Set iff <see cref="Kind"/> is <c>activity</c> or <c>live</c> (both key on the Activity's
    /// track id). Cascades — see the type remarks.</summary>
    [Column("track_id")]
    public TrackId? TrackId { get; set; }

    /// <summary>Set iff <see cref="Kind"/> is <c>segment</c>. Cascades — see the type remarks.</summary>
    [Column("segment_id")]
    public SegmentId? SegmentId { get; set; }

    /// <summary>
    /// Set iff <see cref="Kind"/> is <c>race</c>. Its own column rather than a reuse of
    /// <see cref="TrackId"/>: many races can be built over ONE course Activity, so a track id does not
    /// identify a race. Cascades — see the type remarks.
    /// </summary>
    [Column("race_id")]
    public RaceId? RaceId { get; set; }

    /// <summary>Who minted it. No FK, matching Race.user_id; registered in ChildTableRegistry.UserOwnedTables
    /// so a data reset wipes it.</summary>
    [Column("user_id")]
    public string UserId { get; set; } = "";

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Hard expiry. NOT NULL by design — there is no such thing as a permanent share link here, so a leaked
    /// URL always has a bounded lifetime. Chosen server-side from a fixed preset set (ShareTtl); a
    /// caller-supplied timestamp would make "expiry is mandatory" advisory.
    /// </summary>
    [Column("expires_at")]
    public DateTime ExpiresAt { get; set; }

    /// <summary>Soft revocation, so a revoked link stays listed ("revoked 3 days ago") until the sweep
    /// reclaims it. A nullable timestamp rather than a bool: revocation is auditable.</summary>
    [Column("revoked_at")]
    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// How many times the share PAGE has been opened with this token. Counted at the page, never at token
    /// resolution: authentication runs on every subresource request (the photo list, each of up to 200
    /// image fetches, a live share's poll loop), so counting there would be a write per request and a
    /// number that means nothing.
    ///
    /// It answers one question — "did this link escape?" — so it deliberately stores no viewer identity:
    /// no IP, no user agent, no per-open row. A count and <see cref="LastOpenedAt"/> are enough to notice a
    /// leak without turning share links into surveillance of the people they were shared with.
    ///
    /// Reloads count, and so do link unfurlers (paste into Slack and this ticks before a human looks) —
    /// which is the useful part: it says the URL was fetched by someone.
    /// </summary>
    [Column("open_count")]
    public int OpenCount { get; set; }

    /// <summary>When the page was last opened, or null if never. Pairs with <see cref="OpenCount"/>: the
    /// count says how loud, this says how recent — a dormant link that wakes up is the interesting case.</summary>
    [Column("last_opened_at")]
    public DateTime? LastOpenedAt { get; set; }
}
