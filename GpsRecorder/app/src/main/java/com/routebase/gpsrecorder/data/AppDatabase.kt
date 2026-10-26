package com.routebase.gpsrecorder.data

import android.content.Context
import androidx.room.Database
import androidx.room.Room
import androidx.room.RoomDatabase
import androidx.room.migration.Migration
import androidx.sqlite.db.SupportSQLiteDatabase

@Database(
    entities = [GpsPointEntity::class, PendingRecordingEntity::class, PendingAbandonEntity::class],
    version = 4,
    exportSchema = false
)
abstract class AppDatabase : RoomDatabase() {

    abstract fun gpsPointDao(): GpsPointDao

    abstract fun pendingRecordingDao(): PendingRecordingDao

    abstract fun pendingAbandonDao(): PendingAbandonDao

    companion object {
        @Volatile private var instance: AppDatabase? = null

        // v2 adds pressureHpa. ALTER (not destructive) so un-uploaded offline points survive the update.
        private val MIGRATION_1_2 = object : Migration(1, 2) {
            override fun migrate(db: SupportSQLiteDatabase) {
                db.execSQL("ALTER TABLE pending_points ADD COLUMN pressureHpa REAL")
            }
        }

        // v3 scopes the upload queue per recording: points gain an owner id, and finished-but-undelivered
        // recordings get a table instead of a single prefs slot. Existing rows default to
        // LEGACY_RECORDING_ID and are adopted by LegacyQueueMigration — ALTER, never
        // destructive: a destructive migration here would silently delete a ride that never uploaded.
        // The DDL must match what Room generates for the entities (column types/nullability, index name),
        // or schema validation throws on the first open after an upgrade.
        private val MIGRATION_2_3 = object : Migration(2, 3) {
            override fun migrate(db: SupportSQLiteDatabase) {
                db.execSQL(
                    "ALTER TABLE pending_points ADD COLUMN recordingId INTEGER NOT NULL DEFAULT 0"
                )
                db.execSQL(
                    "CREATE INDEX IF NOT EXISTS index_pending_points_recordingId " +
                        "ON pending_points (recordingId)"
                )
                db.execSQL(
                    """
                    CREATE TABLE IF NOT EXISTS pending_recordings (
                        recordingId INTEGER NOT NULL,
                        sessionId TEXT,
                        clientSequence INTEGER NOT NULL,
                        activityType TEXT NOT NULL,
                        visibility TEXT NOT NULL,
                        clientVersion TEXT NOT NULL,
                        tzOffsetMinutes INTEGER NOT NULL,
                        autoDetected INTEGER NOT NULL,
                        parkedAtMs INTEGER NOT NULL,
                        PRIMARY KEY(recordingId)
                    )
                    """.trimIndent()
                )
            }
        }

        // v4 (AND-06): sessions whose Discard-time abandon call failed offline get a table instead of being
        // dropped — the next flush pass retries the abandon so a discarded ride can't resurface via the
        // next start's CLOSE_STALE path (which finalizes, rather than abandons, whatever it finds active).
        private val MIGRATION_3_4 = object : Migration(3, 4) {
            override fun migrate(db: SupportSQLiteDatabase) {
                db.execSQL(
                    """
                    CREATE TABLE IF NOT EXISTS pending_abandons (
                        sessionId TEXT NOT NULL,
                        requestedAtMs INTEGER NOT NULL,
                        PRIMARY KEY(sessionId)
                    )
                    """.trimIndent()
                )
            }
        }

        fun getInstance(context: Context): AppDatabase =
            instance ?: synchronized(this) {
                instance ?: Room.databaseBuilder(
                    context.applicationContext,
                    AppDatabase::class.java,
                    "gps_recorder.db"
                ).addMigrations(MIGRATION_1_2, MIGRATION_2_3, MIGRATION_3_4).build().also { instance = it }
            }
    }
}
