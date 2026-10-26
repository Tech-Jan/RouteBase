package com.routebase.gpsrecorder.data

import kotlinx.coroutines.test.runTest
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.ResponseBody.Companion.toResponseBody
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import retrofit2.HttpException
import retrofit2.Response
import java.io.IOException

/**
 * Pins the append/acknowledge/delete invariants of [PointDelivery] — the client's silent-data-loss surface.
 * Every rule here was written after a real bug, and until now every one of them was enforced only by a
 * comment: a refactor could have deleted the acknowledgement guard (AQ-04) or swapped the advance/delete
 * order (AQ-09) and the suite would still have been green, because there was no suite.
 *
 * Pure JVM — [PointDelivery] takes an [ApiService] and a [GpsPointDao], both interfaces, so the fakes below
 * need no Room, no Retrofit engine and no Robolectric. `runTest` runs the 1.1 s pacing and 1.5 s 429 backoff
 * on a virtual clock, so the whole suite is instant.
 */
class PointDeliveryTest {

    // ── Fakes ───────────────────────────────────────────────────────────────

    /** One append the fake server received — enough to assert sequence and payload contracts. */
    private data class Sent(val sessionId: String, val seq: Int, val count: Int)

    /**
     * Scripted server. [replies] is consumed one per append; a reply is either a response or a throwable.
     * Running past the end of the script is a test bug, not a server behaviour — fail loudly.
     */
    private class FakeApi(private val replies: MutableList<Result<AppendPointsResponse>>) : ApiService {
        val sent = mutableListOf<Sent>()

        override suspend fun appendPoints(sessionId: String, request: AppendPointsRequest): AppendPointsResponse {
            sent += Sent(sessionId, request.clientSequence, request.points.size)
            check(replies.isNotEmpty()) { "FakeApi: unscripted append #${sent.size} (seq=${request.clientSequence})" }
            return replies.removeAt(0).getOrThrow()
        }

        override suspend fun startSession(request: StartSessionRequest) = unused()
        override suspend fun pauseSession(sessionId: String) = unused()
        override suspend fun resumeSession(sessionId: String) = unused()
        override suspend fun stopSession(sessionId: String) = unused()
        override suspend fun abandonSession(sessionId: String) = unused()
        override suspend fun getActiveSessions(): List<SessionResponse> = unused()
        override suspend fun mintLiveShare(sessionId: String) = unused()

        private fun unused(): Nothing = throw AssertionError("PointDelivery must not touch the session API")
    }

    /** In-memory stand-in for the Room DAO. Only the recording-scoped reads/deletes are exercised. */
    private class FakeDao(points: List<GpsPointEntity>) : GpsPointDao {
        val rows = points.toMutableList()

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
        override suspend fun deleteOrphans(currentRecordingId: Long): Int = 0
        override suspend fun countOrphans(currentRecordingId: Long): Int = 0
    }

    /** The live path's [SeqStore] (prefs), as a plain field. */
    private class FakeSeq(var seq: Int = 0) : SeqStore {
        override suspend fun get(): Int = seq
        override suspend fun set(seq: Int) { this.seq = seq }
    }

    // ── Fixture ─────────────────────────────────────────────────────────────

    private val recordingId = 1_700_000_000_000L
    private val sessionId = "session-abc"

    private fun points(n: Int, owner: Long = recordingId) = (1..n).map {
        GpsPointEntity(
            id = it.toLong(), recordingId = owner,
            lat = 47.0, lng = 11.0, altitude = 500.0, speed = 5.0,
            pressureHpa = 1013.0, timestampUtc = "2026-07-13T10:00:0${it % 10}Z"
        )
    }

    private fun ok(inserted: Int, lastSequence: Int = 0, watched: Boolean = true) =
        Result.success(AppendPointsResponse(inserted, lastSequence, watched))

    private fun http(code: Int): Result<AppendPointsResponse> = Result.failure(
        HttpException(Response.error<Any>(code, "".toResponseBody("text/plain".toMediaType())))
    )

