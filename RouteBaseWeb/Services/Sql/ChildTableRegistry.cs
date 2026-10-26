namespace RouteBaseWeb.Services.Sql;

/// <summary>
/// Single owner of "what hangs off an Activity / Segment / User" (CR5-05). Every child table that must
/// be cleaned up when its parent goes is registered here as data; the three delete seams —
/// <c>ActivityCommandService.DeleteActivityCoreAsync</c>, <c>DatabaseResetService</c>,
/// <c>SegmentCrud.DeleteSegmentAsync</c> — consume these entries instead of carrying their own
/// hand-maintained table lists. <c>ChildTableRegistryParityTests</c> introspects the app-created schema
/// and fails when a track-/segment-/user-keyed table exists but isn't registered, so the historical miss
/// class (Activity_Bbox leak, ME-01 RecordingSession FK gap, F-3 SegmentEffortExclusion follow-up) can no
/// longer ship silently.
///
/// File-artifact hooks (photo rendition purge, .htiles delete, TrackReaderCache invalidate) stay explicit
/// at the seams — they are behavior, not table knowledge.
/// </summary>
public static class ChildTableRegistry
{
    /// <summary>One child table + the exact key-column spelling its cleanup DELETE must use.</summary>
    public sealed record ChildTable(string Table, string KeyColumn)
    {
        /// <summary>DELETE all rows for one parent key (positional <c>{0}</c> parameter form).</summary>
        public string DeleteByKeySql => $"DELETE FROM {Table} WHERE {KeyColumn} = {{0}}";

        /// <summary>DELETE all rows hanging off the user's tracks (SqlBatch <c>$userId</c> form).</summary>
        public string DeleteByUserTracksSql =>
            $"DELETE FROM [{Table}] WHERE {KeyColumn} IN (SELECT track_ID FROM Activity WHERE user_id = $userId);";

        /// <summary>DELETE all rows hanging off the user's segments (SqlBatch <c>$userId</c> form).</summary>
        public string DeleteByUserSegmentsSql =>
            $"DELETE FROM [{Table}] WHERE {KeyColumn} IN (SELECT segment_ID FROM Segment WHERE user_id = $userId);";

        /// <summary>DELETE the user's own rows (SqlBatch <c>$userId</c> form).</summary>
        public string DeleteByUserSql => $"DELETE FROM [{Table}] WHERE {KeyColumn} = $userId;";
    }

    // ---- track-keyed children -------------------------------------------------------------------

    public static readonly ChildTable PhotosByTrack = new("Photos", "track_id");

    public static readonly ChildTable EffortsByTrack = new("GPSinSegment", "track_id");

    // Item 25 (F-3): effort exclusions are cross-user, keyed by track_id AND segment_id with no FK
    // cascade — they must be purged with their effort rows on every path, so a deleted+re-imported
    // track/segment doesn't inherit stale exclusions. Registered in both lists (correct, not a dupe).
    public static readonly ChildTable EffortExclusionsByTrack = new("SegmentEffortExclusion", "track_id");

    // ME-01: explicit so the delete is FK-independent — fresh DBs only got the RecordingSession→Activity
    // cascade FK as of ME-01; legacy DBs already cascade (idempotent double-delete).
    public static readonly ChildTable RecordingSessionsByTrack = new("RecordingSession", "track_id");

    // Content axis: a track's revision list. FK CASCADE on track_id already covers deletion, but the entry
    // is explicit (RecordingSessionsByTrack precedent — FK-independent, legacy-DB-safe) and is what the
    // DatabaseResetService points-only path (no Activity delete) needs. Version_Parent cascades off it.
    //
    // The OBJECTS the versions point at are NOT listed here and must never be: they are content-addressed and
    // shared (two versions — or two activities — can be the same bytes), so they are reclaimed by refcount,
    // not by track key. Purging an activity drops its versions; ITrackObjectStore.SweepUnreferencedAsync then
    // collects whatever no version references any more.
    public static readonly ChildTable ActivityVersionsByTrack = new("Activity_Version", "track_id");

