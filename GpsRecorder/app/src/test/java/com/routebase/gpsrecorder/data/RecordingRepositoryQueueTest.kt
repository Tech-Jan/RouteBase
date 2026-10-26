package com.routebase.gpsrecorder.data

import com.routebase.gpsrecorder.util.ActivityType
import com.routebase.gpsrecorder.util.RecordingParams
import com.routebase.gpsrecorder.util.RecordingPrefs
import kotlinx.coroutines.test.runTest
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.ResponseBody.Companion.toResponseBody
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import retrofit2.HttpException
import retrofit2.Response

/**
 * The queue-drain and startup-sweep behaviour of [RecordingRepository] — none of which could be reached by
 * a test before ARCH-AND-B. The drain's "don't touch the network while something is capturing" gate used to
 * read a process-global static on [com.routebase.gpsrecorder.service.GpsRecordingService], unfakeable from
 * the JVM; it is now the injected [RecordingRepository.busyCapturing] predicate, and prefs/db are behind the
 * [RecordingPrefs] / [Transactor] seams, so the whole class builds with in-memory fakes.
 *
 * Each `busyCapturing` re-check the queue does is load-bearing (RecordingRepository:557/562/566) and was
 * pinned only by comments. The conflict path's `abortIf = { false }` — the one place the drain must finish
 * DESPITE a live recording — was likewise unpinned.
 */
class RecordingRepositoryQueueTest {

    // ── Fakes ───────────────────────────────────────────────────────────────

    private class FakeApi : ApiService {
        val startCalls = mutableListOf<StartSessionRequest>()
        val appendCalls = mutableListOf<Pair<String, Int>>()   // sessionId, clientSequence
        val resumeCalls = mutableListOf<String>()
        val stopCalls = mutableListOf<String>()
        val abandonCalls = mutableListOf<String>()

        /** Scripted replies, consumed one per call; a null-terminated script falls back to the default. */
        var startScript = ArrayDeque<Result<SessionResponse>>()
        var appendScript = ArrayDeque<Result<AppendPointsResponse>>()
        var activeSessions: List<SessionResponse> = emptyList()

        override suspend fun startSession(request: StartSessionRequest): SessionResponse {
            startCalls += request
            return (startScript.removeFirstOrNull() ?: Result.success(session("started"))).getOrThrow()
        }

        override suspend fun appendPoints(sessionId: String, request: AppendPointsRequest): AppendPointsResponse {
            appendCalls += sessionId to request.clientSequence
            return (appendScript.removeFirstOrNull()
                ?: Result.success(AppendPointsResponse(request.points.size, request.clientSequence, false)))
                .getOrThrow()
        }

        override suspend fun resumeSession(sessionId: String): SessionResponse {
            resumeCalls += sessionId; return session(sessionId)
        }

        override suspend fun stopSession(sessionId: String): SessionResponse {
            stopCalls += sessionId; return session(sessionId)
        }

        override suspend fun abandonSession(sessionId: String): SessionResponse {
            abandonCalls += sessionId; return session(sessionId)
        }

        override suspend fun pauseSession(sessionId: String): SessionResponse = session(sessionId)
        override suspend fun getActiveSessions(): List<SessionResponse> = activeSessions
        override suspend fun mintLiveShare(sessionId: String) = ShareLinkResponse("/s/tok")
    }

    private class FakeGpsPointDao(seed: List<GpsPointEntity> = emptyList()) : GpsPointDao {
        val rows = seed.toMutableList()
        var orphanCount = 0
        var deleteOrphansCalls = 0

        override suspend fun getPendingFor(recordingId: Long, limit: Int): List<GpsPointEntity> =
            rows.filter { it.recordingId == recordingId }.sortedBy { it.id }.take(limit)

        override suspend fun getByIds(ids: List<Long>): List<GpsPointEntity> =
            rows.filter { it.id in ids }.sortedBy { it.id }