    private fun delivery(api: FakeApi, dao: FakeDao) = PointDelivery(api, dao)

    // ── The happy path ──────────────────────────────────────────────────────

    @Test fun `accepted batch advances the sequence and deletes exactly those rows`() = runTest {
        val dao = FakeDao(points(3))
        val api = FakeApi(mutableListOf(ok(inserted = 3, lastSequence = 1)))
        val seq = FakeSeq(0)

        val stored = delivery(api, dao).uploadBatchLocked(recordingId, sessionId, seq)

        assertEquals(3, stored)
        assertEquals(listOf(Sent(sessionId, 1, 3)), api.sent)   // a fresh session's first batch is seq 1
        assertEquals(1, seq.seq)
        assertTrue("accepted rows must be deleted", dao.rows.isEmpty())
    }

    @Test fun `nothing pending is a no-op — no request, no sequence burned`() = runTest {
        val dao = FakeDao(emptyList())
        val api = FakeApi(mutableListOf())
        val seq = FakeSeq(7)

        assertEquals(0, delivery(api, dao).uploadBatchLocked(recordingId, sessionId, seq))
        assertTrue(api.sent.isEmpty())
        assertEquals(7, seq.seq)
    }

    @Test fun `only the owning recording's points are ever uploaded`() = runTest {
        // The contamination bug the recordingId scoping exists to prevent: another recording's parked tail
        // sits in the same table and must not ride along into this session.
        val other = 1_600_000_000_000L
        val dao = FakeDao(points(2) + points(2, owner = other).map { it.copy(id = it.id + 100) })
        val api = FakeApi(mutableListOf(ok(inserted = 2, lastSequence = 1)))

        delivery(api, dao).uploadBatchLocked(recordingId, sessionId, FakeSeq(0))

        assertEquals(2, api.sent.single().count)
        assertEquals("the other recording's rows must survive", 2, dao.countFor(other))
    }

    // ── AQ-04: never delete on an unacknowledged batch ──────────────────────

    @Test fun `dedup-eaten batch the server is BEHIND on keeps its rows and throws`() = runTest {
        // inserted=0 with lastSequence < our seq means the server did NOT store these points. Deleting them
        // here is the silent-loss bug AQ-04 was filed for: the rows are gone and nothing ever re-sends them.
        val dao = FakeDao(points(3))
        val api = FakeApi(mutableListOf(ok(inserted = 0, lastSequence = 2)))
        val seq = FakeSeq(4)   // → nextSeq 5, server is at 2

        val thrown = runCatching { delivery(api, dao).uploadBatchLocked(recordingId, sessionId, seq) }

        assertTrue("must throw so the caller backs off", thrown.isFailure)
        assertEquals("rows must be KEPT for the retry", 3, dao.rows.size)
        assertEquals("sequence must not advance", 4, seq.seq)
    }

    @Test fun `dedup-eaten batch the server is CAUGHT UP on deletes its rows`() = runTest {
        // inserted=0 but lastSequence >= our seq: the server already has this batch (our crash lost the
        // acknowledgement, not the points). Deleting is correct — keeping them would re-send forever.
        val dao = FakeDao(points(3))
        val api = FakeApi(mutableListOf(ok(inserted = 0, lastSequence = 5)))
        val seq = FakeSeq(4)   // → nextSeq 5, server is at 5

        val stored = delivery(api, dao).uploadBatchLocked(recordingId, sessionId, seq)

        assertEquals(0, stored)
        assertTrue("acknowledged rows must be deleted", dao.rows.isEmpty())
        assertEquals(5, seq.seq)
    }

