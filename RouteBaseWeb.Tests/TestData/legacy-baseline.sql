-- Real live-DB schema dump (read-only snapshot, 2026-08-02) with every startup patch already applied.
-- This is the CURRENT production vintage, ALTER-appended column spellings included, and it is the only
-- place the legacy-vintage table shapes are exercised in tests -- an AddColumnIfMissing branch only runs
-- for real when the baseline's table predates the column.
-- Refreshed 2026-08-02 when the completed one-shot repairs were deleted: the previous dump (2026-07-24)
-- still carried the pre-step-1b ProfileSettings table, which nothing patches any more.
-- Refresh the same way after future one-shot migrations land on the live DB: dump sqlite_master sql for
-- the app tables, then the app indexes. No retired-store tables (GPS, Track_Blob, Pictures, ...) belong
-- here -- their patchers are deleted, so a baseline carrying them would diverge for real.
-- Editor note: ExecuteScriptAsync splits this file blindly on the statement separator character, so
-- comments must never contain one.

CREATE TABLE "Activity" (
    "track_ID" INTEGER NOT NULL CONSTRAINT "PK_Activity" PRIMARY KEY AUTOINCREMENT,
    "api_ID" INTEGER NULL,
    "track_name" TEXT NULL,
    "activity_type" TEXT NULL,
    "start_datetime_loc" TEXT NULL,
    "start_datetime_utc" TEXT NULL,
    "datetime_timezone" REAL NULL,
    "start_gps_lat" REAL NULL,
    "end_gps_lat" REAL NULL,
    "start_gps_long" REAL NULL,
    "end_gps_long" REAL NULL,
    "time_total" REAL NULL,
    "time_elapsed" REAL NULL,
    "time_moving" REAL NULL,
    "total_distance" REAL NULL,
    "total_ascent" REAL NULL,
    "total_descent" REAL NULL,
    "speed_avg" REAL NULL,
    "speed_max" REAL NULL,
    "heart_rate_avg" REAL NULL,
    "heart_rate_max" REAL NULL,
    "cadence_avg" REAL NULL,
    "cadence_max" REAL NULL,
    "power_avg" REAL NULL,
    "power_max" REAL NULL,
    "power_20min" REAL NULL,
    "friends" TEXT NULL,
    "comments" TEXT NULL,
    "calories" REAL NULL,
    "source" TEXT NULL,
    "multi_day_ID" INTEGER NULL,
    "user_id" TEXT NULL,
    "visibility" TEXT NOT NULL DEFAULT 'friends', lifecycle TEXT NOT NULL DEFAULT 'released', lifecycle_reason TEXT, lifecycle_prev TEXT, lifecycle_at TEXT, superseded_by INTEGER REFERENCES Activity(track_ID) ON DELETE SET NULL, edited_commit_id INTEGER REFERENCES Track_Edit_Commit(commit_id) ON DELETE SET NULL, consumed_commit_id INTEGER REFERENCES Track_Edit_Commit(commit_id) ON DELETE SET NULL, in_edit_session_id TEXT REFERENCES Activity_Edit_Session(session_id) ON DELETE SET NULL,
    CONSTRAINT "FK_Activity_Multi_Day_Tour_multi_day_ID" FOREIGN KEY ("multi_day_ID") REFERENCES "Multi_Day_Tour" ("multi_day_ID") ON DELETE SET NULL,
    CONSTRAINT "FK_Activity_Users_user_id" FOREIGN KEY ("user_id") REFERENCES "Users" ("user_id")
);
CREATE TABLE Activity_Bbox (
                track_ID  INTEGER PRIMARY KEY REFERENCES Activity(track_ID) ON DELETE CASCADE,
                min_lat   REAL NOT NULL,
                max_lat   REAL NOT NULL,
                min_long  REAL NOT NULL,
                max_long  REAL NOT NULL
            );
CREATE TABLE Activity_Edit_Session (
                session_id   TEXT PRIMARY KEY,
                user_id      TEXT NOT NULL,
                status       TEXT NOT NULL DEFAULT 'active',
                script_json  TEXT NOT NULL,
                cursor       INTEGER NOT NULL DEFAULT 0,
                stamps_json  TEXT NOT NULL DEFAULT '[]',
                created_at   TEXT NOT NULL,
                updated_at   TEXT NOT NULL
            , edit_count INTEGER NOT NULL DEFAULT 0);
