using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services;

/// <summary>
/// The content + lifecycle + edit-table half of the schema author (ActivityLifecycle project): the change
/// axis (Track_Edit_Commit / Activity_Edit_Session), the content axis (Track_Object / Activity_Version /
/// Version_Parent) and the lifecycle axis (Activity_Lifecycle_Log).
/// Split from DbSchemaManager.cs by file-size rule only — same class, same rules;
/// the two-author rule and the phase ordering live in the main file's header.
/// </summary>
public partial class DbSchemaManager
{
    /// <inheritdoc />
    public async Task EnsureTrackContentTablesAsync()
    {
        // Activity_Version.commit_id is a real FK into Track_Edit_Commit, and SQLite resolves an FK's parent
        // table on INSERT — so the change axis must exist before the content axis can be written to. Declared
        // here rather than left to call order: both are idempotent, and a caller that wants versions always
        // wants them joinable to the commit that produced them.
        await EnsureTrackEditTablesAsync();

        // Content axis (ActivityLifecycle plan): CONTENT is immutable and content-addressed; a track's
        // current state is one of its versions, flagged is_head. Same TrackBlobCodec bytes as the old
        // Track_Blob — content-addressing wraps them, it does not reformat them.
        //
        // Track_Object   sha256 → bytes. Written once, never updated. Identical content stored once
        //                (re-import of the same GPX, or "restore v2", costs zero new bytes).
        // Activity_Version  append-only revision list. seq 1..N per track; never renumbered.
        //                is_head marks the CURRENT one (PLM's "current revision" flag). It lives here rather
        //                than as an Activity.head_version_id pointer so a dangling head is structurally
        //                impossible: the flag is a property of a row that exists by definition, it dies with
        //                its versions via the ON DELETE CASCADE, and the partial unique index below lets the
        //                DB — not us — enforce "exactly one head per track".
        // Version_Parent    lineage DAG: 1 parent = edit/split · N = merge · 0 = import. NOT derivable from
        //                seq: after a revert, the next edit's parent is the version the head moved BACK to,
        //                not seq-1 — and split/merge lineage crosses tracks, which seq cannot express.
        //                parent_version_id deliberately carries NO FK: purging an old version must leave the
        //                child's link dangling (that is how revert detects "nothing left to restore"), not
        //                cascade-delete the child.
        //
        // AUTOINCREMENT on version_id is LOAD-BEARING, not decoration. Because parent_version_id has no FK,
        // a purged version's id must never be handed out again: plain INTEGER PRIMARY KEY reuses the ids of
        // deleted rows (max(rowid)+1), so a new version could silently inherit a dangling parent link and a
        // merge would appear to descend from an unrelated ride. AUTOINCREMENT makes ids monotonic — a dangling
        // link stays dangling and readably so. Do not "simplify" it away.
        //
        // No EF entity — raw ADO (Dem_Tiles precedent, persistence ignorance). Rowid tables for the BLOB
        // payloads (WITHOUT ROWID would page-split them badly).
        await _context.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS Track_Object (
                sha256        TEXT PRIMARY KEY,
                codec_version INTEGER NOT NULL,
                point_count   INTEGER NOT NULL,
                byte_len      INTEGER NOT NULL,
                data          BLOB    NOT NULL,
                created_at    TEXT    NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Activity_Version (
                version_id  INTEGER PRIMARY KEY AUTOINCREMENT,
                track_id    INTEGER NOT NULL REFERENCES Activity(track_ID) ON DELETE CASCADE,
                seq         INTEGER NOT NULL,
                object_sha  TEXT    NOT NULL REFERENCES Track_Object(sha256),
                commit_id   INTEGER REFERENCES Track_Edit_Commit(commit_id) ON DELETE SET NULL,
                is_head     INTEGER NOT NULL DEFAULT 0,
                point_count INTEGER NOT NULL,
                distance    REAL,
                ascent      REAL,
                duration_s  REAL,
                created_at  TEXT    NOT NULL,
                UNIQUE (track_id, seq)
            );
            CREATE INDEX IF NOT EXISTS IX_ActivityVersion_Track  ON Activity_Version (track_id, seq);
            CREATE INDEX IF NOT EXISTS IX_ActivityVersion_Commit ON Activity_Version (commit_id);
            CREATE INDEX IF NOT EXISTS IX_ActivityVersion_Object ON Activity_Version (object_sha);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_ActivityVersion_Head
                ON Activity_Version (track_id) WHERE is_head = 1;

            CREATE TABLE IF NOT EXISTS Version_Parent (
                version_id        INTEGER NOT NULL REFERENCES Activity_Version(version_id) ON DELETE CASCADE,
                parent_version_id INTEGER NOT NULL,
                PRIMARY KEY (version_id, parent_version_id)
            ) WITHOUT ROWID;
            -- Reverse lineage ('which versions descend from this one?' — merge provenance, un-merge checks).
            -- The WITHOUT ROWID PK only orders by version_id, so without this the reverse walk is a full scan.
            CREATE INDEX IF NOT EXISTS IX_VersionParent_Parent ON Version_Parent (parent_version_id);");

    }

    /// <inheritdoc />
    public async Task EnsureActivityLifecycleAsync()
    {
        // Append-only audit of every lifecycle transition. EF entity + this DDL mirror for legacy DBs
        // (Friendship/Heatmap precedent). commit_id's FK is declared in BOTH places: in EF (RouteBaseDbContext,
        // ActivityLifecycleLog → TrackEditCommit) for fresh DBs, and here for legacy ones. TrackEditCommit is an
        // EF entity for exactly this reason — an EF entity may only FK another EF entity.
        //
        // track_id is ON DELETE CASCADE ON PURPOSE (decided 2026-08-02 — do not re-propose).
        // The consequence is real and was weighed: purging an activity deletes its lifecycle history WITH it,
        // including the 'trashed' transition, so after the fact an audit can only say "the shrinkage is
        // cascade-shaped" — it can never distinguish an intended trash-flow purge from a hard delete.
        // Accepted because the only actor who can purge here is the activity's own owner, so the audit trail
        // would be evidence about you, for you. Note commit_id one line below uses ON DELETE SET NULL, so the
        // non-cascading pattern is available in-house if that ever stops being true — the constraint is the
        // decision, not a limitation.
        await _context.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS Activity_Lifecycle_Log (
                log_id     INTEGER PRIMARY KEY AUTOINCREMENT,
                track_id   INTEGER NOT NULL REFERENCES Activity(track_ID) ON DELETE CASCADE,
                user_id    TEXT,
                from_state TEXT,
                to_state   TEXT NOT NULL,
                reason     TEXT,
                commit_id  INTEGER REFERENCES Track_Edit_Commit(commit_id) ON DELETE SET NULL,
                at         TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_ActivityLifecycleLog_TrackId
                ON Activity_Lifecycle_Log (track_id, at);
            CREATE INDEX IF NOT EXISTS IX_Activity_Lifecycle_Log_commit_id
                ON Activity_Lifecycle_Log (commit_id);");
    }

    /// <inheritdoc />
    public async Task EnsureTrackEditTablesAsync()
    {
        // TrackEditor EditCore (A1 §2.8): the session/audit tables backing the track editor. Access stays with
        // EditSessionStore/TrackEditCommitStore (raw ADO, persistence ignorance); TrackEditCommit is in the EF
        // model for its SHAPE only (no DbSet), so Activity and Activity_Lifecycle_Log can FK it — an EF entity
        // may only FK another EF entity.
        //
        // A commit row is the SCRIPT — the explanation of an edit — and nothing else. What it DID is a query,
        // not a stored id list: the versions it wrote carry its commit_id (seq 1 = it created that activity,
        // seq > 1 = it replaced one), and the activities it consumed carry it in Activity.consumed_commit_id.
        // The old replaced_ids/created_ids/trashed_ids JSON columns are GONE from this schema — no drop
        // migration exists or is needed (verified 2026-07-17: the production DB carries exactly the
        // columns below): ids inside a string can't be joined or indexed, and nothing keeps them honest
        // when a row is purged out from under them.
        //
        // Track_Blob_History is likewise GONE: pre-edit snapshots are subsumed by Activity_Version (a commit's
        // versions ARE its history), and revert is a head move rather than a blob rewrite.
        await _context.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS Track_Edit_Commit (
                commit_id     INTEGER PRIMARY KEY AUTOINCREMENT,
                user_id       TEXT NOT NULL,
                committed_at  TEXT NOT NULL,
                save_mode     TEXT NOT NULL,
                session_id    TEXT,
                script_json   TEXT NOT NULL,
                reverted_at   TEXT
            );
            CREATE INDEX IF NOT EXISTS IX_TrackEditCommit_UserId ON Track_Edit_Commit (user_id, committed_at);

            CREATE TABLE IF NOT EXISTS Activity_Edit_Session (
                session_id        TEXT PRIMARY KEY,
                user_id           TEXT NOT NULL,
                status            TEXT NOT NULL DEFAULT 'active',
                script_json       TEXT NOT NULL,
                cursor            INTEGER NOT NULL DEFAULT 0,
                stamps_json       TEXT NOT NULL DEFAULT '[]',
                edit_count        INTEGER NOT NULL DEFAULT 0,
                created_at        TEXT NOT NULL,
                updated_at        TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_ActivityEditSession_UserId ON Activity_Edit_Session (user_id, status);");

        // edit_count: the resume banner's number, denormalized out of script_json (EditSessionStore re-derives
        // it on every write, so it cannot drift). Legacy DBs get it by ALTER.
        await AddColumnIfMissingAsync("Activity_Edit_Session", "edit_count", "INTEGER NOT NULL DEFAULT 0");

    }
}
