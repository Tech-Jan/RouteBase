using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using RouteBaseWeb.Constants;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services;

/// <inheritdoc />
/// <remarks>
/// Legacy patcher boundary: assumes the pre-web core schema (Activity, Segment, GPSinSegment,
/// Segment_GPS, Multi_Day_Tour, AppCredentials) already exists on every real DB — no Ensure method for
/// those. (GPS was one of these until BlobStore dropped it.) Everything app-era (Users,
/// Friendship, RecordingSession, Heatmap, Photos, ProfileSettings)
/// has an Ensure*TableAsync method so legacy DBs boot clean.
/// Parity with the EF model (OnModelCreating) is enforced by DbSchemaParityTests.
/// New entity? EF entity + matching Ensure*Table DDL + reset coverage; DbSchemaParityTests fails on drift.
///
/// THE SCHEMA HAS TWO AUTHORS, AND ONE RULE FOLLOWS FROM IT.
/// EF authors the DDL for the entities in its model (EnsureCreated, fresh DBs); this class authors everything
/// else and patches legacy DBs. So:
///
///   • a raw-DDL table here may reference ANY table — it is created last, so every target already exists
///     (Activity_Version.track_id → Activity, Activity_Version.commit_id → Track_Edit_Commit);
///   • an EF entity may only reference ANOTHER EF ENTITY — EF writes its own CREATE TABLE and does not know
///     the raw tables exist. Declaring such an FK in raw DDL instead makes the legacy and fresh schemas
///     diverge, which DbSchemaParityTests fails on (FK-ONLY-LEGACY).
///
/// So: if an EF entity needs a foreign key to table X, X must be an EF entity. That is the whole reason
/// TrackEditCommit is one (see Models/TrackEditCommit.cs) despite TrackEditCommitStore owning its data — three
/// EF tables point at it. Do not reach for a trigger to fake the constraint; promote the table instead.
/// </remarks>
public partial class DbSchemaManager : IDbSchemaManager
{
    // Content + lifecycle + edit-table authorship lives in DbSchemaManager.ContentLifecycle.cs (same class,
    // split by the file-size rule).

    private readonly RouteBaseDbContext _context;

    public DbSchemaManager(RouteBaseDbContext context) => _context = context;