        override suspend fun countFor(recordingId: Long): Int = rows.count { it.recordingId == recordingId }
        override suspend fun deleteByIds(ids: List<Long>) { rows.removeAll { it.id in ids } }
        override suspend fun deleteFor(recordingId: Long) { rows.removeAll { it.recordingId == recordingId } }
        override suspend fun insertAll(points: List<GpsPointEntity>) { rows += points }
        override suspend fun reassignOwner(fromId: Long, toId: Long) = Unit
        override suspend fun countQueued(): Int = 0
        override suspend fun deleteQueuedPoints() = Unit

        override suspend fun countOrphans(currentRecordingId: Long): Int = orphanCount
        override suspend fun deleteOrphans(currentRecordingId: Long): Int {
            deleteOrphansCalls++
            return orphanCount
        }
    }

    private class FakePendingDao(seed: List<PendingRecordingEntity> = emptyList()) : PendingRecordingDao {
        val rows = seed.toMutableList()

        override suspend fun upsert(recording: PendingRecordingEntity) {
            rows.removeAll { it.recordingId == recording.recordingId }; rows += recording
        }
        override suspend fun getWithSession(): List<PendingRecordingEntity> =
            rows.filter { it.sessionId != null }.sortedBy { it.recordingId }
        override suspend fun getWithoutSession(): List<PendingRecordingEntity> =
            rows.filter { it.sessionId == null }.sortedBy { it.recordingId }
        override suspend fun findBySessionId(sessionId: String): PendingRecordingEntity? =
            rows.firstOrNull { it.sessionId == sessionId }
        override suspend fun setSessionId(recordingId: Long, sessionId: String) {
            replace(recordingId) { it.copy(sessionId = sessionId) }
        }
        override suspend fun detachSession(recordingId: Long) {
            replace(recordingId) { it.copy(sessionId = null, clientSequence = 0) }
        }
        override suspend fun setClientSequence(recordingId: Long, seq: Int) {
            replace(recordingId) { it.copy(clientSequence = seq) }
        }
        override suspend fun delete(recordingId: Long) { rows.removeAll { it.recordingId == recordingId } }
        override suspend fun count(): Int = rows.size
        override suspend fun maxRecordingId(): Long? = rows.maxOfOrNull { it.recordingId }
        override suspend fun deleteAll() { rows.clear() }

        private fun replace(id: Long, f: (PendingRecordingEntity) -> PendingRecordingEntity) {
            val i = rows.indexOfFirst { it.recordingId == id }
            if (i >= 0) rows[i] = f(rows[i])
        }
    }

    private class FakeAbandonDao : PendingAbandonDao {
        val rows = mutableListOf<PendingAbandonEntity>()
        override suspend fun upsert(entity: PendingAbandonEntity) {
            rows.removeAll { it.sessionId == entity.sessionId }; rows += entity
        }
        override suspend fun getAll(): List<PendingAbandonEntity> = rows.toList()
        override suspend fun delete(sessionId: String) { rows.removeAll { it.sessionId == sessionId } }
    }

    /** Runs the block inline — a unit test needs the atomicity contract, not a real Room transaction. */
    private class FakeTransactor : Transactor {
        override suspend fun <R> transaction(block: suspend () -> R): R = block()
    }

    /** In-memory [RecordingPrefs]; isCapturing/isRecording derive from the fields exactly like PrefsManager. */
    private class FakePrefs : RecordingPrefs {
        override var currentRecordingId: Long = 0L
        override var currentRecordingParams: RecordingParams? = null
        override var activeSessionId: String? = null
        override var activeShareUrl: String? = null
        override var clientSequence: Int = 0
        override var recordingStartTimeMs: Long = 0L
        override var sessionPaused: Boolean = false
        override var lastMovedMs: Long = 0L
        override var currentActivityType: String? = null
        override var currentAutoDetected: Boolean = false
        override val isCapturing: Boolean get() = currentRecordingId != 0L
        override val isRecording: Boolean get() = !activeSessionId.isNullOrBlank()
        override fun clearSession() {
            currentRecordingId = 0L; currentRecordingParams = null; activeSessionId = null
            activeShareUrl = null; clientSequence = 0; recordingStartTimeMs = 0L
            sessionPaused = false
        }
    }

