package com.routebase.gpsrecorder.data

import android.util.Log
import com.routebase.gpsrecorder.util.EventLog
import com.routebase.gpsrecorder.util.PointEnvelope
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext
import retrofit2.HttpException
import java.time.Instant

private const val TAG = "PointDelivery"

// AQ-03: pace point drains under the server's ~1 s per-session rate limiter. The old back-to-back
// do-while drains 429'd on the second batch and aborted, and the stop path then deleted the
// undelivered rows. Space our own requests ≥1.1 s from the last accepted batch (window is
// server-receipt-clock relative, so no clock-skew risk); on a 429 wait out the window and retry.
private const val DRAIN_PACE_MS = 1_100L
private const val RATE_LIMIT_BACKOFF_MS = 1_500L
// Cap on consecutive 429 retries so a persistently rate-limited server can't loop forever; a
// successful batch resets the budget, so a large backlog still drains fully.
private const val MAX_DRAIN_ATTEMPTS = 5
private const val BATCH_SIZE = 100

/** Where a recording's next sequence lives: prefs while it is live, its queue row once parked. */
internal interface SeqStore {
    suspend fun get(): Int
    suspend fun set(seq: Int)
}

/** The sequence store of a PARKED recording — persisted per accepted batch, so a crash mid-drain resumes correctly. */
internal class QueueSeq(
    private val pendingDao: PendingRecordingDao,
    private val recordingId: Long,
    private var current: Int
) : SeqStore {
    override suspend fun get(): Int = current
    override suspend fun set(seq: Int) {
        current = seq
        pendingDao.setClientSequence(recordingId, seq)
    }
}

/**
 * ONE implementation of the append/acknowledge/delete invariants. The live path and the queue path used to
 * have separate copies, and the queue's copy was missing the AQ-04 acknowledgement guard.
 *
 * Every method here assumes the caller holds [RecordingRepository]'s `uploadMutex` — hence the `*Locked`
 * names. Nothing here takes the lock itself, and nothing here touches PrefsManager: the caller supplies the
 * [SeqStore] that says where this recording's sequence lives.
 */
