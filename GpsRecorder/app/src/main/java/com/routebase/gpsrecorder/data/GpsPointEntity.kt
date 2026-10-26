package com.routebase.gpsrecorder.data

import androidx.room.ColumnInfo
import androidx.room.Entity
import androidx.room.Index
import androidx.room.PrimaryKey

@Entity(
    tableName = "pending_points",
    indices = [Index("recordingId")]
)
data class GpsPointEntity(
    @PrimaryKey(autoGenerate = true) val id: Long = 0,
    /**
     * Which recording captured this point (local id — epoch-ms at session start; see
     * [com.routebase.gpsrecorder.util.PrefsManager.currentRecordingId]).
     *
     * The upload queue is shared by every recording, so rows MUST carry their owner: without it the
     * live upload loop drains the oldest rows in the table — which, after a recording whose stop never
     * reached the server, are the *previous* recording's undelivered tail, and they would be appended
     * to the *next* session (silent track contamination). Every read/delete is scoped by this id.
     *
     * [LEGACY_RECORDING_ID] on rows written before v13 (migration default); adopted once by
     * LegacyQueueMigration. The declared defaultValue must stay in sync with the
     * v2→v3 migration's ALTER, or Room's schema validation fails on every upgraded install.
     */
    @ColumnInfo(defaultValue = "0")
    val recordingId: Long = LEGACY_RECORDING_ID,
    val lat: Double,
    val lng: Double,
    val altitude: Double?,
    val speed: Double?,
    // Raw barometric pressure in hPa when the device has a barometer (null otherwise). Sent to the server
    // and stored in its own track channel for the offline elevation-fusion work.
    val pressureHpa: Double?,
    val timestampUtc: String
)

/** Owner id of rows captured before the queue was session-scoped (v13). Real ids are epoch-ms, never 0. */
const val LEGACY_RECORDING_ID = 0L
