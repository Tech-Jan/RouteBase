namespace RouteBaseWeb.Services.Interfaces;

/// <summary>
/// Manages startup SQL schema maintenance: index creation and helper-table back-fill.
/// Extracted from Program.cs so the logic is testable and not buried in top-level statements.
/// All methods use CREATE TABLE/INDEX IF NOT EXISTS and INSERT OR IGNORE — safe to call repeatedly.
/// </summary>
public interface IDbSchemaManager
{
    /// <summary>Ensures all performance-critical indexes exist on an existing database.</summary>
    Task EnsureIndexesAsync();

    /// <summary>Ensures the Activity_Bbox helper table exists and back-fills missing rows.</summary>
    Task EnsureActivityBboxTableAsync();

    /// <summary>Ensures the Photos table + indexes exist (modern photo store; columns mirror the EF entity).</summary>
    Task EnsurePhotosTableAsync();

    /// <summary>Ensures the Users table exists for OIDC authentication.</summary>
    Task EnsureUsersTableAsync();

    /// <summary>Ensures the ProfileSettings table exists (columns mirror the EF entity).</summary>
    Task EnsureProfileSettingsTableAsync();

    /// <summary>
    /// Adds columns introduced since the pre-web schema (multi-provider, multi-user, recording
    /// timezone, shape segments, rule documents, per-effort aggregates). Assumes the core legacy
    /// tables exist; see DbSchemaManager for the authoritative list. Safe to call repeatedly.
    /// </summary>
    Task EnsureNewColumnsAsync();

    /// <summary>Ensures the Friendship table and its indexes exist.</summary>
    Task EnsureFriendshipsTableAsync();

    /// <summary>Ensures the RecordingSession table and its indexes exist.</summary>
    Task EnsureRecordingSessionTableAsync();

    /// <summary>Ensures the Heatmap + Heatmap_Tiles tables (activity-heatmap tile pyramid) exist.</summary>
    Task EnsureHeatmapTablesAsync();

    /// <summary>Ensures the SegmentEffortExclusion table (durable effort-exclusion list, item 25) + its index exist.</summary>
    Task EnsureSegmentEffortExclusionTableAsync();

    /// <summary>Ensures the Dem_Tiles raw-PNG terrarium DEM tile cache exists (non-EF helper table).</summary>
    Task EnsureDemTilesTableAsync();

    /// <summary>Ensures the content/version tables (Track_Object, Activity_Version, Version_Parent) exist
    /// (non-EF helper tables).</summary>
    Task EnsureTrackContentTablesAsync();

    /// <summary>Ensures the Recording_Staging table (live-recording uncompacted point tail) exists (non-EF helper table).</summary>
    Task EnsureRecordingStagingTableAsync();

    /// <summary>Ensures the Error_Log diagnostic error store exists (non-EF helper table, no FK).</summary>
    Task EnsureErrorLogTableAsync();

    /// <summary>
    /// Ensures the two TrackEdit tables exist (Track_Edit_Commit, Activity_Edit_Session) — raw-ADO access,
    /// EF-modeled for shape so EF tables can FK them (EditCore A1 §2.8). Track_Blob_History is gone:
    /// a commit's versions ARE its history (Activity_Version).
    /// </summary>
    Task EnsureTrackEditTablesAsync();

    /// <summary>
    /// Race slice: Race, Race_Checkpoint, Race_Participant, Race_Split_Time. Must run after the Activity
    /// table exists — Race.track_id and Race_Participant.track_id both FK it.
    /// </summary>
    Task EnsureRaceTablesAsync();

    /// <summary>
    /// Share slice: Share_Link (anonymous capability URLs). Must run after the Activity and Segment tables
    /// exist — track_id and segment_id both FK them, and both cascade.
    /// </summary>
    Task EnsureShareLinkTableAsync();

    /// <summary>
    /// Lifecycle axis: the append-only Activity_Lifecycle_Log. Runs after the columns phase.
    /// </summary>
    Task EnsureActivityLifecycleAsync();

    /// <summary>
    /// Normalizes legacy ISO "yyyy-MM-ddTHH:mm:ss" values in Activity start columns to the canonical
    /// space-separated "yyyy-MM-dd HH:mm:ss" form, so EF LINQ date filters / ordering (lexicographic
    /// TEXT compare, where ' ' &lt; 'T') stay correct on mixed-format rows. Guard: rewrites only rows
    /// that still carry a 'T', so it's a no-op once the corpus is clean. Safe to call repeatedly.
    /// </summary>
    Task NormalizeDateTimeTextFormatsAsync();
}