    @Test fun `a retry after a lost response must not delete rows that joined the batch since`() = runTest {
        // AND-02, the live-loop double fault: seq 7 with rows 1-40 REACHES the server (stored,
        // lastSequence=7) but the response is lost. Capture adds rows 41-100 before the retry. Re-reading
        // pending would send all 100 under seq 7; the server dedup-eats it (inserted=0, lastSequence=7),
        // and a sequence-only guard would then delete all 100 — rows 41-100 were never stored anywhere.
        // The batch composition must stay FROZEN until its sequence is acknowledged.
        val dao = FakeDao(points(40))
        val api = FakeApi(mutableListOf(
            Result.failure(IOException("response lost after the server stored the batch")),
            ok(inserted = 0, lastSequence = 7),    // dedup-eaten retry: server already has seq 7
            ok(inserted = 60, lastSequence = 8),   // the grown tail lands under the NEXT seq
        ))
        val seq = FakeSeq(6)
        val d = delivery(api, dao)

        runCatching { d.uploadBatchLocked(recordingId, sessionId, seq) }     // send whose response is lost
        dao.rows += points(60).map { it.copy(id = it.id + 40) }              // capture continues meanwhile
        d.uploadBatchLocked(recordingId, sessionId, seq)                     // retry: dedup-eaten

        assertEquals("only the 40 in-flight rows may be deleted", 60, dao.rows.size)
        assertEquals(7, seq.seq)
        assertEquals("the retry must re-send the FROZEN 40, not the grown 100",
            listOf(40, 40), api.sent.map { it.count })

        d.uploadBatchLocked(recordingId, sessionId, seq)                     // next tick delivers the tail
        assertTrue(dao.rows.isEmpty())
        assertEquals(8, seq.seq)
    }

    @Test fun `an older server that omits lastSequence does not trip the guard`() = runTest {
        // Gson defaults the absent field to 0, and the guard's `in 1 until nextSeq` range deliberately
        // excludes it — an old server must degrade to the pre-AQ-04 behaviour, not wedge the queue.
        val dao = FakeDao(points(2))
        val api = FakeApi(mutableListOf(ok(inserted = 0, lastSequence = 0)))
        val seq = FakeSeq(3)

        delivery(api, dao).uploadBatchLocked(recordingId, sessionId, seq)

        assertTrue(dao.rows.isEmpty())
        assertEquals(4, seq.seq)
    }

    // ── The 400 poison-batch drop ───────────────────────────────────────────

    @Test fun `a 400 that local re-validation can't explain drops the whole batch`() = runTest {
        // Every row here passes local re-validation (an unmirrored server rule rejected the batch). Can't
        // isolate a poison row, so fall back to the old whole-batch drop rather than retrying forever.
        // The rows must go, or the queue wedges on them forever (AQ-05); the seq must not burn, or the
        // never-accepted batch would leave a gap the server reads as missing points.
        val dao = FakeDao(points(2))
        val api = FakeApi(mutableListOf(http(400)))
        val seq = FakeSeq(6)

        val stored = delivery(api, dao).uploadBatchLocked(recordingId, sessionId, seq)

        assertEquals(0, stored)
        assertTrue("poison rows must be dropped, not retried forever", dao.rows.isEmpty())
        assertEquals("a rejected batch never burns its sequence", 6, seq.seq)
    }

    // ── AND-08: drop only the poison row(s), not the whole batch ────────────

    @Test fun `a 400 with one locally-invalid point drops only that point and retries the rest`() = runTest {
        // A legacy pre-PointEnvelope row (or a bounds-drift point) sits among otherwise-good ones. Only it
        // should be dropped; its ~100 siblings must still reach the server.
        val poisonPoint = points(1).single().copy(id = 99, lat = 999.0)
        val dao = FakeDao(points(3) + poisonPoint)
        val api = FakeApi(mutableListOf(http(400), ok(inserted = 3, lastSequence = 1)))
        val seq = FakeSeq(0)

        val stored = delivery(api, dao).uploadBatchLocked(recordingId, sessionId, seq)

        assertEquals("the 3 good points must still land", 3, stored)
        assertTrue("every row is gone: 3 delivered + 1 poison dropped", dao.rows.isEmpty())
        assertEquals(listOf(4, 3), api.sent.map { it.count })   // first attempt: all 4; retry: the 3 survivors
        assertEquals("only the accepted retry burns a sequence", 1, seq.seq)
    }

