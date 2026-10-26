package com.routebase.gpsrecorder.data

import android.util.Log
import com.routebase.gpsrecorder.util.EventLog
import retrofit2.HttpException

private const val TAG = "QueueDrainer"

/**
 * The upload queue: delivering FINISHED-but-undelivered recordings to the server. Extracted verbatim from
 * [RecordingRepository] (2026-07-31). It depends only on
 * [api], [dao], [pendingDao], the shared [PointDelivery], and the injected [busyCapturing] predicate; it
 * never reads prefs and holds no db handle, which is exactly why it is the cleanest unit to cut out.
 *
 * Every method here is `*Locked`: the caller must hold [RecordingRepository]'s `uploadMutex`, and nothing
 * here takes it (same contract as [PointDelivery]). That single mutex is owned by the repository — because
 * the LIVE upload path locks it too — and the whole correctness story is that the live upload loop must
 * never interleave with a queue drain. The drainer runs INSIDE that one lock rather than owning its own, so
 * live and queue can never split across two mutexes. See the `uploadMutex` docstring in [RecordingRepository].
 */
internal class QueueDrainer(
    private val api: ApiService,
    private val dao: GpsPointDao,
    private val pendingDao: PendingRecordingDao,
    private val delivery: PointDelivery,
    private val busyCapturing: () -> Boolean
) {

    /**
     * Delivers every parked recording: create its session if it never got one, append its points, stop it.
     *
     * Runs only when nothing is capturing — the caller ([RecordingRepository.flushQueueAsync]) short-circuits
     * on `busyCapturing()` before taking the lock, and each re-check below yields the instant a recording
     * starts. The server allows ONE active session per user, so creating a queued session mid-recording would
     * 409 — and the 409 branch adopts the active session, which would append the queued ride's points to the
     * LIVE one. Waiting costs nothing: the queue is drained the moment the ride ends, on any network, by
     * UploadWorker.
     *
     * Assumes `uploadMutex` is held.
     */
    internal suspend fun flushLocked() {
        // Recordings that already own a session go first: stopping them frees the server's single
        // active-session slot, so the sessionless ones below can be created without a 409.
        for (row in pendingDao.getWithSession()) {
            if (busyCapturing()) return            // a recording started — it owns the pipe now
            deliverLocked(row)                     // one poisoned row must not block the others
        }
        for (row in pendingDao.getWithoutSession()) {
            if (busyCapturing()) return
            // Serialized by the server's one-active-session rule: if this one can't be created and
            // stopped, the next would only 409 behind it.
            if (!deliverLocked(row)) return
        }
    }

    /**
     * Returns true when the recording was fully delivered and removed from the queue.
     *
     * [abortIf] normally yields to a recording that starts mid-drain; the conflict path passes `{ false }`
     * because there it must finish *despite* a live recording — that recording is blocked until it does.
     */
    internal suspend fun deliverLocked(
        row: PendingRecordingEntity,
        abortIf: () -> Boolean = busyCapturing
    ): Boolean {
        val sessionId = row.sessionId ?: createQueuedSessionLocked(row) ?: return false

        // A parked session may be paused (an auto grace-pause that never got resumed). A paused session
        // 404s appends, which the drain below would misread as "session gone" and discard the points.
        runCatching { api.resumeSession(sessionId) }
            .onFailure { if (it is kotlinx.coroutines.CancellationException) throw it } // AND-40

        val drained = try {
            delivery.drainLocked(
                row.recordingId,
                sessionId,
                QueueSeq(pendingDao, row.recordingId, row.clientSequence),
                abortIf
            )
        } catch (e: HttpException) {
            if (e.code() == 404) {
                // The session no longer accepts appends: the server's sweep abandoned it, or our stop DID
                // land and we never saw the response. Either way these points were never stored — and they
                // must never be attached to a different ride. Detach the row from the dead session so the
                // next pass gives the remainder a session of its own: a split activity, but no lost GPS.
                // (Sequence resets with it — a fresh session accepts nothing below 1.)
                val stranded = dao.countFor(row.recordingId)
                Log.w(TAG, "Queued session $sessionId won't take appends (404); re-creating for $stranded point(s)")
                EventLog.log("Queue: session no longer accepts points — the rest will upload as its own activity")
                pendingDao.detachSession(row.recordingId)
                return false
            }
            Log.w(TAG, "Queue drain failed for ${row.recordingId} (will retry): ${e.message}")
            return false
        } catch (e: kotlinx.coroutines.CancellationException) {
            // Kotlinx CE extends IllegalStateException, so the generic catch below would swallow it: a
            // cancelled flush then kept iterating rows, logging phantom "drain failed" lines (AND-27).
            throw e
        } catch (e: Exception) {
            Log.w(TAG, "Queue drain failed for ${row.recordingId} (will retry): ${e.message}")
            return false
        }
        if (!drained) return false

        try {
            api.stopSession(sessionId)
        } catch (e: HttpException) {
            if (e.code() != 404) {   // 404 = already stopped; anything else, retry the stop later
                Log.w(TAG, "Queued stop failed for ${row.recordingId} (will retry): ${e.message}")
                return false
            }
        } catch (e: kotlinx.coroutines.CancellationException) {
            throw e // AND-27
        } catch (e: Exception) {
            Log.w(TAG, "Queued stop failed for ${row.recordingId} (will retry): ${e.message}")
            return false
        }

        removeRowLocked(row.recordingId)
        EventLog.log("Queue: delivered a pending recording")
        return true
    }

    /**
     * Creates the session for a queued recording that ran entirely offline. A 409 (another session is
     * active) must NEVER adopt that session the way the live path does — adopting it here is precisely
     * how one ride's points get appended to another ride's activity.
     */
    private suspend fun createQueuedSessionLocked(row: PendingRecordingEntity): String? {
        return try {
            val params = row.toParams()
            val response = api.startSession(
                StartSessionRequest(params.activityType, params.visibility, params.clientVersion,
                    params.tzOffset, params.autoDetected)
            )
            pendingDao.setSessionId(row.recordingId, response.sessionId)
            Log.i(TAG, "Queued recording ${row.recordingId} → session ${response.sessionId}")
            response.sessionId
        } catch (e: HttpException) {
            if (e.code() == 409) {
                Log.w(TAG, "Can't create queued session yet — another session is active on the server")
            } else {
                Log.w(TAG, "Queued session creation failed (will retry): ${e.message}")
            }
            null
        } catch (e: kotlinx.coroutines.CancellationException) {
            throw e // AND-27
        } catch (e: Exception) {
            Log.w(TAG, "Queued session creation failed (will retry): ${e.message}")
            null
        }
    }

    private suspend fun removeRowLocked(recordingId: Long) {
        dao.deleteFor(recordingId)
        pendingDao.delete(recordingId)
    }

    /**
     * Startup reaper for points orphaned by the park zero-point race: a straggling `enqueuePoints` (which
     * takes no `uploadMutex`) can land right after [RecordingRepository.parkCurrentRecordingAsync] counted
     * zero and cleared prefs, leaving rows that no queue row names and that [GpsPointDao.countQueued] /
     * deleteQueuedPoints can never see. [currentRecordingId] (the live recording, read from prefs by the
     * repository under the same lock and handed down) and the queue are both excluded, so it can only ever
     * hit truly dead rows.
     */
    internal suspend fun sweepLocked(currentRecordingId: Long) {
        // AND-42: this runs BLOCKING on the main thread at process start (GpsRecorderApp.onCreate), and
        // its arming flag (orphanSweepPending) is set on EVERY recording-pointer drop while an actual
        // orphan — a straggling enqueuePoints after park cleared prefs — is rare. So the common armed
        // case reaps nothing. A COUNT (read, no write lock) first keeps that case off the write path:
        // only pay for deleteOrphans' transaction when there is really something to reap. Same lock, so
        // no new race — a row appearing between the count and a skipped delete is swept next launch.
        if (dao.countOrphans(currentRecordingId) == 0) return
        val removed = dao.deleteOrphans(currentRecordingId)
        if (removed > 0) {
            Log.w(TAG, "Swept $removed orphaned point(s) with no live or queued owner")
            EventLog.log("Swept $removed orphaned point(s) from a previous session")
        }
    }
}
