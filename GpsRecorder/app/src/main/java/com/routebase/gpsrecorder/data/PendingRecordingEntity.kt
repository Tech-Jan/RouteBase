package com.routebase.gpsrecorder.data

import androidx.room.Entity
import androidx.room.PrimaryKey
import com.routebase.gpsrecorder.util.RecordingParams

/**
 * A finished recording whose points and/or stop call have not reached the server yet — one row per
 * recording, so a backlog is a *queue*, not a slot.
 *
 * This table replaces the old single-slot `orphaned_session_id` / `deferred_session` prefs pair, which
 * could only remember ONE undelivered recording: finishing two offline back-to-back silently overwrote
 * the first one's session id, and its points were then uploaded into the second one's session.
 *
 * A row is deleted only after every point is delivered AND the session is stopped, so a crash at any
 * point replays the whole delivery rather than losing it.
 */
@Entity(tableName = "pending_recordings")
data class PendingRecordingEntity(
    /** Local recording id — the same id stamped on this recording's [GpsPointEntity] rows. */
    @PrimaryKey val recordingId: Long,

    /** Server session, or null if the recording ran entirely offline and still needs one created. */
    val sessionId: String?,

    /** Last sequence the server accepted; the next append batch continues from here (server dedups <=). */
    val clientSequence: Int,

    // Start params — needed only when [sessionId] is null (the session must still be created).
    val activityType: String,
    val visibility: String,
    val clientVersion: String,
    val tzOffsetMinutes: Int,
    val autoDetected: Boolean,

    /** When the recording was parked (drain order + diagnostics). */
    val parkedAtMs: Long
) {
    fun toParams() = RecordingParams(activityType, visibility, clientVersion, tzOffsetMinutes, autoDetected)

    companion object {
        fun from(recordingId: Long, sessionId: String?, clientSequence: Int, params: RecordingParams) =
            PendingRecordingEntity(
                recordingId = recordingId,
                sessionId = sessionId,
                clientSequence = clientSequence,
                activityType = params.activityType,
                visibility = params.visibility,
                clientVersion = params.clientVersion,
                tzOffsetMinutes = params.tzOffset,
                autoDetected = params.autoDetected,
                parkedAtMs = System.currentTimeMillis()
            )
    }
}
