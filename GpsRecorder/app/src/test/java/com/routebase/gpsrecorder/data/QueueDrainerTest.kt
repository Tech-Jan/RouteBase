package com.routebase.gpsrecorder.data

import com.routebase.gpsrecorder.util.ActivityType
import kotlinx.coroutines.test.runTest
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.ResponseBody.Companion.toResponseBody
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import retrofit2.HttpException
import retrofit2.Response

/**
 * [QueueDrainer] driven directly — cheaper than going through [RecordingRepository], and the point of the
 * QueueDrainer extraction. Every method under test is `*Locked`: a unit test holds no mutex (there is no
 * contention), so it just calls them. The drain shares a real [PointDelivery] built over the same fakes,
 * exactly as production shares the repository's one instance.
 *
 * The invariants pinned here were comment-only before the extraction (RecordingRepository:561/566/570/573,
 * :567 "one poisoned row must not block the others", :586 `abortIf` default): each `busyCapturing()`
 * re-check, and the conflict path's `abortIf = { false }` that must finish DESPITE a live recording.
 */
class QueueDrainerTest {

    // ── Fakes (tailored to the drain: no prefs, no transactor — the drainer touches neither) ──

    private class FakeApi : ApiService {
        val startCalls = mutableListOf<StartSessionRequest>()
        val appendCalls = mutableListOf<Pair<String, Int>>()   // sessionId, clientSequence
        val resumeCalls = mutableListOf<String>()
        val stopCalls = mutableListOf<String>()

        /** sessionIds whose append / stop should fail with a non-404 HttpException (poison-row modelling). */
        val appendFailFor = mutableSetOf<String>()
        val stopFailFor = mutableSetOf<String>()

        /** Scripted create replies, consumed one per call; empties fall back to a default success. */
        val startScript = ArrayDeque<Result<SessionResponse>>()

        override suspend fun startSession(request: StartSessionRequest): SessionResponse {
            startCalls += request
            return (startScript.removeFirstOrNull() ?: Result.success(session("started"))).getOrThrow()
        }

        override suspend fun appendPoints(sessionId: String, request: AppendPointsRequest): AppendPointsResponse {
            appendCalls += sessionId to request.clientSequence
            if (sessionId in appendFailFor) throw http(500)
            return AppendPointsResponse(request.points.size, request.clientSequence, false)
        }

        override suspend fun resumeSession(sessionId: String): SessionResponse {
            resumeCalls += sessionId; return session(sessionId)
        }

        override suspend fun stopSession(sessionId: String): SessionResponse {
            stopCalls += sessionId
            if (sessionId in stopFailFor) throw http(500)
            return session(sessionId)
        }

        override suspend fun abandonSession(sessionId: String): SessionResponse = session(sessionId)
        override suspend fun pauseSession(sessionId: String): SessionResponse = session(sessionId)
        override suspend fun getActiveSessions(): List<SessionResponse> = emptyList()
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

    // ── Fixture helpers ─────────────────────────────────────────────────────

    private fun drainer(
        api: FakeApi = FakeApi(),
        dao: FakeGpsPointDao = FakeGpsPointDao(),
        pendingDao: FakePendingDao = FakePendingDao(),
        busyCapturing: () -> Boolean = { false }
    ) = QueueDrainer(api, dao, pendingDao, PointDelivery(api, dao), busyCapturing)

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

    // ── flushLocked: the positive control ───────────────────────────────────

    @Test fun `flushLocked drains a with-session and a without-session row when idle`() = runTest {
        // With-session rows go first (freeing the server's one active-session slot); the sessionless row
        // then gets a session created and delivered too.
        val api = FakeApi()
        val dao = FakeGpsPointDao(listOf(point(1, 100L), point(2, 200L)))
        val pending = FakePendingDao(listOf(parked(100L, "sess-100"), parked(200L, sessionId = null)))

        drainer(api, dao, pending, busyCapturing = { false }).flushLocked()

        assertEquals("the sessionless ride had a server session created", 1, api.startCalls.size)
        assertEquals("both rides were stopped, with-session first", listOf("sess-100", "started"), api.stopCalls)
        assertEquals("the queue is empty", 0, pending.count())
        assertTrue("every point was delivered", dao.rows.isEmpty())
    }

    // ── flushLocked: the busyCapturing re-checks ────────────────────────────

    @Test fun `flushLocked drains nothing while busyCapturing is true`() = runTest {
        // RecordingRepository:566 as a wholesale skip — busy from the first row, so not one request fires.
        val api = FakeApi()
        val dao = FakeGpsPointDao(listOf(point(1, 900L)))
        val pending = FakePendingDao(listOf(parked(900L, "sess-900")))

        drainer(api, dao, pending, busyCapturing = { true }).flushLocked()

        assertTrue("no session started", api.startCalls.isEmpty())
        assertTrue("no points appended", api.appendCalls.isEmpty())
        assertTrue("no session stopped", api.stopCalls.isEmpty())
        assertEquals("the queue is untouched", 1, pending.count())
    }

    @Test fun `flushLocked aborts before the next row when a recording starts mid-drain`() = runTest {
        // RecordingRepository:566 — the per-row re-check. Idle until the first stop lands, busy from then on:
        // the second row must be left for the next pass, not delivered on top of the freshly-started ride.
        val api = FakeApi()
        val dao = FakeGpsPointDao(listOf(point(1, 100L), point(2, 200L)))
        val pending = FakePendingDao(listOf(parked(100L, "sess-100"), parked(200L, "sess-200")))

        drainer(api, dao, pending, busyCapturing = { api.stopCalls.isNotEmpty() }).flushLocked()

        assertEquals("only the first ride was stopped", listOf("sess-100"), api.stopCalls)
        assertEquals("the second row survives for the next pass",
            200L, pending.getWithSession().single().recordingId)
    }