    /// <summary>
    /// The complete legacy-mirror index/DDL statement list applied by <see cref="EnsureIndexesAsync"/>.
    /// Exposed for DbSchemaParityTests, which asserts every CREATE INDEX here matches the fresh EF schema.
    /// </summary>
    internal static readonly string[] IndexDdl = new[]
    {
            // GPS table + its 4 indexes are gone since BlobStore (point data lives in the content axis).
            // Nothing indexes GPS anymore.
            // Segment_GPS / GPSinSegment / AppCredentials / RecordingSession / ProfileSettings indexes
            // are the complete legacy mirror of OnModelCreating for pre-EF-era DBs (EnsureCreated only
            // builds them on fresh DBs) — appended below. Parity is enforced by DbSchemaParityTests;
            // extend both sides together.
            // Activity table — used for ordering in GetActivitiesAsync
            "CREATE INDEX IF NOT EXISTS IX_Activity_StartDate  ON Activity (start_datetime_utc DESC);",
            // IX_Activity_StartDateLoc (not the retired IX_Activity_StartDateLocal) — a plain
            // ORDER BY start_datetime_loc DESC, the dashboard/overview grid's sort (ActivityQueryService
            // .GetAllActivitiesAsync). Unlike the retired index's datetime()-wrapped consumer, a bare-
            // column index serves this one directly.
            "CREATE INDEX IF NOT EXISTS IX_Activity_StartDateLoc ON Activity (start_datetime_loc DESC);",
            // Activity_Bbox index is created inside EnsureActivityBboxTableAsync, not here,
            // because the table must exist first.

            // Phase 2 (multi-user) indexes — also defined in OnModelCreating for new DBs
            "CREATE INDEX IF NOT EXISTS IX_Activity_Lifecycle          ON Activity (lifecycle);",
            // EF auto-creates an index behind every FK it models — mirror them or the schemas diverge.
            "CREATE INDEX IF NOT EXISTS IX_Activity_edited_commit_id   ON Activity (edited_commit_id);",
            "CREATE INDEX IF NOT EXISTS IX_Activity_consumed_commit_id ON Activity (consumed_commit_id);",
            "CREATE INDEX IF NOT EXISTS IX_Activity_superseded_by      ON Activity (superseded_by);",
            "CREATE INDEX IF NOT EXISTS IX_Activity_in_edit_session_id ON Activity (in_edit_session_id);",
            "CREATE INDEX IF NOT EXISTS IX_Photos_merged_from_track_id ON Photos (merged_from_track_id);",
            "CREATE INDEX IF NOT EXISTS IX_Activity_UserId             ON Activity (user_id);",
            "CREATE INDEX IF NOT EXISTS IX_Activity_UserId_StartDate   ON Activity (user_id, start_datetime_utc DESC);",
            "CREATE INDEX IF NOT EXISTS IX_Activity_Visibility         ON Activity (visibility);",
            "CREATE INDEX IF NOT EXISTS IX_Segment_UserId              ON Segment (user_id);",
            "CREATE INDEX IF NOT EXISTS IX_Segment_Visibility          ON Segment (visibility);",
            "CREATE INDEX IF NOT EXISTS IX_MultiDayTour_UserId         ON Multi_Day_Tour (user_id);",
            "CREATE INDEX IF NOT EXISTS IX_AppCredentials_UserId_Type  ON AppCredentials (user_id, Type);",

            // Legacy mirror of EF-only indexes (see comment above) — pre-EF DBs never got these.
            "CREATE INDEX IF NOT EXISTS IX_SegmentGps_SegmentId   ON Segment_GPS (segment_ID);",
            // No IX_SegmentGps_GeoCell: the geo_cell column is retired — the matcher hashes cells in C#
            // (GeoCellHash) from lat/lng and never read the stored column.
            "CREATE INDEX IF NOT EXISTS IX_GpsInSegment_SegmentId ON GPSinSegment (segment_id);",
            "CREATE INDEX IF NOT EXISTS IX_GpsInSegment_TrackId   ON GPSinSegment (track_id);",
            "CREATE INDEX IF NOT EXISTS IX_AppCredentials_Type ON AppCredentials (Type);",
            "CREATE INDEX IF NOT EXISTS IX_RecordingSession_TrackId ON RecordingSession (track_id);",

            // ProfileSettings unique (Category, SettingKey, UserId). Every row is user-scoped
            // (UserId NOT NULL), so the index genuinely constrains the whole table — while UserId was
            // nullable, SQLite's NULL-distinct rule left global rows unconstrained.
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_ProfileSettings_Category_SettingKey_UserId " +
                "ON ProfileSettings (Category, SettingKey, UserId);",
    };

    /// <inheritdoc />
    public async Task EnsureIndexesAsync()
    {
        foreach (var sql in IndexDdl)
        {
            await _context.Database.ExecuteSqlRawAsync(sql);
        }
    }