    // Lifecycle audit. FK CASCADE already covers it; registered explicitly for the same reason as
    // RecordingSessionsByTrack (FK-independent, legacy-DB-safe) and so the rot-guard parity test sees it.
    public static readonly ChildTable LifecycleLogByTrack = new("Activity_Lifecycle_Log", "track_id");

    public static readonly ChildTable RecordingStagingByTrack = new("Recording_Staging", "track_ID");

    // inval-09: Activity_Bbox is not EF-modelled, so EF's Remove(activity) never cascade-deletes it —
    // always explicit, or the row leaks (harmless to selection, which INNER JOINs Activity, but leaked).
    public static readonly ChildTable ActivityBboxByTrack = new("Activity_Bbox", "track_ID");

    // Race slice — the activity appears in TWO roles, and both die with it:
    //   Race.track_id           the COURSE. A race whose course is gone can never be replayed.
    //   Race_Participant.track_id  a REAL rider (own or a friend's ride) in someone's field.
    // Race_Checkpoint and Race_Split_Time carry no track key: they hang off these two and are covered
    // by ON DELETE CASCADE (the runtime FK-on assumption is pinned by
    // DatabaseResetServiceTests.RuntimeForeignKeys_DefaultOn_CascadeFires).
    public static readonly ChildTable RaceParticipantsByTrack = new("Race_Participant", "track_id");
    public static readonly ChildTable RacesByTrack = new("Race", "track_id");

    // Share slice: a live capability URL must not outlive the resource it grants. ON DELETE CASCADE in both
    // schema authors already covers this; registered explicitly for the same reason as
    // RecordingSessionsByTrack (FK-independent, legacy-DB-safe) and so the rot guard sees it. The same table
    // appears in all three lists — it carries track_id, segment_id AND user_id (the EffortExclusionsBy*
    // precedent: correct, not a dupe).
    public static readonly ChildTable ShareLinksByTrack = new("Share_Link", "track_id");

    /// <summary>
    /// Everything keyed by <c>track_id</c>/<c>track_ID</c> that dies with its Activity. Order is
    /// FK-safety load-bearing: children before the Activity parent row.
    /// </summary>
    public static readonly IReadOnlyList<ChildTable> TrackChildTables = new[]
    {
        PhotosByTrack,
        EffortsByTrack,
        EffortExclusionsByTrack,
        RecordingSessionsByTrack,
        ActivityVersionsByTrack,
        LifecycleLogByTrack,
        RecordingStagingByTrack,
        ActivityBboxByTrack,
        // Nothing hangs off a share link, so its position is free.
        ShareLinksByTrack,
        // Participants before races: a participant hangs off a Race as well as an Activity, so it goes first.
        RaceParticipantsByTrack,
        RacesByTrack,
    };

    // ---- segment-keyed children -----------------------------------------------------------------

    public static readonly ChildTable EffortsBySegment = new("GPSinSegment", "segment_id");

    public static readonly ChildTable SegmentGpsBySegment = new("Segment_GPS", "segment_ID");

    // Item 25 (F-3): see EffortExclusionsByTrack — the same table, purged by its segment key here.
    public static readonly ChildTable EffortExclusionsBySegment = new("SegmentEffortExclusion", "segment_id");

    // Share slice: see ShareLinksByTrack — the same table, purged by its segment key here.
    public static readonly ChildTable ShareLinksBySegment = new("Share_Link", "segment_id");

    /// <summary>
    /// Everything keyed by <c>segment_id</c>/<c>segment_ID</c> deleted explicitly when its Segment goes.
    /// Only SegmentEffortExclusion is genuinely FK-less; the others carry ON DELETE CASCADE on both pinned
    /// schema vintages (legacy-baseline.sql + fresh EF), so their deletes are belt-and-braces idempotence.
    /// Order matches the SA-06 atomic delete.
    /// </summary>
    public static readonly IReadOnlyList<ChildTable> SegmentChildTables = new[]
    {
        EffortsBySegment,
        SegmentGpsBySegment,
        EffortExclusionsBySegment,
        ShareLinksBySegment,
    };