    // ── Fixture helpers ─────────────────────────────────────────────────────

    private fun repo(
        api: FakeApi = FakeApi(),
        dao: FakeGpsPointDao = FakeGpsPointDao(),
        pendingDao: FakePendingDao = FakePendingDao(),
        prefs: FakePrefs = FakePrefs(),
        abandonDao: FakeAbandonDao = FakeAbandonDao(),
        busyCapturing: () -> Boolean = { false }
    ) = RecordingRepository(api, dao, pendingDao, prefs, abandonDao, FakeTransactor(), busyCapturing)

    private fun parked(recordingId: Long, sessionId: String?, clientSequence: Int = 0) =
        PendingRecordingEntity(
            recordingId = recordingId, sessionId = sessionId, clientSequence = clientSequence,
            activityType = ActivityType.RIDE.wire, visibility = "private", clientVersion = "test",
            tzOffsetMinutes = 0, autoDetected = false, parkedAtMs = 1_700_000_000_000L
        )

    private fun point(id: Long, owner: Long) = GpsPointEntity(
        id = id, recordingId = owner, lat = 47.0, lng = 11.0,
        altitude = null, speed = null, pressureHpa = null, timestampUtc = "2026-07-31T10:00:00Z"
    )

    private fun http(code: Int): Result<SessionResponse> = Result.failure(
        HttpException(Response.error<Any>(code, "".toResponseBody("text/plain".toMediaType())))
    )

    // ── flushQueueAsync: the busyCapturing gate ─────────────────────────────

    @Test fun `flush delivers a queued recording when idle`() = runTest {
        // The positive control: nothing capturing → the queue drains one parked ride end-to-end.
        val api = FakeApi()
        val dao = FakeGpsPointDao(listOf(point(1, 900L), point(2, 900L)))
        val pending = FakePendingDao(listOf(parked(900L, sessionId = "sess-900")))

        repo(api = api, dao = dao, pendingDao = pending, busyCapturing = { false }).flushQueueAsync()

        assertEquals("its points were appended", listOf("sess-900" to 1), api.appendCalls)
        assertEquals("then the session was stopped", listOf("sess-900"), api.stopCalls)
        assertEquals("and the queue row is gone", 0, pending.count())
        assertTrue("its points are gone", dao.rows.isEmpty())
    }

    @Test fun `flush is skipped entirely while a recording is capturing`() = runTest {
        // RecordingRepository:557 — the outer gate. A live recording owns the upload pipe; the queue must
        // not fire a single request, or a queued ride's create/append could collide with the live session.
        val api = FakeApi()
        val pending = FakePendingDao(listOf(parked(900L, sessionId = "sess-900")))

        repo(api = api, pendingDao = pending, busyCapturing = { true }).flushQueueAsync()

        assertTrue("no session was started", api.startCalls.isEmpty())
        assertTrue("no points were appended", api.appendCalls.isEmpty())
        assertTrue("no session was stopped", api.stopCalls.isEmpty())
        assertEquals("the queue is untouched", 1, pending.count())
    }

    @Test fun `flush aborts part-way when a recording starts between rows`() = runTest {
        // RecordingRepository:562 — the per-row re-check. Two parked rides; a recording starts the instant
        // the first finishes (modelled as busy the moment the first stop lands). The second must be left
        // for the next pass, NOT delivered on top of the freshly-started live session.
        val api = FakeApi()
        val pending = FakePendingDao(listOf(parked(100L, "sess-100"), parked(200L, "sess-200")))

        // Idle until the first delivery completes (its stop call lands), busy from then on.
        repo(api = api, pendingDao = pending, busyCapturing = { api.stopCalls.isNotEmpty() }).flushQueueAsync()

        assertEquals("only the first ride was stopped", listOf("sess-100"), api.stopCalls)
        assertNull("the first row is gone", pending.getWithSession().firstOrNull { it.recordingId == 100L })
        assertEquals("the second row survives for the next pass",
            200L, pending.getWithSession().single().recordingId)
    }