    // ── flushLocked: one poisoned row must not block the others (:567) ───────

    @Test fun `flushLocked keeps draining after a poisoned with-session row fails`() = runTest {
        // The first loop calls deliverLocked(row) and IGNORES the result, so a row that cannot be delivered
        // must not halt the loop. Model: the first ride's append 500s (points kept, row kept), yet the
        // healthy ride behind it still delivers and is removed.
        val api = FakeApi().apply { appendFailFor += "sess-bad" }
        val dao = FakeGpsPointDao(listOf(point(1, 100L), point(2, 200L)))
        val pending = FakePendingDao(listOf(parked(100L, "sess-bad"), parked(200L, "sess-good")))

        drainer(api, dao, pending, busyCapturing = { false }).flushLocked()

        assertEquals("only the healthy row was stopped — the failure did not halt the loop",
            listOf("sess-good"), api.stopCalls)
        assertNotNull("the poisoned row is left for a retry",
            pending.getWithSession().firstOrNull { it.recordingId == 100L })
        assertNull("the healthy row behind it was delivered and removed",
            pending.rows.firstOrNull { it.recordingId == 200L })
        assertTrue("the poisoned row's points are kept (append never succeeded)",
            dao.rows.any { it.recordingId == 100L })
        assertTrue("the healthy row's points are gone (delivered)",
            dao.rows.none { it.recordingId == 200L })
    }

    // ── flushLocked: the sessionless loop serializes on the one-active-session rule (:573) ──

    @Test fun `flushLocked stops the sessionless loop at the first row that cannot be created`() = runTest {
        // RecordingRepository:573 — the SECOND loop returns on the first !deliverLocked(row): if this
        // sessionless ride's create 409s (another is active), the next would only 409 behind it. Contrast
        // the first loop above, which pushes on.
        val api = FakeApi().apply { startScript.addLast(Result.failure(http(409))) }
        val dao = FakeGpsPointDao(listOf(point(1, 300L), point(2, 400L)))
        val pending = FakePendingDao(listOf(parked(300L, sessionId = null), parked(400L, sessionId = null)))

        drainer(api, dao, pending, busyCapturing = { false }).flushLocked()

        assertEquals("only the first sessionless row's create was attempted", 1, api.startCalls.size)
        assertEquals("nothing was stopped", emptyList<String>(), api.stopCalls)
        assertEquals("both rows survive for the next pass", 2, pending.count())
    }

    // ── deliverLocked: abortIf = { false } finishes DESPITE a live recording ──

    @Test fun `deliverLocked with abortIf false finishes despite busyCapturing true`() = runTest {
        // RecordingRepository:308's conflict path, tested at its own level: a FINISHED, still-queued
        // recording holds the server's slot and blocks a live ride's session. The drain normally yields to a
        // live recording, but this one caller passes abortIf = { false } and must finish anyway.
        val api = FakeApi()
        val dao = FakeGpsPointDao(listOf(point(1, 50L)))
        val row = parked(50L, "queued-sess")
        val pending = FakePendingDao(listOf(row))

        val delivered = drainer(api, dao, pending, busyCapturing = { true })
            .deliverLocked(row, abortIf = { false })

        assertTrue("the blocking queued ride is delivered despite busyCapturing=true", delivered)
        assertEquals("its points were appended", listOf("queued-sess" to 1), api.appendCalls)
        assertEquals("and it was stopped", listOf("queued-sess"), api.stopCalls)
        assertEquals("the queue row is gone", 0, pending.count())
    }

    @Test fun `deliverLocked yields when busyCapturing is true and abortIf is the default`() = runTest {
        // The mirror of the above: the default abortIf is busyCapturing, so a normal drain of the same row
        // under a live recording delivers nothing (drainLocked aborts before the first batch).
        val api = FakeApi()
        val dao = FakeGpsPointDao(listOf(point(1, 50L)))
        val row = parked(50L, "queued-sess")
        val pending = FakePendingDao(listOf(row))

        val delivered = drainer(api, dao, pending, busyCapturing = { true }).deliverLocked(row)

        assertFalse("a live recording blocks the ordinary drain", delivered)
        assertTrue("no points were appended", api.appendCalls.isEmpty())
        assertTrue("no session was stopped", api.stopCalls.isEmpty())
        assertEquals("the row survives", 1, pending.count())
    }

    // ── sweepLocked: skip the write when there is nothing to reap (folded-in item 3) ──

    @Test fun `sweepLocked skips the delete transaction when nothing to reap`() = runTest {
        val dao = FakeGpsPointDao().apply { orphanCount = 0 }

        drainer(dao = dao).sweepLocked(currentRecordingId = 0L)

        assertEquals("the armed-but-empty common case must not open the DELETE transaction",
            0, dao.deleteOrphansCalls)
    }

    @Test fun `sweepLocked deletes when orphans exist`() = runTest {
        val dao = FakeGpsPointDao().apply { orphanCount = 3 }

        drainer(dao = dao).sweepLocked(currentRecordingId = 7L)

        assertEquals("a real orphan still gets reaped — the gate only skips the empty case",
            1, dao.deleteOrphansCalls)
    }
}

/** File-private session builder for the fake's default responses (mirrors RecordingRepositoryQueueTest). */
private fun session(id: String, status: String? = null) =
    SessionResponse(sessionId = id, trackId = 1L, status = status ?: "recording",
        activityType = ActivityType.RIDE.wire, startedAt = "2026-07-31T09:00:00Z")

/** A non-404 [HttpException] with the given status, for modelling append/stop/create failures. */
private fun http(code: Int) = HttpException(
    Response.error<Any>(code, "".toResponseBody("text/plain".toMediaType()))
)