internal class PointDelivery(
    private val api: ApiService,
    private val dao: GpsPointDao
) {

    /**
     * Whether the server said someone was on the live view, as of the last append it answered.
     *
     * A @Volatile flag rather than a return value on purpose: `uploadBatchLocked` already returns the
     * stored-point count, `drainLocked` loops it, and threading a second value up through both — and through
     * `RecordingRepository.uploadPendingPoints` — would churn four signatures to carry one hint. This is a
     * HINT, not an invariant: it only picks an upload delay. A stale value costs at most one extra tick.
     *
     * Starts `true` so a ride begins at the fast cadence before the server has answered anything.
     */
    @Volatile internal var lastWatched: Boolean = true
        private set

    // AND-02: the batch composition is FROZEN between an unacknowledged send and its retry. The AQ-04
    // guard is sequence-only — it proves "seq N is stored", not "THESE rows are stored" — and on the live
    // path the pending set GROWS between a lost response and the retry. Re-reading it would send a bigger
    // batch under the same seq; the server dedup-eats that, and the fall-through delete would destroy the
    // rows that joined since — points the server never saw, silently gone. Holding the exact row ids until
    // the seq is resolved makes "seq acknowledged ⟹ these rows stored" true again. In-memory only: a
    // process death between send and retry falls back to a re-read (a double fault, accepted residual).
    private var inFlightRecordingId: Long = -1
    private var inFlightSeq: Int = -1
    private var inFlightIds: List<Long>? = null

    /**
     * Uploads one batch of [recordingId]'s points to [sessionId]. Returns the number of points the server
     * stored (0 when nothing was pending). Throws when the batch was not accepted, so the caller backs off.
     */
    internal suspend fun uploadBatchLocked(recordingId: Long, sessionId: String, seqStore: SeqStore): Int {
        val nextSeq = seqStore.get() + 1
        val frozenIds = inFlightIds?.takeIf { inFlightRecordingId == recordingId && inFlightSeq == nextSeq }
        val pending = frozenIds?.let { dao.getByIds(it) }?.ifEmpty { null }
            ?: dao.getPendingFor(recordingId, BATCH_SIZE)
        if (pending.isEmpty()) return 0

        inFlightRecordingId = recordingId
        inFlightSeq = nextSeq
        inFlightIds = pending.map { it.id }
        val dtos = pending.map {
            GpsPointDto(it.lat, it.lng, it.altitude, it.speed, it.pressureHpa, it.timestampUtc)
        }
        val response = try {
            api.appendPoints(sessionId, AppendPointsRequest(nextSeq, dtos))
        } catch (e: HttpException) {
            if (e.code() == 400) {
                // Server 400-rejected the whole batch as out-of-envelope (RS-03). The capture-side filter
                // (PointEnvelope) should have caught these — a legacy pre-filter row (LegacyQueueMigration
                // predates PointEnvelope) or a client/server bounds drift can still slip one through — but
                // AND-08: one bad point must not cost its ~100 siblings their GPS. Re-run the same envelope
                // check here and drop ONLY the row(s) that fail it, then retry the rest under the same,
                // never-accepted sequence.
                val body = runCatching { e.response()?.errorBody()?.string() }.getOrNull()
                val poison = pending.filterNot(::isLocallyValid)
                if (poison.isEmpty()) {
                    // Nothing here fails our own envelope check, yet the server still rejected the batch —
                    // an unmirrored server rule. Can't isolate the bad row, so fall back to the old
                    // whole-batch drop rather than retrying the identical batch forever.
                    Log.w(TAG, "Append 400 with no locally-invalid point found; dropping all ${dtos.size}: $body")
                    EventLog.log("Server rejected ${dtos.size} point(s) as out-of-envelope — dropped: $body")
                    dao.deleteByIds(pending.map { it.id })
                    inFlightIds = null   // the seq was never accepted; nothing is in flight anymore
                    return 0
                }
                Log.w(TAG, "Append 400; dropping ${poison.size}/${dtos.size} poison point(s), retrying the rest: $body")
                EventLog.log(
                    "Server rejected a batch — dropped ${poison.size} poison point(s), " +
                        "retrying the other ${dtos.size - poison.size}"
                )
                dao.deleteByIds(poison.map { it.id })
                // A 400 means the server stored NOTHING under this seq, so changing the composition for
                // the retry is safe — clear the freeze and let the recursion re-read and re-freeze.
                inFlightIds = null
                return uploadBatchLocked(recordingId, sessionId, seqStore)
            }
            throw e
        }
        // The server answered, so its view of "is anyone watching" is current. Only updated here — a 400
        // (poison batch) and a throw both leave the previous value, which is right: we learned nothing.
        lastWatched = response.watched
        // AQ-04: never delete on a dedup-eaten batch (inserted == 0) unless the server confirms it is
        // caught up (lastSequence >= nextSeq). If lastSequence is behind our seq the points were NOT
        // stored — keep the rows and throw so the caller backs off and retries.
        if (response.inserted == 0 && response.lastSequence in 1 until nextSeq) {
            Log.w(TAG, "Append inserted=0 lastSequence=${response.lastSequence} < seq=$nextSeq; keeping rows")
            throw IllegalStateException("append not acknowledged (seq=$nextSeq)")
        }
        if (response.inserted != dtos.size) {
            Log.w(TAG, "Server inserted ${response.inserted}/${dtos.size} points (seq=$nextSeq)")
        }
        // AQ-09: advance seq THEN delete rows. A crash between the two re-sends this batch as seq+1
        // (server accepts as new → duplicated geometry: minor point/time skew). The reverse order is
        // worse — delete-then-crash-before-advance makes the NEXT batch reuse an accepted seq → it is
        // dedup-eaten → silent loss.
        //
        // NonCancellable so only a process death can land in that gap. Both drain callers run in a
        // cancellable scope — MainActivity's flush in lifecycleScope, the service's in serviceJob — and
        // Room suspends between the two writes, so backgrounding the app mid-drain would otherwise
        // cancel exactly there and duplicate this batch on the next pass.
        withContext(NonCancellable) {
            seqStore.set(nextSeq)
            dao.deleteByIds(pending.map { it.id })
        }
        inFlightIds = null   // the seq is resolved; the next batch reads fresh
        return response.inserted
    }

    /** Reapplies the capture-time envelope (AND-08) to a row already in the queue, so a point that slipped
     * through it — a pre-PointEnvelope legacy row, or bounds drift — is identified without needing Android
     * or a live server. An unparseable timestamp counts as invalid; it could never have passed the check. */
    private fun isLocallyValid(point: GpsPointEntity): Boolean {
        val timeMs = runCatching { Instant.parse(point.timestampUtc).toEpochMilli() }.getOrNull() ?: return false
        if (!PointEnvelope.isValidFix(point.lat, point.lng, timeMs)) return false
        if (point.altitude != null && PointEnvelope.sanitizeAltitude(point.altitude) == null) return false
        if (point.speed != null && PointEnvelope.sanitizeSpeed(point.speed) == null) return false
        if (point.pressureHpa != null && PointEnvelope.sanitizePressure(point.pressureHpa) == null) return false
        return true
    }

    /**
     * Uploads every point of one recording, paced under the server's ~1 s per-session limiter (AQ-03).
     * Returns true iff that recording has no rows left. A 429 waits out the window and retries the same
     * batch (bounded by [MAX_DRAIN_ATTEMPTS] consecutive limited attempts; a successful batch resets the
     * budget, so a large backlog still drains fully). Any other error propagates to the caller.
     */
    internal suspend fun drainLocked(
        recordingId: Long,
        sessionId: String,
        seqStore: SeqStore,
        abortIf: () -> Boolean = { false }
    ): Boolean {
        var rateLimitAttempts = 0
        while (rateLimitAttempts < MAX_DRAIN_ATTEMPTS) {
            if (abortIf()) return false
            try {
                uploadBatchLocked(recordingId, sessionId, seqStore)
                rateLimitAttempts = 0   // progress (or nothing pending) — reset the 429 budget
            } catch (e: HttpException) {
                if (e.code() == 429) {
                    rateLimitAttempts++
                    delay(RATE_LIMIT_BACKOFF_MS)
                    continue
                }
                throw e
            }
            if (dao.countFor(recordingId) == 0) return true
            delay(DRAIN_PACE_MS)   // space the next batch ≥1.1 s from this accepted one
        }
        return dao.countFor(recordingId) == 0
    }
}
