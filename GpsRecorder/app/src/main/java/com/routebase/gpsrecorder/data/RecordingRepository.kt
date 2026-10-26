package com.routebase.gpsrecorder.data

import com.routebase.gpsrecorder.util.ActivityType
import com.routebase.gpsrecorder.util.SessionStatus
import com.routebase.gpsrecorder.util.Visibility
import android.util.Log
import com.routebase.gpsrecorder.util.EventLog
import com.routebase.gpsrecorder.util.RecordingParams
import com.routebase.gpsrecorder.util.RecordingPrefs
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import retrofit2.HttpException

private const val TAG = "RecordingRepository"

private const val STOP_ATTEMPTS = 3
// Phone-vs-server clock slack when deciding whether an active session predates the current recording.
private const val SESSION_AGE_SLACK_MS = 5 * 60_000L

/** Queue size for the UI: finished recordings still waiting to reach the server. */
data class QueueStatus(val recordings: Int, val points: Int) {
    val isEmpty: Boolean get() = recordings == 0
}

/**
 * Owns the local point queue and everything that talks to the recording API.
 *
 * Two lifecycles, deliberately separate:
 *
 *  - **The current recording** — capturing now. Its id/session/sequence live in [PrefsManager]; its
 *    points are the `pending_points` rows stamped with [PrefsManager.currentRecordingId].
 *  - **The upload queue** — recordings that are FINISHED but whose points and/or stop call have not
 *    reached the server (`pending_recordings` + their points). Delivered in the background by
 *    UploadWorker; never blocks a new recording from starting.
 *
 * A recording moves from the first to the second exactly once, in [parkCurrentRecordingAsync] — the
 * only place allowed to drop the phone's pointer to an undelivered recording.
 *
 * Every point read/delete is scoped by `recordingId`. Before v13 the queue was one shared FIFO with a
 * single orphan slot in prefs, which meant a stop that failed offline left its tail at the head of the
 * table for the NEXT recording's upload loop to append into the wrong session (and a second offline
 * stop overwrote the first one's session id entirely).
 */
