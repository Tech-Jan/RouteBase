package com.routebase.gpsrecorder.data

import com.routebase.gpsrecorder.util.ActivityType
import com.routebase.gpsrecorder.util.Visibility
import com.routebase.gpsrecorder.util.EventLog
import com.routebase.gpsrecorder.util.PrefsManager
import com.routebase.gpsrecorder.util.RecordingParams
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/**
 * One-time upgrade to the session-scoped upload queue (v13).
 *
 * Before v13 an undelivered recording lived in two prefs slots — `orphaned_session_id` (its stop failed)
 * or `deferred_session` (it never got a session) — and its points sat untagged in a shared table. This
 * adopts whatever those slots pointed at into `pending_recordings`, and stamps its points with a real
 * owner id, so an upgrade neither strands the recording nor lets it contaminate the next one.
 *
 * Runs from GpsRecorderApp.onCreate, before any service, receiver or activity in the process can look at
 * the recording state — which is also why it needs no lock: nothing else is running yet.
 */
class LegacyQueueMigration(
    private val dao: GpsPointDao,
    private val pendingDao: PendingRecordingDao,
    private val prefs: PrefsManager
) {

    suspend fun runAsync() = withContext(Dispatchers.IO) {
        if (prefs.queueMigrated) return@withContext

        val legacyPoints = dao.countFor(LEGACY_RECORDING_ID)
        val orphanSession = prefs.legacyOrphanedSessionId
        val activeSession = prefs.activeSessionId
        val deferredParams = prefs.legacyDeferredParams

        // Params matter only when a session must still be created (the deferred case carries its own);
        // otherwise this is a placeholder for a session that already exists.
        val fallbackParams = RecordingParams(
            prefs.currentActivityType ?: ActivityType.OTHER.wire, Visibility.PRIVATE.wire, "unknown", 0, prefs.currentAutoDetected
        )
        // A real id, not LEGACY_RECORDING_ID: afterwards, owner 0 must mean "unowned" and nothing else, or
        // "no recording is capturing" (currentRecordingId == 0) would name these very rows.
        val recordingId = System.currentTimeMillis()

        // An in-progress recording is adopted too, not resumed: installing the APK killed the service, so
        // nothing is capturing anyway, and its points deserve to reach the server as their own activity.
        val adopted = when {
            orphanSession != null -> PendingRecordingEntity.from(
                recordingId, orphanSession, prefs.legacyOrphanedClientSequence, fallbackParams)
            activeSession != null -> PendingRecordingEntity.from(
                recordingId, activeSession, prefs.clientSequence, fallbackParams)
            deferredParams != null -> PendingRecordingEntity.from(
                recordingId, null, prefs.clientSequence, deferredParams)
            else -> null
        }

        when {
            adopted != null -> {
                pendingDao.upsert(adopted)
                dao.reassignOwner(LEGACY_RECORDING_ID, recordingId)
                EventLog.log("Upgrade: adopted a previous recording into the upload queue ($legacyPoints pts)")
            }
            legacyPoints > 0 -> {
                // Points with neither a session nor params: nothing can ever deliver them to the right
                // activity. Before v13 they would have been swept into whichever session came next — the
                // contamination this release exists to remove.
                dao.deleteFor(LEGACY_RECORDING_ID)
                EventLog.log("Upgrade: discarded $legacyPoints undeliverable point(s)")
            }
        }

        prefs.clearLegacyQueueState()
        prefs.clearSession()          // the old current-recording state now lives in the queue row
        prefs.queueMigrated = true
    }
}