CREATE TABLE Activity_Lifecycle_Log (
                log_id     INTEGER PRIMARY KEY AUTOINCREMENT,
                track_id   INTEGER NOT NULL REFERENCES Activity(track_ID) ON DELETE CASCADE,
                user_id    TEXT,
                from_state TEXT,
                to_state   TEXT NOT NULL,
                reason     TEXT,
                commit_id  INTEGER REFERENCES Track_Edit_Commit(commit_id) ON DELETE SET NULL,
                at         TEXT NOT NULL
            );
CREATE TABLE Activity_Version (
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
CREATE TABLE "AppCredentials" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_AppCredentials" PRIMARY KEY AUTOINCREMENT,
    "Type" TEXT NOT NULL,
    "AccessToken" TEXT NULL,
    "RefreshToken" TEXT NULL,
    "ExpiresAt" TEXT NULL,
    "AthleteId" INTEGER NULL,
    "ClientId" TEXT NULL,
    "ClientSecret" TEXT NULL,
    "ApiKey" TEXT NULL,
    "user_id" TEXT NULL,
    "CreatedAt" TEXT NOT NULL,
    "UpdatedAt" TEXT NOT NULL,
    CONSTRAINT "FK_AppCredentials_Users_user_id" FOREIGN KEY ("user_id") REFERENCES "Users" ("user_id")
);
CREATE TABLE Dem_Tiles (
                z          INTEGER NOT NULL,
                x          INTEGER NOT NULL,
                y          INTEGER NOT NULL,
                png        BLOB    NOT NULL,
                fetched_at TEXT    NOT NULL,
                PRIMARY KEY (z, x, y)
            ) WITHOUT ROWID;
CREATE TABLE Error_Log (
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
            );
