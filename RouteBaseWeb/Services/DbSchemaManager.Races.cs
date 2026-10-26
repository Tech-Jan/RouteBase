using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services;

/// <summary>
/// Race-slice schema (course + checkpoints + participants + split times).
///
/// Fresh DBs get these from EF's OnModelCreating; this patches the legacy DB into the same shape.
/// DbSchemaParityTests enforces the two converge — keep the DDL below in lockstep with the
/// modelBuilder.Entity&lt;Race*&gt; blocks in RouteBaseDbContext.
/// </summary>
public partial class DbSchemaManager
{
    /// <inheritdoc />
    public async Task EnsureRaceTablesAsync()
    {
        await _context.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS Race (
                race_ID    INTEGER PRIMARY KEY AUTOINCREMENT,
                race_name  TEXT,
                race_date  TEXT,
                track_id   INTEGER NOT NULL REFERENCES Activity(track_ID) ON DELETE CASCADE,
                reference_track_id INTEGER REFERENCES Activity(track_ID) ON DELETE SET NULL,
                user_id    TEXT,
                visibility TEXT NOT NULL DEFAULT 'friends',
                edit_policy TEXT NOT NULL DEFAULT 'owner',
                notes      TEXT,
                created_at TEXT
            );
            CREATE INDEX IF NOT EXISTS IX_Race_UserId  ON Race (user_id);
            CREATE INDEX IF NOT EXISTS IX_Race_TrackId ON Race (track_id);

            CREATE TABLE IF NOT EXISTS Race_Checkpoint (
                checkpoint_ID   INTEGER PRIMARY KEY AUTOINCREMENT,
                race_ID         INTEGER NOT NULL REFERENCES Race(race_ID) ON DELETE CASCADE,
                seq             INTEGER NOT NULL,
                checkpoint_name TEXT,
                chainage_m      REAL NOT NULL,
                leg_name        TEXT,
                leg_color       TEXT
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_RaceCheckpoint_RaceSeq ON Race_Checkpoint (race_ID, seq);

            CREATE TABLE IF NOT EXISTS Race_Participant (
                participant_ID INTEGER PRIMARY KEY AUTOINCREMENT,
                race_ID        INTEGER NOT NULL REFERENCES Race(race_ID) ON DELETE CASCADE,
                kind           TEXT NOT NULL DEFAULT 'simulated',
                display_name   TEXT,
                bib            TEXT,
                category       TEXT,
                gender         TEXT,
                club           TEXT,
                track_id       INTEGER REFERENCES Activity(track_ID) ON DELETE CASCADE,
                seed           INTEGER NOT NULL DEFAULT 0,
                official_finish_seconds REAL,
                color          TEXT,
                finish_status     TEXT,
                finish_status_raw TEXT,
                start_offset_seconds REAL,
                wave              TEXT,
                discipline        TEXT,
                emphasised        INTEGER,
                callout_off       INTEGER
            );
            CREATE INDEX IF NOT EXISTS IX_RaceParticipant_RaceId  ON Race_Participant (race_ID);
            CREATE INDEX IF NOT EXISTS IX_RaceParticipant_TrackId ON Race_Participant (track_id);

            CREATE TABLE IF NOT EXISTS Race_Split_Time (
                participant_ID  INTEGER NOT NULL REFERENCES Race_Participant(participant_ID) ON DELETE CASCADE,
                checkpoint_ID   INTEGER NOT NULL REFERENCES Race_Checkpoint(checkpoint_ID) ON DELETE CASCADE,
                elapsed_seconds REAL NOT NULL,
                PRIMARY KEY (participant_ID, checkpoint_ID)
            );
            CREATE INDEX IF NOT EXISTS IX_RaceSplitTime_CheckpointId ON Race_Split_Time (checkpoint_ID);");

        // CREATE TABLE IF NOT EXISTS is a no-op on a table that already exists, so columns added after
        // the slice shipped need this second pass or a live DB never grows them.
        await AddColumnIfMissingAsync("Race", "reference_track_id", "INTEGER REFERENCES Activity(track_ID) ON DELETE SET NULL");
        await AddColumnIfMissingAsync("Race", "edit_policy", "TEXT NOT NULL DEFAULT 'owner'");

        // EF indexes every FK by convention, so the fresh schema has this and a patched legacy one would
        // not — DbSchemaParityTests fails on exactly that gap. It must run AFTER the ADD COLUMN above:
        // on a legacy Race table the column does not exist until then, and indexing a missing column throws.
        await _context.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_Race_reference_track_id ON Race (reference_track_id);");
        await AddColumnIfMissingAsync("Race_Checkpoint", "leg_name",  "TEXT");
        await AddColumnIfMissingAsync("Race_Checkpoint", "leg_color", "TEXT");
        await AddColumnIfMissingAsync("Race_Participant", "official_finish_seconds", "REAL");
        await AddColumnIfMissingAsync("Race_Participant", "color", "TEXT");
        // NULLABLE on purpose, and it is not a style choice: SQLite cannot tighten a column added by
        // ALTER, so a NOT NULL one would need a DbSchemaParityTests allow-list entry forever. Null also
        // carries real meaning here — "the file published no status" — see RaceParticipant.FinishStatus.
        await AddColumnIfMissingAsync("Race_Participant", "finish_status", "TEXT");
        await AddColumnIfMissingAsync("Race_Participant", "finish_status_raw", "TEXT");
        // Nullable for the same reason, and null MEANS 0 here: a NOT NULL DEFAULT 0 column would need a
        // DbSchemaParityTests allow-list entry (SQLite cannot tighten an ALTER-added column) and would buy
        // identical behaviour. See RaceParticipant.StartOffsetSeconds.
        await AddColumnIfMissingAsync("Race_Participant", "start_offset_seconds", "REAL");
        await AddColumnIfMissingAsync("Race_Participant", "wave", "TEXT");
        await AddColumnIfMissingAsync("Race_Participant", "discipline", "TEXT");
        // Nullable, and null MEANS false — third column on this table to make that trade. See
        // RaceParticipant.Emphasised.
        await AddColumnIfMissingAsync("Race_Participant", "emphasised", "INTEGER");
        // Same trade again, inverted: null means "called out as usual". See RaceParticipant.CalloutOff.
        await AddColumnIfMissingAsync("Race_Participant", "callout_off", "INTEGER");
    }
}