    // ── The conflict path: abortIf = { false } finishes DESPITE a live recording ──

    @Test fun `conflict path delivers the blocking queued recording despite a live recording`() = runTest {
        // RecordingRepository:304 — a live recording just 409'd on session creation because a FINISHED,
        // still-queued recording holds the server's one active-session slot. The queue drain normally yields
        // to a live recording, but here it must finish IN SPITE of one (busyCapturing = { true }) — the live
        // recording is blocked until the slot is freed. That is the only caller that passes abortIf = { false }.
        val api = FakeApi().apply {
            activeSessions = listOf(session("queued-sess"))   // the queued ride still holds the slot
            startScript.addLast(http(409))                    // the live ride's create is refused …
            startScript.addLast(Result.success(session("live-sess")))  // … then succeeds once the slot frees
        }
        val pending = FakePendingDao(listOf(parked(50L, sessionId = "queued-sess")))
        val prefs = FakePrefs().apply {
            currentRecordingId = 5000L                        // a live recording is capturing …
            activeSessionId = null                            // … but has no session yet (isRecording=false)
            currentRecordingParams = RecordingParams(ActivityType.RIDE.wire, "private", "test", 0, false)
        }

        val created = repo(api = api, pendingDao = pending, prefs = prefs, busyCapturing = { true })
            .tryCreateSession()

        assertTrue("the live recording ends up with a session after the slot is freed", created)
        assertEquals("the blocking queued ride was stopped despite busyCapturing=true",
            listOf("queued-sess"), api.stopCalls)
        assertEquals("the queue is now empty", 0, pending.count())
        assertEquals("the live session was created only after the queued one was cleared",
            listOf("live-sess"), listOf(prefs.activeSessionId))
    }

    // ── AND-42: the startup orphan sweep skips the write when there is nothing to reap ──

    @Test fun `orphan sweep skips the delete transaction when nothing to reap`() = runTest {
        val dao = FakeGpsPointDao().apply { orphanCount = 0 }

        repo(dao = dao).sweepOrphanedPointsAsync()

        assertEquals("the armed-but-empty common case must not open the DELETE transaction",
            0, dao.deleteOrphansCalls)
    }

    @Test fun `orphan sweep deletes when orphans exist`() = runTest {
        val dao = FakeGpsPointDao().apply { orphanCount = 3 }

        repo(dao = dao).sweepOrphanedPointsAsync()

        assertEquals("a real orphan still gets reaped — the mitigation only gates the empty case",
            1, dao.deleteOrphansCalls)
    }

    @Test fun `busyCapturing predicate is what the drain reads, not any service static`() = runTest {
        // Regression guard for ARCH-AND-B: prove the gate is the injected predicate end-to-end. If a future
        // refactor reintroduced a reach-up to GpsRecordingService.isServiceActive, this flush would still
        // fire because the JVM static is false in a unit test — so the assertion below would break.
        val api = FakeApi()
        val pending = FakePendingDao(listOf(parked(1L, "s1")))
        var busy = true

        val r = repo(api = api, pendingDao = pending, busyCapturing = { busy })
        r.flushQueueAsync()
        assertTrue("predicate true → nothing drained", api.stopCalls.isEmpty())

        busy = false
        r.flushQueueAsync()
        assertEquals("predicate false → the same queue now drains", listOf("s1"), api.stopCalls)
    }
}

/** Top-level so the nested [ApiService] fake can build default responses too. */
private fun session(id: String, status: String? = null) =
    SessionResponse(sessionId = id, trackId = 1L, status = status ?: "recording",
        activityType = ActivityType.RIDE.wire, startedAt = "2026-07-31T09:00:00Z")