    @Test fun `a 400 with an unparseable timestamp treats that row as poison`() = runTest {
        val poisonPoint = points(1).single().copy(id = 99, timestampUtc = "not-a-timestamp")
        val dao = FakeDao(points(2) + poisonPoint)
        val api = FakeApi(mutableListOf(http(400), ok(inserted = 2, lastSequence = 1)))

        val stored = delivery(api, dao).uploadBatchLocked(recordingId, sessionId, FakeSeq(0))

        assertEquals(2, stored)
        assertTrue(dao.rows.isEmpty())
    }

    @Test fun `a 400 with an out-of-range non-null altitude is treated as poison`() = runTest {
        // altitude/speed/pressure are normally sanitized to null at capture; a value outside the server's
        // band that is somehow non-null (legacy row) still must not take its siblings down with it.
        val poisonPoint = points(1).single().copy(id = 99, altitude = 50_000.0)
        val dao = FakeDao(points(2) + poisonPoint)
        val api = FakeApi(mutableListOf(http(400), ok(inserted = 2, lastSequence = 1)))

        val stored = delivery(api, dao).uploadBatchLocked(recordingId, sessionId, FakeSeq(0))

        assertEquals(2, stored)
        assertTrue(dao.rows.isEmpty())
    }

    // ── The `watched` cadence hint ──────────────────────────────────────────

    @Test fun `watched starts optimistic and then tracks the server's answer`() = runTest {
        val dao = FakeDao(points(2))
        val api = FakeApi(mutableListOf(ok(inserted = 1, lastSequence = 1, watched = false)))
        val d = delivery(api, dao)

        assertTrue("a ride must start at the fast cadence before the server has answered", d.lastWatched)

        d.uploadBatchLocked(recordingId, sessionId, FakeSeq(0))
        assertFalse("nobody watching → the phone may idle its radio", d.lastWatched)
    }

    @Test fun `a failed append leaves the previous watched hint alone`() = runTest {
        // A throw and a 400 both mean we learned nothing about who is watching. Flipping the hint on a
        // network error would silently drop a live viewer to the 60 s cadence for the rest of the ride.
        val dao = FakeDao(points(2))
        val api = FakeApi(mutableListOf(ok(inserted = 2, lastSequence = 1, watched = true), http(500)))
        val d = delivery(api, dao)

        d.uploadBatchLocked(recordingId, sessionId, FakeSeq(0))
        assertTrue(d.lastWatched)

        dao.rows += points(2).map { it.copy(id = it.id + 50) }
        runCatching { d.uploadBatchLocked(recordingId, sessionId, FakeSeq(1)) }

        assertTrue("a 500 says nothing about viewers — keep the last known answer", d.lastWatched)
    }

    // ── drainLocked ─────────────────────────────────────────────────────────

    @Test fun `drain uploads every batch and reports the recording empty`() = runTest {
        // 250 points = 3 batches at BATCH_SIZE 100, paced 1.1 s apart on the virtual clock.
        val dao = FakeDao(points(250))
        val api = FakeApi(mutableListOf(
            ok(inserted = 100, lastSequence = 1),
            ok(inserted = 100, lastSequence = 2),
            ok(inserted = 50, lastSequence = 3),
        ))
        val seq = FakeSeq(0)

        assertTrue(delivery(api, dao).drainLocked(recordingId, sessionId, seq))

        assertEquals(listOf(1, 2, 3), api.sent.map { it.seq })
        assertEquals(listOf(100, 100, 50), api.sent.map { it.count })
        assertTrue(dao.rows.isEmpty())
        assertEquals(3, seq.seq)
    }