CREATE TABLE "Friendship" (
    "friendship_id" INTEGER NOT NULL CONSTRAINT "PK_Friendship" PRIMARY KEY AUTOINCREMENT,
    "requester_id" TEXT NOT NULL,
    "addressee_id" TEXT NOT NULL,
    "status" TEXT NOT NULL,
    "created_at" TEXT NOT NULL,
    "updated_at" TEXT NOT NULL,
    CONSTRAINT "FK_Friendship_Users_addressee_id" FOREIGN KEY ("addressee_id") REFERENCES "Users" ("user_id") ON DELETE CASCADE,
    CONSTRAINT "FK_Friendship_Users_requester_id" FOREIGN KEY ("requester_id") REFERENCES "Users" ("user_id") ON DELETE CASCADE
);
CREATE TABLE "GPSinSegment" (
    "GPSinSegment_ID" INTEGER NOT NULL CONSTRAINT "PK_GPSinSegment" PRIMARY KEY AUTOINCREMENT,
    "track_id" INTEGER NOT NULL,
    "segment_id" INTEGER NOT NULL,
    "seg_gps_count" INTEGER NULL,
    "seg_total_count" INTEGER NULL,
    "seg_perc" INTEGER NULL,
    "gps_id_entry_guessed" INTEGER NULL,
    "gps_id_exit_guessed" INTEGER NULL,
    "gps_id_count" INTEGER NULL,
    "Offset_entry" REAL NULL,
    "gps_id_entry_calculated" INTEGER NULL,
    "Offset_exit" REAL NULL,
    "gps_id_exit_calculated" INTEGER NULL, effort_distance REAL, effort_elevation REAL, effort_time REAL, effort_speed_avg REAL, effort_hr_avg REAL, effort_power_avg REAL, effort_cadence_avg REAL,
    CONSTRAINT "FK_GPSinSegment_Activity_track_id" FOREIGN KEY ("track_id") REFERENCES "Activity" ("track_ID") ON DELETE CASCADE,
    CONSTRAINT "FK_GPSinSegment_Segment_segment_id" FOREIGN KEY ("segment_id") REFERENCES "Segment" ("segment_ID") ON DELETE CASCADE
);
CREATE TABLE Heatmap (
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
CREATE TABLE "Multi_Day_Tour" (
    "multi_day_ID" INTEGER NOT NULL CONSTRAINT "PK_Multi_Day_Tour" PRIMARY KEY AUTOINCREMENT,
    "multi_day_tour_name" TEXT NULL,
    "multi_day_description" TEXT NULL,
    "start_date" TEXT NULL,
    "end_date" TEXT NULL,
    "total_distance" REAL NULL,
    "total_ascent" REAL NULL,
    "total_descent" REAL NULL,
    "total_time" REAL NULL,
    "total_days" INTEGER NULL,
    "total_activities" INTEGER NULL,
    "user_id" TEXT NULL,
    CONSTRAINT "FK_Multi_Day_Tour_Users_user_id" FOREIGN KEY ("user_id") REFERENCES "Users" ("user_id")
);
CREATE TABLE Photos (
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
            , merged_from_track_id INTEGER REFERENCES Activity(track_ID) ON DELETE SET NULL);
CREATE TABLE "ProfileSettings" (
                Id           INTEGER PRIMARY KEY AUTOINCREMENT,
                Category     TEXT NOT NULL,
                SettingKey   TEXT NOT NULL,
                SettingValue TEXT NOT NULL,
                CreatedAt    TEXT NOT NULL,
                UpdatedAt    TEXT NOT NULL,
                UserId       TEXT NOT NULL
            );
CREATE TABLE Race (
                race_ID    INTEGER PRIMARY KEY AUTOINCREMENT,
                race_name  TEXT,
                race_date  TEXT,
                track_id   INTEGER NOT NULL REFERENCES Activity(track_ID) ON DELETE CASCADE,
                user_id    TEXT,
                visibility TEXT NOT NULL DEFAULT 'friends',
                notes      TEXT,
                created_at TEXT
            , reference_track_id INTEGER REFERENCES Activity(track_ID) ON DELETE SET NULL, edit_policy TEXT NOT NULL DEFAULT 'owner');
CREATE TABLE Race_Checkpoint (
                checkpoint_ID   INTEGER PRIMARY KEY AUTOINCREMENT,
                race_ID         INTEGER NOT NULL REFERENCES Race(race_ID) ON DELETE CASCADE,
                seq             INTEGER NOT NULL,
                checkpoint_name TEXT,
                chainage_m      REAL NOT NULL,
                leg_name TEXT, leg_color TEXT);
CREATE TABLE Race_Participant (
                participant_ID INTEGER PRIMARY KEY AUTOINCREMENT,
                race_ID        INTEGER NOT NULL REFERENCES Race(race_ID) ON DELETE CASCADE,
                kind           TEXT NOT NULL DEFAULT 'simulated',
                display_name   TEXT,
                bib            TEXT,
                category       TEXT,
                gender         TEXT,
                club           TEXT,
                track_id       INTEGER REFERENCES Activity(track_ID) ON DELETE CASCADE,
                seed           INTEGER NOT NULL DEFAULT 0
            , official_finish_seconds REAL, color TEXT);
CREATE TABLE Race_Split_Time (
                participant_ID  INTEGER NOT NULL REFERENCES Race_Participant(participant_ID) ON DELETE CASCADE,
                checkpoint_ID   INTEGER NOT NULL REFERENCES Race_Checkpoint(checkpoint_ID) ON DELETE CASCADE,
                elapsed_seconds REAL NOT NULL,
                PRIMARY KEY (participant_ID, checkpoint_ID)
            );
CREATE TABLE RecordingSession (
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
                visibility      TEXT NOT NULL DEFAULT 'friends',
                client_version  TEXT
            , timezone_offset_minutes INTEGER);
CREATE TABLE "Recording_Staging" (
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
            ) WITHOUT ROWID;
CREATE TABLE "Segment" (
    "segment_ID" INTEGER NOT NULL CONSTRAINT "PK_Segment" PRIMARY KEY AUTOINCREMENT,
    "segment_name" TEXT NULL,
    "segment_distance" REAL NULL,
    "segment_ascent" INTEGER NULL,
    "segment_descent" REAL NULL,
    "activity_type" TEXT NULL,
    "user_id" TEXT NULL,
    "visibility" TEXT NOT NULL DEFAULT 'friends', segment_kind TEXT DEFAULT 'polyline', shape_match_mode TEXT DEFAULT 'whole', match_owner_ids TEXT, created_at TEXT, match_rules_json TEXT,
    CONSTRAINT "FK_Segment_Users_user_id" FOREIGN KEY ("user_id") REFERENCES "Users" ("user_id")
);
CREATE TABLE SegmentEffortExclusion (
                exclusion_id  INTEGER PRIMARY KEY AUTOINCREMENT,
                segment_id    INTEGER NOT NULL,
                track_id      INTEGER NOT NULL,
                entry_index   INTEGER NOT NULL,
                excluded_at   TEXT NOT NULL
            );
CREATE TABLE "Segment_GPS" (
    "seg_GPS_ID" INTEGER NOT NULL CONSTRAINT "PK_Segment_GPS" PRIMARY KEY AUTOINCREMENT,
    "segment_ID" INTEGER NOT NULL,
    "GPS_lat" REAL NULL,
    "GPS_long" REAL NULL,
    "altitude" INTEGER NULL,
    CONSTRAINT "FK_Segment_GPS_Segment_segment_ID" FOREIGN KEY ("segment_ID") REFERENCES "Segment" ("segment_ID") ON DELETE CASCADE
);
CREATE TABLE Share_Link (
                share_link_ID INTEGER PRIMARY KEY AUTOINCREMENT,
                token_hash    TEXT NOT NULL,
                kind          TEXT NOT NULL,
                track_id      INTEGER REFERENCES Activity(track_ID) ON DELETE CASCADE,
                segment_id    INTEGER REFERENCES Segment(segment_ID) ON DELETE CASCADE,
                user_id       TEXT NOT NULL,
                created_at    TEXT NOT NULL,
                expires_at    TEXT NOT NULL,
                revoked_at    TEXT
            , race_id INTEGER REFERENCES Race(race_ID) ON DELETE CASCADE, open_count INTEGER NOT NULL DEFAULT 0, last_opened_at TEXT);
CREATE TABLE Track_Edit_Commit (
                commit_id     INTEGER PRIMARY KEY AUTOINCREMENT,
                user_id       TEXT NOT NULL,
                committed_at  TEXT NOT NULL,
                save_mode     TEXT NOT NULL,
                session_id    TEXT,
                script_json   TEXT NOT NULL,
                reverted_at   TEXT
            );
CREATE TABLE Track_Object (
                sha256        TEXT PRIMARY KEY,
                codec_version INTEGER NOT NULL,
                point_count   INTEGER NOT NULL,
                byte_len      INTEGER NOT NULL,
                data          BLOB    NOT NULL,
                created_at    TEXT    NOT NULL
            );
CREATE TABLE "Users" (
    "user_id" TEXT NOT NULL CONSTRAINT "PK_Users" PRIMARY KEY,
    "display_name" TEXT NULL,
    "email" TEXT NULL,
    "is_admin" INTEGER NOT NULL,
    "created_at" TEXT NOT NULL,
    "last_login" TEXT NOT NULL
);
CREATE TABLE Version_Parent (
                version_id        INTEGER NOT NULL REFERENCES Activity_Version(version_id) ON DELETE CASCADE,
                parent_version_id INTEGER NOT NULL,
                PRIMARY KEY (version_id, parent_version_id)
            ) WITHOUT ROWID;
CREATE UNIQUE INDEX IX_ActivityBbox_TrackId ON Activity_Bbox (track_ID);
CREATE INDEX IX_ActivityEditSession_UserId ON Activity_Edit_Session (user_id, status);
CREATE INDEX IX_ActivityLifecycleLog_TrackId
                ON Activity_Lifecycle_Log (track_id, at);
CREATE INDEX IX_ActivityVersion_Commit ON Activity_Version (commit_id);
CREATE UNIQUE INDEX IX_ActivityVersion_Head
                ON Activity_Version (track_id) WHERE is_head = 1;
CREATE INDEX IX_ActivityVersion_Object ON Activity_Version (object_sha);
CREATE INDEX IX_ActivityVersion_Track  ON Activity_Version (track_id, seq);
CREATE INDEX IX_Activity_Lifecycle          ON Activity (lifecycle);
CREATE INDEX IX_Activity_Lifecycle_Log_commit_id
                ON Activity_Lifecycle_Log (commit_id);
CREATE INDEX IX_Activity_StartDate  ON Activity (start_datetime_utc DESC);
CREATE INDEX IX_Activity_StartDateLoc ON Activity (start_datetime_loc DESC);
CREATE INDEX "IX_Activity_UserId" ON "Activity" ("user_id");
CREATE INDEX "IX_Activity_UserId_StartDate" ON "Activity" ("user_id", "start_datetime_utc");
CREATE INDEX "IX_Activity_Visibility" ON "Activity" ("visibility");
CREATE INDEX IX_Activity_consumed_commit_id ON Activity (consumed_commit_id);
CREATE INDEX IX_Activity_edited_commit_id   ON Activity (edited_commit_id);
CREATE INDEX IX_Activity_in_edit_session_id ON Activity (in_edit_session_id);
CREATE INDEX "IX_Activity_multi_day_ID" ON "Activity" ("multi_day_ID");
CREATE INDEX IX_Activity_superseded_by      ON Activity (superseded_by);
CREATE INDEX "IX_AppCredentials_Type" ON "AppCredentials" ("Type");
CREATE INDEX "IX_AppCredentials_UserId_Type" ON "AppCredentials" ("user_id", "Type");
CREATE INDEX "IX_Friendship_Addressee" ON "Friendship" ("addressee_id", "status");
CREATE INDEX "IX_Friendship_Requester" ON "Friendship" ("requester_id", "status");
CREATE UNIQUE INDEX "IX_Friendship_requester_id_addressee_id" ON "Friendship" ("requester_id", "addressee_id");
CREATE INDEX "IX_GpsInSegment_SegmentId" ON "GPSinSegment" ("segment_id");
CREATE INDEX "IX_GpsInSegment_TrackId" ON "GPSinSegment" ("track_id");
CREATE UNIQUE INDEX IX_Heatmap_SegmentId ON Heatmap (segment_ID);
CREATE INDEX "IX_MultiDayTour_UserId" ON "Multi_Day_Tour" ("user_id");
CREATE INDEX IX_Photo_TrackId            ON Photos (track_id);
CREATE INDEX IX_Photo_UserId             ON Photos (user_id);
CREATE INDEX IX_Photo_UserId_CapturedUtc ON Photos (user_id, captured_utc);
CREATE INDEX IX_Photo_UserId_ContentHash ON Photos (user_id, content_hash);
CREATE INDEX IX_Photos_merged_from_track_id ON Photos (merged_from_track_id);
CREATE UNIQUE INDEX IX_ProfileSettings_Category_SettingKey_UserId ON ProfileSettings (Category, SettingKey, UserId);
CREATE UNIQUE INDEX IX_RaceCheckpoint_RaceSeq ON Race_Checkpoint (race_ID, seq);
CREATE INDEX IX_RaceParticipant_RaceId  ON Race_Participant (race_ID);
CREATE INDEX IX_RaceParticipant_TrackId ON Race_Participant (track_id);
CREATE INDEX IX_RaceSplitTime_CheckpointId ON Race_Split_Time (checkpoint_ID);
CREATE INDEX IX_Race_TrackId ON Race (track_id);
CREATE INDEX IX_Race_UserId  ON Race (user_id);
CREATE INDEX IX_Race_reference_track_id ON Race (reference_track_id);
CREATE UNIQUE INDEX IX_RecordingSession_ActiveUser
                ON RecordingSession(user_id) WHERE status IN ('recording','paused');
CREATE INDEX IX_RecordingSession_Status ON RecordingSession(status);
CREATE INDEX IX_RecordingSession_TrackId ON RecordingSession (track_id);
CREATE INDEX IX_RecordingSession_UserId ON RecordingSession(user_id);
CREATE INDEX IX_SegmentEffortExclusion_SegmentTrack
                ON SegmentEffortExclusion (segment_id, track_id);
CREATE INDEX "IX_SegmentGps_SegmentId" ON "Segment_GPS" ("segment_ID");
CREATE INDEX "IX_Segment_UserId" ON "Segment" ("user_id");
CREATE INDEX "IX_Segment_Visibility" ON "Segment" ("visibility");
CREATE INDEX IX_ShareLink_ExpiresAt ON Share_Link (expires_at);
CREATE INDEX IX_ShareLink_RaceId ON Share_Link (race_id);
CREATE INDEX IX_ShareLink_SegmentId ON Share_Link (segment_id);
CREATE UNIQUE INDEX IX_ShareLink_TokenHash ON Share_Link (token_hash);
CREATE INDEX IX_ShareLink_TrackId   ON Share_Link (track_id);
CREATE INDEX IX_ShareLink_UserId    ON Share_Link (user_id);
CREATE INDEX IX_TrackEditCommit_UserId ON Track_Edit_Commit (user_id, committed_at);
CREATE INDEX IX_VersionParent_Parent ON Version_Parent (parent_version_id);
