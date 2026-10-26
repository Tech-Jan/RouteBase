namespace RouteBaseWeb.Services.Sql;

/// <summary>
/// Guarded Activity/bbox DML owned by the track editor (CommitPath §4.4; invariant 7 — Activity DML lives
/// behind <c>Services/Sql/</c>). Pure string constants; execution/transactions stay in the save/revert/derive
/// services. The consume/restore transitions are guarded on the exact state each writes so they never
/// intersect the auto-review machine (CR4-02: the guard is now <c>WHERE lifecycle='released'</c> / trashed+edit-merge).
/// </summary>
public static class ActivityEditSql
{
    // Replace-save: hide a splice-consumed source AND record which commit consumed it — that stamp is what
    // makes "what did this commit trash?" a query rather than a JSON id list. Guarded: only confirmed, only
    // the owner's row. ({0} = commit, {1} = track, {2} = user)
    public const string ConsumeSourceSql =
        "UPDATE Activity SET lifecycle = 'trashed', lifecycle_reason = 'edit-merge', " +
        "lifecycle_prev = 'released', lifecycle_at = datetime('now'), consumed_commit_id = {0} " +
        "WHERE track_ID = {1} AND user_id = {2} AND lifecycle = 'released';";

    // Revert: bring a consumed source back. Guarded on the exact state the save wrote.
    public const string RestoreConsumedSql =
        "UPDATE Activity SET lifecycle = 'released', lifecycle_reason = 'user', " +
        "lifecycle_prev = 'trashed', lifecycle_at = datetime('now'), " +
        "consumed_commit_id = NULL, superseded_by = NULL " +
        "WHERE track_ID = {0} AND user_id = {1} AND lifecycle = 'trashed' " +
        "AND lifecycle_reason = 'edit-merge';";

    // Option B: record which activity a merge folded this one into. Written in phase 2, once the merge product
    // exists and has an id. superseded_by is an FK ON DELETE SET NULL, so purging the PRODUCT un-supersedes its
    // sources automatically — there is no merge left to protect and the trash stops warning about them.
    // ({0} = merged-into activity, {1} = track, {2} = user)
    public const string SetSupersededBySql =
        "UPDATE Activity SET superseded_by = {0} " +
        "WHERE track_ID = {1} AND user_id = {2} AND lifecycle = 'trashed';";

    // Photos follow the merge onto the merged ride, remembering where they came from so a revert can send each
    // one home. Only photos still sitting on an input move (merged_from_track_id IS NULL) — a second merge of
    // an already-merged ride must not overwrite the original origin and strand the first revert.
    // ({0} = merged-into activity, {1} = source track, {2} = user)
    public const string MovePhotosToMergedSql =
        "UPDATE Photos SET merged_from_track_id = track_id, track_id = {0} " +
        "WHERE track_id = {1} AND user_id = {2} AND merged_from_track_id IS NULL;";

    // Revert: send each photo back to the ride it came from. A photo whose origin was purged has a NULL
    // merged_from_track_id (the FK saw to that) and simply stays on the product — which is about to be deleted,
    // so this runs BEFORE the purge and such a photo is genuinely unrecoverable. That is what the
    // "this merge can never be undone" confirmation was warning about.
    // ({0} = merge product, {1} = user)
    public const string RestorePhotosFromMergeSql =
        "UPDATE Photos SET track_id = merged_from_track_id, merged_from_track_id = NULL " +
        "WHERE track_id = {0} AND user_id = {1} AND merged_from_track_id IS NOT NULL;";

    // Check-out lock: claim the ride for an edit session only if it is free. The IS NULL guard is what
    // makes it atomic — 0 rows ⇒ someone already has it checked out (EditSessionService.ClaimAsync).
    // ({0} = session, {1} = track, {2} = user)
    public const string ClaimEditSessionSql =
        "UPDATE Activity SET in_edit_session_id = {0} " +
        "WHERE track_ID = {1} AND user_id = {2} AND in_edit_session_id IS NULL;";

    // Check-in: release the session's checkout pointer (at most one ride, structurally). The session row
    // stays behind (status 'saved'), so the pointer is released explicitly, not by an FK cascade. ({0} = session)
    public const string ReleaseEditSessionSql =
        "UPDATE Activity SET in_edit_session_id = NULL WHERE in_edit_session_id = {0};";

    // Edited badge (D-A2-6): point an activity at the commit that edited it.
    public const string SetEditedCommitSql =
        "UPDATE Activity SET edited_commit_id = {0} WHERE track_ID = {1} AND user_id = {2};";

    public const string ClearEditedCommitSql =
        "UPDATE Activity SET edited_commit_id = NULL WHERE track_ID = {0} AND user_id = {1};";

    // Edit-derive/save owns the sensor scalars (a crop changes hr/cadence/power aggregates; S4 flow-scoping).
    // A whole-NULL channel writes NULL.
    public const string UpdateSensorStatsSql = @"
        UPDATE Activity SET
            heart_rate_avg = {0}, heart_rate_max = {1},
            cadence_avg    = {2}, cadence_max    = {3},
            power_avg      = {4}, power_max      = {5}
        WHERE track_ID = {6};";

    // Exact bbox overwrite — the expand-only import upsert can't SHRINK after a crop (§0 bbox misfit).
    public const string RewriteActivityBboxSql = @"
        INSERT INTO Activity_Bbox (track_ID, min_lat, max_lat, min_long, max_long)
        VALUES ({0}, {1}, {2}, {3}, {4})
        ON CONFLICT(track_ID) DO UPDATE SET
          min_lat = excluded.min_lat, max_lat = excluded.max_lat,
          min_long = excluded.min_long, max_long = excluded.max_long;";
}