    @Test fun `a 429 retries the SAME sequence and then drains`() = runTest {
        // The rate limiter must not cost the batch its number: re-sending at seq+1 would leave the rejected
        // batch's points unstored under a sequence the server never sees.
        val dao = FakeDao(points(2))
        val api = FakeApi(mutableListOf(http(429), ok(inserted = 2, lastSequence = 1)))
        val seq = FakeSeq(0)

        assertTrue(delivery(api, dao).drainLocked(recordingId, sessionId, seq))

        assertEquals("both attempts carry seq 1", listOf(1, 1), api.sent.map { it.seq })
        assertTrue(dao.rows.isEmpty())
    }

    @Test fun `a permanently rate-limited server gives up bounded, keeping the points`() = runTest {
        // Bounded so a wedged server can't spin the drain forever; the points stay queued for the next pass.
        val dao = FakeDao(points(2))
        val api = FakeApi(MutableList(5) { http(429) })
        val seq = FakeSeq(0)

        assertFalse(delivery(api, dao).drainLocked(recordingId, sessionId, seq))

        assertEquals("MAX_DRAIN_ATTEMPTS consecutive 429s, then stop", 5, api.sent.size)
        assertEquals("nothing was accepted — keep every row", 2, dao.rows.size)
        assertEquals(0, seq.seq)
    }

    @Test fun `an accepted batch resets the 429 budget so a long backlog still drains`() = runTest {
        // 4 x 429 then progress, then 4 x 429 then progress: 8 limited attempts total, more than the budget
        // of 5 — proof the budget is CONSECUTIVE, or a big offline backlog would strand halfway.
        val dao = FakeDao(points(150))
        val api = FakeApi(mutableListOf(
            http(429), http(429), http(429), http(429),
            ok(inserted = 100, lastSequence = 1),
            http(429), http(429), http(429), http(429),
            ok(inserted = 50, lastSequence = 2),
        ))

        assertTrue(delivery(api, dao).drainLocked(recordingId, sessionId, FakeSeq(0)))
        assertTrue(dao.rows.isEmpty())
    }

    @Test fun `a non-429 HTTP error propagates so the caller can decide`() = runTest {
        // The queue reads a 404 as "this session is gone" and detaches; the drain must not swallow it.
        val dao = FakeDao(points(2))
        val api = FakeApi(mutableListOf(http(404)))

        val thrown = runCatching { delivery(api, dao).drainLocked(recordingId, sessionId, FakeSeq(0)) }

        assertEquals(404, (thrown.exceptionOrNull() as HttpException).code())
        assertEquals("an undelivered batch keeps its rows", 2, dao.rows.size)
    }

    @Test fun `an offline drain propagates and keeps every point`() = runTest {
        val dao = FakeDao(points(2))
        val api = FakeApi(mutableListOf(Result.failure(IOException("no network"))))

        val thrown = runCatching { delivery(api, dao).drainLocked(recordingId, sessionId, FakeSeq(0)) }

        assertTrue(thrown.exceptionOrNull() is IOException)
        assertEquals(2, dao.rows.size)
    }

    @Test fun `abortIf yields before touching the network`() = runTest {
        // A recording starting mid-drain owns the pipe: the queue must back off instantly, not after a batch.
        val dao = FakeDao(points(2))
        val api = FakeApi(mutableListOf())

        assertFalse(delivery(api, dao).drainLocked(recordingId, sessionId, FakeSeq(0), abortIf = { true }))

        assertTrue("not a single request may go out", api.sent.isEmpty())
        assertEquals(2, dao.rows.size)
    }

    @Test fun `a drain that only meets poison batches still terminates`() = runTest {
        // Every batch 400s → each drops its rows without advancing the seq. The drain must end (rows gone),
        // not spin: a 400 makes progress in the table even though it makes none in the sequence.
        val dao = FakeDao(points(150))
        val api = FakeApi(mutableListOf(http(400), http(400)))
        val seq = FakeSeq(0)

        assertTrue(delivery(api, dao).drainLocked(recordingId, sessionId, seq))

        assertTrue(dao.rows.isEmpty())
        assertEquals("no batch was ever accepted", 0, seq.seq)
    }
}