    /// <summary>
    /// Segment-keyed tables covered by <c>ON DELETE CASCADE</c> in BOTH the EF model and the
    /// DbSchemaManager DDL (so every DB vintage cascades) — documented here as data for the parity
    /// test, never emitted as DELETE statements. Heatmap's .htiles tile file is a file artifact,
    /// purged explicitly at the delete/reset seams.
    /// </summary>
    public static readonly IReadOnlyList<string> SegmentCascadeCoveredTables = new[] { "Heatmap" };

    // ---- race-keyed children ----------------------------------------------------------------------

    /// <summary>
    /// Everything keyed by <c>race_ID</c>/<c>race_id</c>, ALL of it covered by <c>ON DELETE CASCADE</c> in
    /// both schema authors — data for the rot guard, never emitted as DELETE statements. Unlike the
    /// activity and segment axes there is no hand-written race delete seam to feed: RaceCrud.DeleteRaceAsync
    /// removes the Race row and the cascade does the rest (pinned by
    /// DatabaseResetServiceTests.RuntimeForeignKeys_DefaultOn_CascadeFires). The entry that matters here is
    /// <c>Share_Link</c>: deleting a race must revoke its share links structurally, exactly as deleting an
    /// activity or a segment does — and listing it means a future race-keyed table cannot be added without
    /// someone stating which of the two it is.
    /// </summary>
    public static readonly IReadOnlyList<string> RaceCascadeCoveredTables =
        new[] { "Race_Checkpoint", "Race_Participant", "Share_Link" };

    // ---- user-keyed roots (DatabaseResetService) --------------------------------------------------

    public static readonly ChildTable MultiDayToursByUser = new("Multi_Day_Tour", "user_id");
    public static readonly ChildTable ActivitiesByUser = new("Activity", "user_id");
    public static readonly ChildTable SegmentsByUser = new("Segment", "user_id");
    // Reset deletes AppCredentials with an extra Type-scoped condition (special SQL stays at the seam);
    // registered so the parity test knows the table's user cleanup is owned.
    public static readonly ChildTable AppCredentialsByUser = new("AppCredentials", "user_id");
    public static readonly ChildTable ProfileSettingsByUser = new("ProfileSettings", "UserId");
    // EditCore (A1 §2.8): session rows never FK-bind to Activity (a session spans many source tracks).
    public static readonly ChildTable EditSessionsByUser = new("Activity_Edit_Session", "user_id");
    public static readonly ChildTable EditCommitsByUser = new("Track_Edit_Commit", "user_id");
    // A race is also a user-owned root: the track-keyed delete already takes it when its course activity
    // goes, but a data reset must wipe races directly too (idempotent double-delete, RecordingSession precedent).
    public static readonly ChildTable RacesByUser = new("Race", "user_id");
    // A share link is also a user-owned root: the track/segment cascades take it when the shared resource
    // goes, but a data reset must revoke the user's links directly too (idempotent double-delete,
    // RacesByUser precedent). A reset that left live tokens behind would be a data-exposure bug.
    public static readonly ChildTable ShareLinksByUser = new("Share_Link", "user_id");
    // B8: diagnostic error rows carry a FK-less user_id (a store write must never fail on a missing
    // Users row), so nothing cascades them — reset owns the wipe explicitly, on EVERY confirmed reset
    // ("reset means reset"; the rows are diagnostic exhaust about the data being deleted, not user
    // content worth a checkbox). System rows (user_id NULL) are not the user's and survive.
    public static readonly ChildTable ErrorLogByUser = new("Error_Log", "user_id");

    /// <summary>
    /// Tables wiped per-user by DatabaseResetService (roots included). Photos/RecordingSession also
    /// carry a denormalized <c>user_id</c> but are cleaned via their track key (TrackChildTables) or the
    /// Activity cascade — the parity test accepts track registration as user coverage. The Users row
    /// itself is the account root and is never wiped by a data reset.
    /// </summary>
    public static readonly IReadOnlyList<ChildTable> UserOwnedTables = new[]
    {
        MultiDayToursByUser,
        ActivitiesByUser,
        SegmentsByUser,
        AppCredentialsByUser,
        ProfileSettingsByUser,
        EditSessionsByUser,
        EditCommitsByUser,
        RacesByUser,
        ShareLinksByUser,
        ErrorLogByUser,
    };
}
