namespace RouteBaseWeb.Services.Sql;

/// <summary>
/// Raw Activity DML authored by the recording flow (B1-S1): shell insert at session
/// start, start-location/timezone fill-in at finalize, and the pending-only auto-recording discard.
/// Consolidates Activity column spelling behind <c>Services/Sql/</c> alongside
/// <see cref="ActivityImportSql"/> / <see cref="ActivityStatsSql"/> — Recording authors an Activity row
/// but no longer knows its schema inline. The paired <c>RecordingSession</c> UPDATEs stay inline in the
/// service (Recording's own table). Execution, transactions, and logging stay in the services.
/// </summary>
public static class ActivityRecordingSql
{
    // Session start: NULL-stats shell (RS-02). api_ID = 0 (no external id). source + lifecycle carry the
    // auto-vs-manual split: an auto-detected ride starts in-work/awaiting-review (invisible until the user
    // approves it), a manual one starts released and is watchable on the map as it records. Stats at finalize.
    public const string InsertShellSql = @"
                INSERT INTO Activity (
                    api_ID, track_name, activity_type,
                    start_datetime_utc, source, lifecycle, lifecycle_reason, user_id, visibility
                ) VALUES (
                    0, {0}, {1},
                    {2}, {3}, {4}, {5}, {6}, {7}
                );";

    // Finalize: fill in start/end time and location once the whole track is known.
    public const string UpdateStartLocationSql = @"
            UPDATE Activity SET
                start_datetime_utc  = {0},
                start_datetime_loc  = {1},
                datetime_timezone   = {2},
                start_gps_lat       = {3},
                start_gps_long      = {4},
                end_gps_lat         = {5},
                end_gps_long        = {6}
            WHERE track_ID = {7};";

    // Auto-recording adjudicator discard: a too-short / too-tiny auto ride is a false trigger. The
    // awaiting-review guard (CR4-02) is what stops a ride the user already approved from being stomped —
    // only a ride still in the review queue can be rejected. Floors: {1} = minMovingSeconds, {2} =
    // minDistanceMeters. Also subsumes the 0-point false start: its NULL stats read as 0, below every floor.
    // Raw SQL rather than ActivityLifecycleService because the guard IS the scope here (no HTTP user), and
    // the whole point is that it must match-or-do-nothing in one statement.
    public const string DiscardShortAutoSql = @"
            UPDATE Activity SET lifecycle = 'trashed', lifecycle_reason = 'review-rejected',
                                lifecycle_prev = 'in-work', lifecycle_at = datetime('now')
            WHERE track_ID = {0} AND source = 'auto-recording'
              AND lifecycle = 'in-work' AND lifecycle_reason = 'awaiting-review'
              AND (COALESCE(time_moving, 0) < {1} OR COALESCE(total_distance, 0) < {2});";

    // Auto-approve (AutoApproveRecordings): release an auto-detected ride at finalize instead of parking it
    // in the review queue. Carries the SAME awaiting-review guard as the discard above, and for the same
    // reason — it runs immediately after the adjudicator, so a ride that was just judged a false trigger is
    // now 'trashed' and must not be resurrected by this statement. Same no-HTTP-user rationale for raw SQL.
    public const string AutoApproveSql = @"
            UPDATE Activity SET lifecycle = 'released', lifecycle_reason = 'auto-approved',
                                lifecycle_prev = 'in-work', lifecycle_at = datetime('now')
            WHERE track_ID = {0} AND source = 'auto-recording'
              AND lifecycle = 'in-work' AND lifecycle_reason = 'awaiting-review';";
}