class RecordingRepository(
    private val api: ApiService,
    private val dao: GpsPointDao,
    private val pendingDao: PendingRecordingDao,
    private val prefs: RecordingPrefs,
    private val pendingAbandonDao: PendingAbandonDao,
    // AND-23: runs the Discard mutation as one committed transaction across the three queue tables
    // (points, recordings, abandons). A seam, not the concrete AppDatabase, so this class no longer
    // depends on Room just for atomicity — and Discard's caller can be tested without a real database.
    private val transactor: Transactor,
    // ARCH-AND-B: "is a recording capturing (or the service coming up)?" — supplied from above, not
    // reached for from below. Production wires `{ prefs.isCapturing || GpsRecordingService.isServiceActive }`
    // (GpsRecorderApp), so the data layer no longer reaches UP into the service's process-global static.
    // That static was unfakeable, which is why the queue drain below could never be unit-tested.
    private val busyCapturing: () -> Boolean
) {

    /**
     * Serializes everything that reads or deletes queued points, so the live upload loop can never
     * interleave with a pause/stop flush, a queue drain, or a park — otherwise two writers reuse the
     * same clientSequence (duplicating a batch) or park a sequence that an in-flight batch has already
     * advanced (the next append is then dedup-eaten and its rows deleted unstored: silent loss).
     *
     * Not reentrant: `*Locked` helpers assume the caller holds it and must never re-take it.
     */
    private val uploadMutex = Mutex()

    /**
     * The append/acknowledge/delete mechanics, shared by the live path and the queue (see [PointDelivery]).
     * Every call into it must be made with [uploadMutex] held — it never takes the lock itself.
     */
    private val delivery = PointDelivery(api, dao)

    /**
     * The upload queue — delivering finished-but-undelivered recordings. A composed collaborator,
     * mirroring [delivery]: its methods are all `*Locked` and never take [uploadMutex], so the live path
     * (which this repository owns and which locks the same mutex) and the queue drain can never split across
     * two locks. Constructed here rather than injected so it shares this repository's one [delivery] instance
     * — the queued drain's append/`lastWatched` state is then the same object the live loop reads — and the
     * repository's constructor stays unchanged. Same collaborators the repository holds: [api], [dao],
     * [pendingDao], [delivery], [busyCapturing].
     */
    private val drainer = QueueDrainer(api, dao, pendingDao, delivery, busyCapturing)

    /**
     * Edge-trigger for the BACK_OFF log: tryCreateSession retries every upload tick for as long as the
     * foreign session is up, and one line per ride is the fingerprint we want — not one per minute.
     * Cleared whenever this recording gets a session, so a later block logs again.
     */
    @Volatile private var foreignSessionLogged = false

    /**
     * Whether the server said someone was on the live view, as of the last append it answered. The upload
     * loop reads this to pick its cadence (see GpsRecordingService.startUploadLoop). A hint, not state.
     */
    val lastWatched: Boolean get() = delivery.lastWatched

    /** The LIVE recording's sequence store: prefs. (A parked recording uses [QueueSeq] instead.) */
    private val prefsSeq: SeqStore = object : SeqStore {
        override suspend fun get(): Int = prefs.clientSequence
        override suspend fun set(seq: Int) { prefs.clientSequence = seq }
    }

    // ── Current recording: start / reconnect ────────────────────────────────

    /**
     * Starts a recording. The local id and params are claimed BEFORE the network call, so points captured
     * while the request is in flight — or after it fails offline — already have an owner. Throws if the
     * session could not be created; the caller keeps capturing regardless (see [tryCreateSession]).
     */
    suspend fun startSession(
        activityType: String,
        visibility: String,
        clientVersion: String,
        autoDetected: Boolean = false,
        shareLive: Boolean = false
    ): SessionResponse = withContext(Dispatchers.IO) {
        val tzOffsetMinutes = java.util.TimeZone.getDefault()
            .getOffset(System.currentTimeMillis()) / 60_000
        val params = RecordingParams(activityType, visibility, clientVersion, tzOffsetMinutes, autoDetected, shareLive)

        prefs.currentRecordingId = nextRecordingId()
        prefs.currentRecordingParams = params
        prefs.recordingStartTimeMs = System.currentTimeMillis()

        // Any failure here (offline, 409, server down) just means the session doesn't exist YET: capture
        // continues into the local queue and the upload loop's tryCreateSession — which owns the single
        // conflict policy — creates it within a tick. Nothing is lost by not special-casing it here.
        createCurrentSession(params)
    }

    /** Creates the current recording's session and points prefs at it. Throws on any API failure. */
    private suspend fun createCurrentSession(params: RecordingParams): SessionResponse {
        val owner = prefs.currentRecordingId
        val response = api.startSession(
            StartSessionRequest(params.activityType, params.visibility, params.clientVersion,
                params.tzOffset, params.autoDetected)
        )
        // A malformed 200 Gson-defaults to a blank sessionId; adopting it would wedge every append
        // on "sessions//points". Treat it as the API failure it is — the next tick retries.
        check(response.sessionId.isNotBlank()) { "startSession returned no sessionId" }
        // Belt to the service's startMutex braces: if a stop/clearSession raced this call while it was
        // in flight, writing the response now would strand activeSessionId with currentRecordingId=0 —
        // the next ride's reconnect branch then drops every point. The session just created is left for
        // the next start's 409 conflict policy to close; nothing local points at it.
        if (owner == 0L || prefs.currentRecordingId != owner) {
            Log.w(TAG, "Session ${response.sessionId} created for a recording that no longer exists — not adopting")
            return response
        }
        prefs.activeSessionId = response.sessionId
        // A fresh session has accepted nothing: the first batch must be sequence 1. (This also re-bases a
        // recording whose previous session vanished server-side — see reconnectSession.)
        prefs.clientSequence = 0
        foreignSessionLogged = false
        return response
    }

    /**
     * Epoch-ms, but never colliding with a recording already in the queue: the device clock can jump
     * backwards (NTP correction after boot), and a duplicate id would make the new recording's points
     * merge into a parked one — and the parked row be overwritten (REPLACE) at park time.
     */
    private suspend fun nextRecordingId(): Long {
        val now = System.currentTimeMillis()
        val highest = pendingDao.maxRecordingId() ?: 0L
        return if (now > highest) now else highest + 1
    }

    /**
     * Re-attaches the current recording to ITS OWN session after a process restart.
     *
     * Only ever adopts the session prefs already point at. The old code took whatever session the server
     * reported as active, which — now that finished-but-undelivered recordings keep their sessions open —
     * could be a *queued* recording's session, quietly appending the live ride into a finished activity.
     */
    suspend fun reconnectSession(): SessionResponse? = withContext(Dispatchers.IO) {
        val mine = prefs.activeSessionId ?: return@withContext null
        val session = api.getActiveSessions().firstOrNull { it.sessionId == mine }
        if (session == null) {
            // Our session is no longer active (the server's sweep finalized it while the app was dead).
            // It cannot take appends. Drop the pointer: the upload loop creates a fresh session and the
            // points we still hold — scoped to this recording — go there. A split activity, nothing lost.
            Log.w(TAG, "Session $mine is no longer active; continuing into a new one")
            EventLog.log("Session expired while the app was dead — continuing into a new one")
            prefs.activeSessionId = null
            return@withContext null
        }
        // AQ-04: resync to the server's accepted sequence. A previous stop-failure left the server session
        // recording at LastSequence=N; adopting it with a local clientSequence of 0 would make the new
        // uploads' first N batches dedup-eaten and then deleted. maxOf guards the restore case where the
        // local seq is already ahead of the server's.
        prefs.clientSequence = maxOf(prefs.clientSequence, session.lastSequence)
        if (prefs.recordingStartTimeMs == 0L) {
            prefs.recordingStartTimeMs = System.currentTimeMillis()
        }
        // Re-arm the idle-stop window so a mid-recording process restart can't instantly false-stop
        // before the movement anchor re-seeds on the next fix.
        prefs.lastMovedMs = System.currentTimeMillis()
        // AQ-08: a paused session accepts no appends (server 404s them) → the upload loop would wedge with
        // the UI still saying "recording". Prefer resume; if that fails, mark paused locally so the upload
        // loop skips instead of hammering 404s.
        if (session.status == SessionStatus.PAUSED.wire) {
            try { api.resumeSession(session.sessionId) }
            catch (e: kotlinx.coroutines.CancellationException) { throw e } // don't swallow cancellation (AND-40)
            catch (_: Exception) { prefs.sessionPaused = true }
        }
        session
    }

    /**
     * AND-22: the upload loop saw a run of append 404s and must decide whether the session is truly gone
     * or merely paused from the web (a paused session 404s appends just like a dead one). Probe the
     * active-session list — self-only, cheap — to tell them apart before the loop gives up on the session.
     */
    suspend fun classifyActiveSession(): SessionLiveness = withContext(Dispatchers.IO) {
        val mine = prefs.activeSessionId ?: return@withContext SessionLiveness.DEAD
        try {
            val session = api.getActiveSessions().firstOrNull { it.sessionId == mine }
            when {
                session == null -> SessionLiveness.DEAD          // gone from the active list → expired/abandoned
                session.status == SessionStatus.PAUSED.wire -> SessionLiveness.WEB_PAUSED
                // Present but recording (or an older server that omits status) → not dead, keep uploading.
                else -> SessionLiveness.ACTIVE
            }
        } catch (e: kotlinx.coroutines.CancellationException) {
            throw e
        } catch (e: Exception) {
            SessionLiveness.UNKNOWN                    // probe failed — do NOT declare the session dead on it
        }
    }

    /**
     * Creates the server session for a recording that has none yet (started offline, or its start failed).
     * Called by the upload loop each tick until it succeeds. Returns true when the current recording has a
     * session. This is the ONLY place that resolves a 409 — see [resolveSessionConflict].
     */
    suspend fun tryCreateSession(): Boolean = withContext(Dispatchers.IO) {
        if (prefs.isRecording) return@withContext true
        val params = prefs.currentRecordingParams ?: return@withContext false
        try {
            createCurrentSession(params)
            Log.i(TAG, "Session created for the recording in progress")
            true
        } catch (e: HttpException) {
            when (e.code()) {
                409 -> when (resolveSessionConflict()) {
                    // The active session was ours all along (a start whose response we never saw).
                    SessionConflict.ADOPTED -> true
                    // A finished recording was holding the slot; it has been delivered and stopped, so the
                    // slot is free now — create ours.
                    SessionConflict.CLEARED ->
                        runCatching { createCurrentSession(params); true }
                            .getOrElse { if (it is kotlinx.coroutines.CancellationException) throw it else false } // AND-40
                    SessionConflict.UNRESOLVED -> false
                }
                // The loop DOES re-invoke this every upload tick while needsSessionCreation holds — say so
                // (AND-28: the old "not retrying" line sent field debugging down the wrong path).
                401 -> { Log.e(TAG, "Auth failed (401) — will retry next tick; check the API key"); false }
                else -> false
            }
        } catch (e: kotlinx.coroutines.CancellationException) {
            throw e // prompt cancellation (AND-27) — a torn-down flush must not log phantom failures
        } catch (e: Exception) {
            Log.w(TAG, "Session creation failed (will retry): ${e.message}")
            false
        }
    }

    /**
     * AND-24: mint the public live-share (/s/{token}) for the CURRENT recording, if the user chose Share
     * live and it hasn't been minted yet. Best-effort and idempotent — the upload loop calls it each tick,
     * so an offline start still gets its link once connectivity returns and the session exists. A mint
     * failure is swallowed (retried next tick) and never affects capture or point upload. Only mints for the
     * active recording; the server refuses (409) a mint for any session that isn't recording/paused.
     */
    suspend fun ensureShareLinkAsync() = withContext(Dispatchers.IO) {
        val params = prefs.currentRecordingParams ?: return@withContext
        val sessionId = prefs.activeSessionId
        if (!ShareLivePolicy.shouldMint(params.shareLive, sessionId != null, prefs.activeShareUrl != null)) {
            return@withContext
        }
        try {
            val url = api.mintLiveShare(sessionId!!).url
            // Gson-defaulted blank (malformed 200) = failed mint; the catch below logs and the next
            // tick retries. Storing "" would satisfy shouldMint forever and surface an empty link.
            check(url.isNotBlank()) { "mint returned no url" }
            prefs.activeShareUrl = url
            EventLog.log("Live-share link ready")
        } catch (e: kotlinx.coroutines.CancellationException) {
            throw e
        } catch (e: Exception) {
            Log.w(TAG, "Live-share mint failed (will retry): ${e.message}")
        }
    }

    private enum class SessionConflict { ADOPTED, CLEARED, UNRESOLVED }

    /**
     * The server refuses a second active session, and we just hit that. [SessionConflictPolicy] owns the
     * rule; this method only carries it out. See that file for why an AUTO start must never close a session
     * it did not create.
     */
    private suspend fun resolveSessionConflict(): SessionConflict {
        // Blank-id filter: a malformed row Gson-defaults to sessionId="" (adopt seam guard) — such a
        // row must not be adopted, delivered against, or "closed" via POST sessions//stop.
        val active = runCatching { api.getActiveSessions().firstOrNull { it.sessionId.isNotBlank() } }
            .getOrElse { if (it is kotlinx.coroutines.CancellationException) throw it else null } // AND-40
            ?: return SessionConflict.UNRESOLVED

        val queued = pendingDao.findBySessionId(active.sessionId)
        val action = SessionConflictPolicy.decide(
            queuedOwnsIt = queued != null,
            startedBeforeUs = startedBefore(active, prefs.recordingStartTimeMs),
            autoDetected = prefs.currentAutoDetected
        )

        return when (action) {
            ConflictAction.DELIVER_QUEUED -> {
                // Deliver it right now even though we are capturing: this touches only the queued recording's
                // own points and session, and until it is stopped the live recording cannot get a session.
                Log.w(TAG, "A queued recording still holds the active session; delivering it to free the slot")
                EventLog.log("Finishing a previously queued recording so this one can start")
                val delivered = uploadMutex.withLock { drainer.deliverLocked(queued!!, abortIf = { false }) }
                if (delivered) SessionConflict.CLEARED else SessionConflict.UNRESOLVED
            }

            ConflictAction.CLOSE_STALE -> {
                Log.w(TAG, "Active session ${active.sessionId} predates this recording; closing it")
                EventLog.log("Closed a stale server session that was blocking this recording")
                runCatching { api.stopSession(active.sessionId) }
                    .fold(
                        { SessionConflict.CLEARED },
                        { if (it is kotlinx.coroutines.CancellationException) throw it; SessionConflict.UNRESOLVED }) // AND-40
            }

            ConflictAction.BACK_OFF -> {
                // An unattended auto start will not stop a session it cannot identify — it could be the
                // user's own live ride on another device, and stopping it also finalizes it. Keep capturing
                // locally; the queue delivers this recording as its own activity, and if the other session
                // ends, the next tick creates ours normally. Logged once, not on every upload tick.
                if (!foreignSessionLogged) {
                    foreignSessionLogged = true
                    Log.w(TAG, "Auto start blocked by older session ${active.sessionId}; leaving it alone")
                    EventLog.log("Another recording is already active — this one will upload as its own activity")
                }
                SessionConflict.UNRESOLVED
            }

            ConflictAction.ADOPT -> {
                prefs.activeSessionId = active.sessionId
                prefs.clientSequence = maxOf(prefs.clientSequence, active.lastSequence)   // AQ-04
                if (active.status == SessionStatus.PAUSED.wire)
                    runCatching { api.resumeSession(active.sessionId) }
                        .onFailure { if (it is kotlinx.coroutines.CancellationException) throw it } // AND-40
                Log.i(TAG, "Adopted our own in-flight session ${active.sessionId}")
                SessionConflict.ADOPTED
            }
        }
    }

    /**
     * Did [session] start meaningfully before this recording? The comparison crosses two clocks (the
     * server stamps startedAt, the phone stamps recordingStartTimeMs), so it is deliberately slack: a
     * false "no" merely adopts a session we would otherwise have closed, and a false "yes" costs one empty
     * session. Unparseable timestamp → treat as not-older, i.e. keep the old adopt behaviour.
     */
    private fun startedBefore(session: SessionResponse, recordingStartMs: Long): Boolean {
        if (recordingStartMs == 0L) return false
        val startedAtMs = ServerTimestamps.parseEpochMs(session.startedAt) ?: return false
        return startedAtMs < recordingStartMs - SESSION_AGE_SLACK_MS
    }

    // ── Current recording: points ───────────────────────────────────────────

    /** Persists a batch of captured points, stamped with the recording that owns them. */
    suspend fun enqueuePoints(points: List<GpsPointEntity>) = withContext(Dispatchers.IO) {
        val recordingId = prefs.currentRecordingId
        if (recordingId == 0L) {
            // Nothing owns these, so no drain could ever deliver them — and an unowned row is exactly
            // what used to get swept into the next session. Drop them loudly instead.
            Log.w(TAG, "Dropping ${points.size} point(s) captured with no active recording")
            return@withContext
        }
        dao.insertAll(points.map { it.copy(recordingId = recordingId) })
    }

    /** One batch from the current recording. Called on a timer by the service's upload loop. */
    suspend fun uploadPendingPoints(): Int = withContext(Dispatchers.IO) {
        uploadMutex.withLock {
            val sessionId = prefs.activeSessionId ?: return@withLock 0
            val recordingId = prefs.currentRecordingId
            if (recordingId == 0L) return@withLock 0
            delivery.uploadBatchLocked(recordingId, sessionId, prefsSeq)
        }
    }

    // ── Current recording: pause / resume / stop ────────────────────────────

    suspend fun pauseSession() = withContext(Dispatchers.IO) {
        val sessionId = prefs.activeSessionId ?: return@withContext  // offline — local-only pause
        val recordingId = prefs.currentRecordingId
        // Flush before pausing so a paused session isn't sitting on undelivered points (AQ-03 paced).
        // Undelivered points are not lost on pause — they wait for resume — so pausing proceeds regardless.
        // The id guard is not decoration: draining "recording 0" would mean draining rows no recording
        // owns into this session.
        if (recordingId != 0L) {
            try {
                uploadMutex.withLock { delivery.drainLocked(recordingId, sessionId, prefsSeq) }
            } catch (e: Exception) {
                Log.w(TAG, "Pre-pause flush failed; pausing anyway: ${e.message}")
            }
        }
        api.pauseSession(sessionId)
        // prefs.sessionPaused is set synchronously by the service before this coroutine runs.
    }

    /** Tells the server to resume a paused session (explicit user Resume, or an auto grace-resume). */
    suspend fun resumePausedSession(sessionId: String? = null) = withContext(Dispatchers.IO) {
        // No known session → nothing to tell the server; capture simply continues offline and
        // tryCreateSession owns session creation (and the single 409 policy). The old fallback adopted
        // getActiveSessions().firstOrNull() here — the last surviving copy of the adoption pattern that
        // createQueuedSessionLocked and CredentialVerifier both document as how one ride's points get
        // appended to another ride's activity; worse, it resynced no lastSequence, so the adopted
        // session's dedup then silently ATE this ride's first batches (AQ-04 guard sees "caught up").
        val id = sessionId ?: prefs.activeSessionId ?: return@withContext
        api.resumeSession(id)
    }

    /**
     * Ends the current recording: deliver every point, stop the session, and only then forget it.
     * Anything that could not be delivered is PARKED in the upload queue — never dropped, and never left
     * open on the server with no local pointer to it (that was the 2h12m-hole bug: the phone cleared its
     * state, the session stayed 'recording' server-side, and the next start reconnected into it).
     */
    suspend fun stopSession(): Unit = withContext(Dispatchers.IO) {
        val recordingId = prefs.currentRecordingId
        if (recordingId == 0L) { prefs.clearSession(); return@withContext }

        // Started offline and still has no session? One last attempt now — the network may be back.
        if (!prefs.isRecording)
            runCatching { tryCreateSession() }
                .onFailure { if (it is kotlinx.coroutines.CancellationException) throw it } // AND-40

        val sessionId = prefs.activeSessionId
        if (sessionId == null) {
            // Still no session: the queue will create it and deliver the points when a network returns.
            parkCurrentRecordingAsync()
            return@withContext
        }

        val drained = try {
            uploadMutex.withLock { delivery.drainLocked(recordingId, sessionId, prefsSeq) }
        } catch (e: Exception) {
            Log.w(TAG, "Pre-stop drain failed: ${e.message}")
            false
        }
        if (!drained) {
            // Points still undelivered → park with the session; the queue appends the rest, THEN stops it.
            parkCurrentRecordingAsync()
            return@withContext
        }

        var lastError: Exception? = null
        repeat(STOP_ATTEMPTS) { attempt ->
            try {
                api.stopSession(sessionId)
                forgetStoppedRecording(recordingId)
                EventLog.log("Recording stopped and fully delivered")
                return@withContext
            } catch (e: HttpException) {
                if (e.code() == 404) {   // already finalized/abandoned server-side — nothing left to do
                    forgetStoppedRecording(recordingId)
                    return@withContext
                }
                lastError = e
            } catch (e: Exception) {
                lastError = e
            }
            Log.w(TAG, "stopSession attempt ${attempt + 1}/$STOP_ATTEMPTS failed: ${lastError?.message}")
            if (attempt < STOP_ATTEMPTS - 1) delay(2_000L * (attempt + 1))
        }

        // Points are delivered but the stop never landed. Park it: the session is still OPEN on the
        // server, and only a successful stop closes it. (The old code cleared local state here and left it
        // to the server's 12 h abandon — which is what let the next start 409-reconnect into a dead ride.)
        Log.e(TAG, "stopSession failed after $STOP_ATTEMPTS attempts; queued for retry", lastError)
        EventLog.log("Stop failed — recording queued so the stop can be retried")
        parkCurrentRecordingAsync()
    }

    /**
     * The session is closed and its points are delivered — drop the recording, under the lock, so a
     * concurrent [parkCurrentRecordingLocked] (the finalize worker's service-gone path) can't slip in
     * between the two writes and queue a recording that is already finished.
     *
     * The stop races the last in-flight [enqueuePoints]: fixes captured in the final seconds can land
     * after the drain, and a stopped session cannot take them. Dropping them is the only option left —
     * but never silently, or a missing track tail has no fingerprint.
     */
    private suspend fun forgetStoppedRecording(recordingId: Long) = uploadMutex.withLock {
        val tail = dao.countFor(recordingId)
        if (tail > 0) {
            Log.w(TAG, "Dropping $tail point(s) captured after the session was stopped")
            EventLog.log("Dropped $tail point(s) captured after the stop")
        }
        dao.deleteFor(recordingId)
        prefs.clearSession()
    }

    // ── Parking: current recording → upload queue ───────────────────────────

    /**
     * Hands the current recording to the upload queue and clears it from prefs, so a new recording can
     * start immediately (offline included) without inheriting its points or its session.
     */
    suspend fun parkCurrentRecordingAsync() = withContext(Dispatchers.IO) {
        uploadMutex.withLock { parkCurrentRecordingLocked() }
    }

    private suspend fun parkCurrentRecordingLocked() {
        val recordingId = prefs.currentRecordingId
        if (recordingId == 0L) return

        val sessionId = prefs.activeSessionId
        val params = prefs.currentRecordingParams
        val points = dao.countFor(recordingId)

        // Fully delivered and no session left open → nothing for the queue to do.
        if (points == 0 && sessionId == null) {
            prefs.clearSession()
            return
        }

        // No session AND no params → the session can never be created, so these points can never be
        // delivered to the right activity. Unreachable in practice (params are written before capture
        // starts); drop them rather than leaving rows the queue would carry forever.
        if (sessionId == null && params == null) {
            Log.e(TAG, "Parking $recordingId with no session and no params — discarding $points point(s)")
            EventLog.log("Queue: discarded $points undeliverable point(s)")
            dao.deleteFor(recordingId)
            prefs.clearSession()
            return
        }

        pendingDao.upsert(
            PendingRecordingEntity.from(
                recordingId = recordingId,
                sessionId = sessionId,
                clientSequence = prefs.clientSequence,
                // Params are only read when a session must still be created (sessionId == null), which the
                // branch above guarantees is impossible here without them.
                params = params ?: RecordingParams(
                    prefs.currentActivityType ?: ActivityType.OTHER.wire, Visibility.PRIVATE.wire, "unknown", 0, prefs.currentAutoDetected
                )
            )
        )
        EventLog.log(
            "Queued for upload: $points point(s)" + if (sessionId == null) " (session not created yet)" else ""
        )
        prefs.clearSession()
    }

    // ── The upload queue ────────────────────────────────────────────────────

    /**
     * Delivers every parked recording — see [QueueDrainer.flushLocked] for the queue-drain semantics. The
     * `busyCapturing()` short-circuit stays OUTSIDE the lock: a live recording owns the pipe, so there is no
     * point taking the mutex only to release it — exactly as before this drain became a collaborator.
     */
    suspend fun flushQueueAsync() = withContext(Dispatchers.IO) {
        if (busyCapturing()) return@withContext
        uploadMutex.withLock { drainer.flushLocked() }
    }

    suspend fun queueStatusAsync(): QueueStatus = withContext(Dispatchers.IO) {
        QueueStatus(recordings = pendingDao.count(), points = dao.countQueued())
    }

    suspend fun hasQueuedWork(): Boolean = withContext(Dispatchers.IO) { pendingDao.count() > 0 }

    /**
     * Startup reaper for points orphaned by the park zero-point race — see [QueueDrainer.sweepLocked] for the
     * why. The live recording id is read here from prefs, under the lock, and handed down so the drainer
     * stays prefs-free; the read happens at the same instant inside the lock as it did before this moved.
     */
    suspend fun sweepOrphanedPointsAsync() = withContext(Dispatchers.IO) {
        uploadMutex.withLock { drainer.sweepLocked(prefs.currentRecordingId) }
    }

    /** User-initiated "give up on the backlog". Only ever touches queued rows — never the live recording. */
    suspend fun discardQueueAsync() = withContext(Dispatchers.IO) {
        val sessions = uploadMutex.withLock {
            val status = QueueStatus(pendingDao.count(), dao.countQueued())
            if (status.isEmpty && status.points == 0) return@withLock emptyList()

            // Sessions whose server side must be abandoned. A discarded recording whose session stays
            // 'recording' would keep holding the server's single active-session slot and 409 the next start;
            // worse, the next ride's stale-session check (resolveSessionConflict's CLOSE_STALE) would find it
            // and *stop* it, finalizing the very ride the user discarded into a real activity (AND-06).
            val sessions = pendingDao.getWithSession().mapNotNull { it.sessionId }
            val nowMs = System.currentTimeMillis()

            // AND-23: the discard is ONE committed transaction — the queue rows are deleted AND every abandon
            // is durably recorded in pending_abandons together. A crash leaves the queue either fully
            // discarded (rows gone, abandons pending for [retryPendingAbandonsAsync]) or untouched, never
            // half. The old order (abandon over the network first, THEN delete) could crash after abandoning
            // some sessions with the local rows still present, and the next flush would resurrect the
            // discarded ride as a fresh activity.
            transactor.transaction {
                for (sessionId in sessions) pendingAbandonDao.upsert(PendingAbandonEntity(sessionId, nowMs))
                dao.deleteQueuedPoints()
                pendingDao.deleteAll()
            }
            EventLog.log("Queue discarded by user (${status.recordings} recording(s), ${status.points} pts)")
            sessions
        }

        // Best-effort: fire the abandons now so the server frees the slot immediately. Whatever fails
        // (offline) stays in pending_abandons and is retried by [retryPendingAbandonsAsync]. A 404 means
        // the server already forgot it — the intent is satisfied, so drop it. OUTSIDE the mutex, like the
        // retry pass and for the same reason: N discarded sessions on flaky connectivity could hold the
        // lock N×15 s and stall the live upload loop; the mutex was only ever for the queue mutation above.
        for (sessionId in sessions) {
            runCatching { api.abandonSession(sessionId) }
                .onSuccess { pendingAbandonDao.delete(sessionId) }
                .onFailure { e ->
                    // runCatching also catches CancellationException — rethrow it so a cancelled discard
                    // actually stops instead of silently working through the rest of the list (AND-40).
                    if (e is kotlinx.coroutines.CancellationException) throw e
                    if (e is HttpException && e.code() == 404) pendingAbandonDao.delete(sessionId)
                }
        }
    }

    /**
     * Retries abandons that failed offline at Discard time (AND-06). Independent of [uploadMutex] — these
     * sessions own no local queue row any more, so nothing else can race a write against them.
     */
    suspend fun retryPendingAbandonsAsync() = withContext(Dispatchers.IO) {
        for (row in pendingAbandonDao.getAll()) {
            runCatching { api.abandonSession(row.sessionId) }
                .onSuccess { pendingAbandonDao.delete(row.sessionId) }
                .onFailure { e ->
                    // runCatching also catches CancellationException — rethrow so a cancelled retry pass
                    // stops instead of grinding through every pending row (AND-40).
                    if (e is kotlinx.coroutines.CancellationException) throw e
                    // 404 = the server already forgot it (purged, or someone else stopped it) — the intent
                    // is satisfied either way, so stop retrying. Anything else: try again next pass.
                    if (e is HttpException && e.code() == 404) pendingAbandonDao.delete(row.sessionId)
                }
        }
    }
}