    /// <inheritdoc />
    public async Task EnsureActivityBboxTableAsync()
    {
        // Create the Activity_Bbox helper table if it doesn't exist yet.
        // This table is maintained incrementally by the import pipeline (one row per activity)
        // so Stage 1 can avoid a full GPS GROUP BY scan on every analysis run.
        await _context.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS Activity_Bbox (
                track_ID  INTEGER PRIMARY KEY REFERENCES Activity(track_ID) ON DELETE CASCADE,
                min_lat   REAL NOT NULL,
                max_lat   REAL NOT NULL,
                min_long  REAL NOT NULL,
                max_long  REAL NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_ActivityBbox_TrackId ON Activity_Bbox (track_ID);");

        // No GPS-scan backfill here anymore: the blob store holds the points, bbox rows are written by the
        // import/recording/finalize funnels, and the one-shot blob migration self-heals any missing row
        // (INSERT OR IGNORE from each decoded blob's min/max) during encode-all.
    }

    /// <inheritdoc />
    public async Task EnsurePhotosTableAsync()
    {
        // Modern photo store (replaces the pre-web Pictures table).
        // Every photo is assigned to an Activity (§5, no unassigned rows) — deleting the activity cascades
        // the rows; the rendition JPEGs live on the filesystem (IPhotoImageStore), so the only image-derived
        // DB column is the ThumbHash placeholder. Columns/indexes/FK mirror Models/Photo.cs (EF) — parity
        // enforced by DbSchemaParityTests. Column DEFAULTs are legacy-boot niceties (not parity-compared).
        await _context.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS Photos (
                photo_id         INTEGER PRIMARY KEY AUTOINCREMENT,
                user_id          TEXT,
                track_id         INTEGER NOT NULL REFERENCES Activity(track_ID) ON DELETE CASCADE,
                assignment       TEXT,
                captured_utc     TEXT,
                tz_offset        REAL,
                lat              REAL,
                lng              REAL,
                location_source  TEXT,
                on_map           INTEGER NOT NULL DEFAULT 1,
                track_position_m REAL,
                orig_filename    TEXT,
                content_hash     TEXT,
                camera_model     TEXT,
                has_exif_time    INTEGER NOT NULL DEFAULT 0,
                thumbhash        BLOB,
                storage          TEXT,
                width            INTEGER NOT NULL DEFAULT 0,
                height           INTEGER NOT NULL DEFAULT 0,
                description      TEXT,
                imported_utc     TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Photo_TrackId            ON Photos (track_id);
            CREATE INDEX IF NOT EXISTS IX_Photo_UserId             ON Photos (user_id);
            CREATE INDEX IF NOT EXISTS IX_Photo_UserId_ContentHash ON Photos (user_id, content_hash);
            CREATE INDEX IF NOT EXISTS IX_Photo_UserId_CapturedUtc ON Photos (user_id, captured_utc);");
    }

    /// <inheritdoc />
    public async Task EnsureUsersTableAsync()
    {
        await _context.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS Users (
                user_id      TEXT PRIMARY KEY,
                display_name TEXT,
                email        TEXT,
                is_admin     INTEGER NOT NULL DEFAULT 0,
                created_at   TEXT NOT NULL,
                last_login   TEXT NOT NULL
            );");
    }

    /// <inheritdoc />
    public async Task EnsureProfileSettingsTableAsync()
    {
        // Columns mirror Models/ProfileSettings.cs (EF entity). The unique index
        // (Category, SettingKey, UserId) is created in EnsureIndexesAsync.
        //
        // UserId is NOT NULL: every row belongs to a user. DataType/Description/DefaultValue are gone —
        // all three were write-only, and DefaultValue in particular was a second copy of
        // ProfileSettingsDefaults that had already drifted from it.
        await _context.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS ProfileSettings (
                Id           INTEGER PRIMARY KEY AUTOINCREMENT,
                Category     TEXT NOT NULL,
                SettingKey   TEXT NOT NULL,
                SettingValue TEXT NOT NULL,
                CreatedAt    TEXT NOT NULL,
                UpdatedAt    TEXT NOT NULL,
                UserId       TEXT NOT NULL
            );");
    }

    /// <inheritdoc />
    public async Task EnsureNewColumnsAsync()
    {
        // Use PRAGMA table_info to check column existence before issuing ALTER TABLE.
        // SQLite has no "ADD COLUMN IF NOT EXISTS" syntax, and EF Core logs failed
        // commands at Error level before the exception reaches a try-catch — so the
        // try-catch approach produces noisy false errors on fresh databases where EF Core
        // already created the columns from the model.

        // Assumes every core pre-web table (Activity, Segment, Multi_Day_Tour, AppCredentials,
        // GPSinSegment) and every app-era table with an Ensure*TableAsync method already exist — the
        // init block runs the tables phase before this columns phase (see Program.cs). ALTERing a
        // missing table would throw "no such table"; AddColumnIfMissingAsync sees an empty
        // pragma_table_info for a missing table and would issue the ALTER, so ordering is load-bearing.

        // Phase 1 (multi-provider refactor) columns. The former GPS.grade_smooth ALTER is gone — GPS is no
        // longer a live table (an ALTER on the migration-dropped table would throw at startup); the blob
        // codec carries the grade_smooth channel.
        await AddColumnIfMissingAsync("Activity", "calories",     "REAL");
        await AddColumnIfMissingAsync("Activity", "source",       "TEXT DEFAULT 'strava'");

        // Phase 2 (multi-user) columns
        await AddColumnIfMissingAsync("Activity",      "user_id",    "TEXT");
        await AddColumnIfMissingAsync("Activity",      "visibility", "TEXT DEFAULT 'friends'");
        await AddColumnIfMissingAsync("Segment",       "user_id",    "TEXT");
        await AddColumnIfMissingAsync("Segment",       "visibility", "TEXT DEFAULT 'friends'");
        await AddColumnIfMissingAsync("Multi_Day_Tour","user_id",    "TEXT");
        await AddColumnIfMissingAsync("AppCredentials","user_id",    "TEXT");

        // Phase 3 (live recording timezone) columns
        await AddColumnIfMissingAsync("RecordingSession", "timezone_offset_minutes", "INTEGER");

        // Phase 5 (shape-defined segments) — discriminator column. The column DEFAULT is what keeps it
        // non-NULL: every writer supplies a value, and one that omitted it would still get 'polyline'.
        await AddColumnIfMissingAsync("Segment", "segment_kind", "TEXT DEFAULT 'polyline'");

        // Rules rework — the JSON rule document that fully defines a shape segment.
        // (The legacy shape_match_mode column is left in place; SQLite ignores it harmlessly.)
        await AddColumnIfMissingAsync("Segment", "match_rules_json", "TEXT");

        // Phase 6 (owner-scoped shape segments) — which owners' visible tracks a shape segment matches.
        // NULL = legacy "own tracks only"; existing segments keep that behaviour with no back-fill.
        await AddColumnIfMissingAsync("Segment", "match_owner_ids", "TEXT");

        // Creation timestamp — drives the "Created" column + date-range filter on the Segments index.
        // NULL for legacy segments (no recorded creation time); new segments stamp it on create.
        await AddColumnIfMissingAsync("Segment", "created_at", "TEXT");

        // Precomputed per-effort aggregates — filled by analysis so the segment cards' "/ Total"
        // readouts sum stored values instead of rescanning GPS. NULL = not yet analysed (fallback path).
        await AddColumnIfMissingAsync("GPSinSegment", "effort_distance",  "REAL");
        await AddColumnIfMissingAsync("GPSinSegment", "effort_elevation", "REAL");
        await AddColumnIfMissingAsync("GPSinSegment", "effort_time",      "REAL");

        // Per-effort averages — lets the effort table build entirely from stored columns
        // (no GPS window rescans on page load). Same NULL = not-yet-analysed convention.
        await AddColumnIfMissingAsync("GPSinSegment", "effort_speed_avg",   "REAL");
        await AddColumnIfMissingAsync("GPSinSegment", "effort_hr_avg",      "REAL");
        await AddColumnIfMissingAsync("GPSinSegment", "effort_power_avg",   "REAL");
        await AddColumnIfMissingAsync("GPSinSegment", "effort_cadence_avg", "REAL");

        // Photos — map visibility toggle. Unpinning hides a photo (on_map=0) while keeping its lat/lng so
        // it can be restored to the same spot; existing rows default to shown.
        await AddColumnIfMissingAsync("Photos", "on_map", "INTEGER NOT NULL DEFAULT 1");

        // Photos follow a merge onto the merged ride; this is how a revert sends each one home again.
        await AddColumnIfMissingAsync("Photos", "merged_from_track_id",
            "INTEGER REFERENCES Activity(track_ID) ON DELETE SET NULL");

        // Lifecycle axis (ActivityLifecycle Batch 2). NOT NULL DEFAULT 'released' — the store default is what
        // makes an imported row visible (ActivityImportSql omits the column). The rest of the axis is
        // nullable: an activity that has never transitioned has no reason, no restore target and no
        // timestamp, and NULL says that honestly.
        await AddColumnIfMissingAsync("Activity", "lifecycle", "TEXT NOT NULL DEFAULT 'released'");
        await AddColumnIfMissingAsync("Activity", "lifecycle_reason", "TEXT");
        await AddColumnIfMissingAsync("Activity", "lifecycle_prev",   "TEXT");
        await AddColumnIfMissingAsync("Activity", "lifecycle_at",     "TEXT");
        await AddColumnIfMissingAsync("Activity", "superseded_by",    "INTEGER REFERENCES Activity(track_ID) ON DELETE SET NULL");

        // The two commit pointers, both with a real FK (ON DELETE SET NULL) — purging a commit nulls them
        // instead of leaving them pointing at a row that is gone. The FK is only expressible because
        // TrackEditCommit is an EF entity: EF authors Activity's DDL and can reference nothing it does not
        // model. Nullable, so the REFERENCES clause is legal on ALTER TABLE ADD COLUMN too.
        //   edited_commit_id   — the newest non-reverted commit that edited this activity (the "edited" badge)
        //   consumed_commit_id — the commit that spliced this activity into another (a merge trashed it)
        await AddColumnIfMissingAsync("Activity", "edited_commit_id",
            "INTEGER REFERENCES Track_Edit_Commit(commit_id) ON DELETE SET NULL");
        await AddColumnIfMissingAsync("Activity", "consumed_commit_id",
            "INTEGER REFERENCES Track_Edit_Commit(commit_id) ON DELETE SET NULL");

        // The checkout pointer: the open edit session this ride is loaded into, or NULL when it is free. Same
        // reason the two commit pointers can have an FK — Activity_Edit_Session is an EF entity. ON DELETE SET
        // NULL is what makes an abort one statement (delete the session → the ride is released), and the column
        // being on Activity is what makes the lock structural: one open edit per ride, enforced by a guarded
        // UPDATE ... WHERE in_edit_session_id IS NULL rather than by a read-then-write race.
        await AddColumnIfMissingAsync("Activity", "in_edit_session_id",
            "TEXT REFERENCES Activity_Edit_Session(session_id) ON DELETE SET NULL");
    }

    /// <inheritdoc />
    public async Task EnsureFriendshipsTableAsync()
    {
        await _context.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS Friendship (
                friendship_id   INTEGER PRIMARY KEY AUTOINCREMENT,
                requester_id    TEXT NOT NULL REFERENCES Users(user_id) ON DELETE CASCADE,
                addressee_id    TEXT NOT NULL REFERENCES Users(user_id) ON DELETE CASCADE,
                status          TEXT NOT NULL DEFAULT 'pending',
                created_at      TEXT NOT NULL,
                updated_at      TEXT NOT NULL,
                UNIQUE(requester_id, addressee_id)
            );
            CREATE INDEX IF NOT EXISTS IX_Friendship_Requester ON Friendship(requester_id, status);
            CREATE INDEX IF NOT EXISTS IX_Friendship_Addressee ON Friendship(addressee_id, status);");
    }

    /// <inheritdoc />
    public async Task EnsureRecordingSessionTableAsync()
    {
        await _context.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS RecordingSession (
                session_id      TEXT PRIMARY KEY,
                track_id        INTEGER NOT NULL REFERENCES Activity(track_ID) ON DELETE CASCADE,
                user_id         TEXT NOT NULL,
                status          TEXT NOT NULL DEFAULT 'recording',
                activity_type   TEXT NOT NULL DEFAULT 'Run',
                started_at      TEXT NOT NULL,
                stopped_at      TEXT,
                last_point_at   TEXT,
                point_count     INTEGER NOT NULL DEFAULT 0,
                last_sequence   INTEGER NOT NULL DEFAULT -1,
                timezone_offset_minutes INTEGER,
                visibility      TEXT NOT NULL DEFAULT 'friends',
                client_version  TEXT
            );
            CREATE INDEX IF NOT EXISTS IX_RecordingSession_UserId ON RecordingSession(user_id);
            CREATE INDEX IF NOT EXISTS IX_RecordingSession_Status ON RecordingSession(status);");

        // Single-active-session invariant (RS-02): partial unique index on user_id over active rows.
        // It is the race-proof backstop under RecordingService's check-then-insert, not a hint.
        await _context.Database.ExecuteSqlRawAsync(@"
            CREATE UNIQUE INDEX IF NOT EXISTS IX_RecordingSession_ActiveUser
                ON RecordingSession(user_id) WHERE status IN ('recording','paused');");
    }

    /// <inheritdoc />
    public async Task EnsureHeatmapTablesAsync()
    {
        // Heatmap metadata for heatmap-kind segments. Tile imagery no longer lives in the DB — it moved
        // to one packed .htiles file per heatmap on the filesystem (HeatmapTilePackStore). Deleting a
        // Segment cascades to Heatmap; the tile file is purged explicitly at the delete/reset seams.
        await _context.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS Heatmap (
                heatmap_ID     INTEGER PRIMARY KEY AUTOINCREMENT,
                segment_ID     INTEGER NOT NULL REFERENCES Segment(segment_ID) ON DELETE CASCADE,
                status         TEXT NOT NULL DEFAULT 'pending',
                error          TEXT,
                min_zoom       INTEGER,
                max_zoom       INTEGER,
                count_max      REAL,
                bounds_south   REAL,
                bounds_west    REAL,
                bounds_north   REAL,
                bounds_east    REAL,
                activity_ids   TEXT,
                activity_types TEXT,
                generated_at   TEXT
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Heatmap_SegmentId ON Heatmap (segment_ID);");

    }

    /// <inheritdoc />
    public async Task EnsureSegmentEffortExclusionTableAsync()
    {
        // Durable effort-exclusion list (item 25). Mirrors the EF entity so a fresh EF DB (EnsureCreated)
        // and a patched legacy DB converge (DbSchemaParityTests). No FK cascade — rows are deleted
        // explicitly at the segment/track/reset seams (legacy no-cascade convention).
        await _context.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS SegmentEffortExclusion (
                exclusion_id  INTEGER PRIMARY KEY AUTOINCREMENT,
                segment_id    INTEGER NOT NULL,
                track_id      INTEGER NOT NULL,
                entry_index   INTEGER NOT NULL,
                excluded_at   TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_SegmentEffortExclusion_SegmentTrack
                ON SegmentEffortExclusion (segment_id, track_id);");
    }

    /// <inheritdoc />
    public async Task EnsureDemTilesTableAsync()
    {
        // Persistent raw-PNG terrarium DEM tile cache (one blob per z/x/y). No EF entity — no
        // relations/cascades/queries (raw ADO via DemTileStore, persistence ignorance). WITHOUT ROWID:
        // the (z, x, y) PK is the only access path. DEM is static + user-independent, so there is no
        // user column, no FK, and no expiry/eviction (fetched_at is diagnostic only).
        await _context.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS Dem_Tiles (
                z          INTEGER NOT NULL,
                x          INTEGER NOT NULL,
                y          INTEGER NOT NULL,
                png        BLOB    NOT NULL,
                fetched_at TEXT    NOT NULL,
                PRIMARY KEY (z, x, y)
            ) WITHOUT ROWID;");
    }

    /// <inheritdoc />
    public async Task EnsureRecordingStagingTableAsync()
    {
        // Uncompacted tail of a live recording session — slim rows (no sensor channels; live capture has
        // none), compacted into a Track_Blob on finalize and deleted. No EF entity — raw ADO via
        // RecordingStagingStore (Dem_Tiles/Track_Blob precedent). WITHOUT ROWID: the composite
        // (track_ID, seq) PK is the only access path (append tail + ordered read). Epoch-second ts_utc is
        // legal here — the TC-02 ban is GPS-table-only; this transient table mirrors the blob time channel.
        // ts_utc is NOT NULL (API-10): a single NULL row throws in LivePointDto.From → 500s both live
        // surfaces, and Decision K already treats the timestamp as non-null in code. The sole writer
        // (RecordingService append) always stamps it, so the constraint only makes an impossible state
        // unrepresentable.
        await _context.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS Recording_Staging (
                track_ID  INTEGER NOT NULL REFERENCES Activity(track_ID) ON DELETE CASCADE,
                seq       INTEGER NOT NULL,
                lat       REAL NOT NULL,
                lng       REAL NOT NULL,
                altitude  REAL,
                speed     REAL,
                distance  REAL,
                ts_utc    INTEGER NOT NULL,
                pressure  REAL,
                PRIMARY KEY (track_ID, seq)
            ) WITHOUT ROWID;");
        // Existing DBs created before the barometric-pressure channel need the column back-filled.
        await AddColumnIfMissingAsync("Recording_Staging", "pressure", "REAL");
    }

    /// <inheritdoc />
    public async Task EnsureErrorLogTableAsync()
    {
        // Diagnostic error store — one row per captured Error/Critical log (OBS-02). No EF entity: raw ADO
        // via ErrorLogStore (Dem_Tiles/Track_Blob precedent, persistence ignorance). No FK on user_id — a
        // store write must never fail on a missing Users row. Rowid table (INTEGER PK aliases rowid):
        // newest-first paging orders by error_id DESC and the retention cap keeps the table tiny, so no
        // secondary index is warranted. timestamp_utc is the house "yyyy-MM-dd HH:mm:ss" (space) form.
        await _context.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS Error_Log (
                error_id       INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc  TEXT NOT NULL,
                source         TEXT NOT NULL,
                category       TEXT NOT NULL,
                level          TEXT NOT NULL,
                message        TEXT NOT NULL,
                exception_type TEXT,
                stack_trace    TEXT,
                path           TEXT,
                user_id        TEXT
            );");
    }

    /// <inheritdoc />
    public async Task NormalizeDateTimeTextFormatsAsync()
    {
        // Guard against mixed TEXT datetime formats on Activity start columns. Current writers
        // (AppendFmtDt, EF parameters) emit "yyyy-MM-dd HH:mm:ss"; legacy rows may hold ISO
        // "yyyy-MM-ddTHH:mm:ss". TEXT compare is lexicographic and ' '(0x20) < 'T'(0x54), so a
        // same-day mixed pair sorts/filters wrongly in the EF LINQ date paths (StatisticsService,
        // ActivityQueryService). The LIKE '%T%' guard means clean corpora (Q6 census = 0 today)
        // touch zero rows — this is a guard against future bad writes, not a repair scan. Activity
        // is small, so the per-startup scan is negligible. GPS-wide normalization, if ever needed,
        // belongs in the maintenance action, not here.
        await _context.Database.ExecuteSqlRawAsync(
            "UPDATE Activity SET start_datetime_loc = replace(start_datetime_loc, 'T', ' ') " +
            "WHERE start_datetime_loc LIKE '%T%';");
        await _context.Database.ExecuteSqlRawAsync(
            "UPDATE Activity SET start_datetime_utc = replace(start_datetime_utc, 'T', ' ') " +
            "WHERE start_datetime_utc LIKE '%T%';");
    }

    private async Task AddColumnIfMissingAsync(string table, string column, string definition)
    {
        // Validate before interpolating: all callers pass hardcoded literals, but we
        // guard defensively so this can never become an injection vector if misused.
        if (!System.Text.RegularExpressions.Regex.IsMatch(table,  @"^\w+$", RegexOptions.None, RegexDefaults.MatchTimeout) ||
            !System.Text.RegularExpressions.Regex.IsMatch(column, @"^\w+$", RegexOptions.None, RegexDefaults.MatchTimeout))
        {
            throw new ArgumentException(
                $"Unsafe identifier in schema migration: table='{table}', column='{column}'");
        }

        // EF1002: table/column names cannot be SQL parameters — DDL requires interpolation.
        // Inputs are validated above to contain only word characters [A-Za-z0-9_].
#pragma warning disable EF1002
        var existing = await _context.Database
            .SqlQueryRaw<string>(
                $"SELECT name FROM pragma_table_info('{table}') WHERE name = '{column}'")
            .ToListAsync();

        if (existing.Count == 0)
        {
            await _context.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE [{table}] ADD COLUMN {column} {definition};");
        }
#pragma warning restore EF1002
    }
}
