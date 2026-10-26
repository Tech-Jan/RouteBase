package com.routebase.gpsrecorder.work

import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * Pins the upload-cadence decision extracted from UploadWorker.doWork. Three rules carry real incident
 * history and are driven at their exact boundaries:
 *  - AND-07 — a capture whose service died must PARK after a bounded number of observations, not retry
 *    forever (the old bug blocked the whole queue).
 *  - AND-04 — those observations must be SPACED IN TIME; a burst of wakeups inside the window must not
 *    burn the budget.
 *  - AND-26 — decide never clears the budget (the worker resets only after a successful park), so a spent
 *    budget keeps parking instead of restarting the ~50-min climb after every transient failure.
 */
class UploadDecisionTest {

    private fun state(
        isConfigured: Boolean = true,
        isCapturing: Boolean = true,
        isServiceActive: Boolean = false,
        deadServiceTicks: Int = 0,
        lastDeadServiceTickMs: Long = 0L,
    ) = UploadState(isConfigured, isCapturing, isServiceActive, deadServiceTicks, lastDeadServiceTickMs)

    private val spacing = UploadDecision.DEAD_SERVICE_TICK_SPACING_MS
    private val parkThreshold = UploadDecision.DEAD_SERVICE_PARK_THRESHOLD

    private fun decide(state: UploadState, nowMs: Long) = UploadDecision.decide(state, nowMs)

    // ── Not configured short-circuits everything ────────────────────────────

    @Test fun `not configured wins over every other signal`() {
        // Even a state that would otherwise park stays NOT_CONFIGURED — the gate is first, before AND-06.
        val loaded = state(isConfigured = false, isServiceActive = true, deadServiceTicks = 99, lastDeadServiceTickMs = 1L)
        assertEquals(UploadAction.NOT_CONFIGURED, decide(loaded, nowMs = 1_700_000_000_000L))
        assertEquals(UploadAction.NOT_CONFIGURED, decide(state(isConfigured = false, isCapturing = false), nowMs = 1L))
    }

    // ── An active service owns the single session slot ──────────────────────

    @Test fun `a live recording yields the queue`() {
        // isCapturing && isServiceActive → yield (the worker also resets the dead-service budget here).
        assertEquals(UploadAction.YIELD_TO_LIVE_SERVICE, decide(state(isCapturing = true, isServiceActive = true, deadServiceTicks = 2), nowMs = 1_700_000_000_000L))
    }

    @Test fun `nothing capturing but a service still up waits for teardown`() {
        // !isCapturing && isServiceActive → wait (distinct from YIELD: the worker must NOT reset here).
        assertEquals(UploadAction.WAIT_FOR_SERVICE_STOP, decide(state(isCapturing = false, isServiceActive = true), nowMs = 1L))
    }

    @Test fun `nothing owns the slot flushes the queue`() {
        assertEquals(UploadAction.FLUSH, decide(state(isCapturing = false, isServiceActive = false), nowMs = 1L))
    }

    // ── AND-04: observations must be spaced in time ─────────────────────────

    @Test fun `a wakeup one ms inside the spacing window does not count`() {
        val last = 100_000_000L
        val s = state(isCapturing = true, deadServiceTicks = 0, lastDeadServiceTickMs = last)
        assertEquals(UploadAction.WAIT_FOR_TICK_SPACING, decide(s, nowMs = last + spacing - 1))
    }

    @Test fun `a burst inside the window never tips a nearly-spent budget into a park`() {
        // deadServiceTicks one short of the threshold, but the wakeup is inside the spacing window: it must
        // stay WAIT, never PARK. This is the AND-04 bug — a flurry of ~30 s retry wakeups burning the budget.
        val last = 100_000_000L
        val s = state(isCapturing = true, deadServiceTicks = parkThreshold - 1, lastDeadServiceTickMs = last)
        assertEquals(UploadAction.WAIT_FOR_TICK_SPACING, decide(s, nowMs = last + spacing - 1))
    }

    @Test fun `the first spaced observation counts exactly at the spacing boundary`() {
        // now - last == spacing is NOT < spacing, so it counts (deadServiceTicks 0 → still observing).
        val last = 100_000_000L
        val s = state(isCapturing = true, deadServiceTicks = 0, lastDeadServiceTickMs = last)
        assertEquals(UploadAction.OBSERVE_TICK, decide(s, nowMs = last + spacing))
    }

    @Test fun `a fresh dead capture's first observation is never gated by spacing`() {
        // lastDeadServiceTickMs 0 with a real wall clock is always far past the spacing window → count now.
        val s = state(isCapturing = true, deadServiceTicks = 0, lastDeadServiceTickMs = 0L)
        assertEquals(UploadAction.OBSERVE_TICK, decide(s, nowMs = 1_700_000_000_000L))
    }

    // ── AND-07: the spaced-tick budget parks a stuck capture ────────────────

    @Test fun `observations below the park threshold keep observing`() {
        val last = 100_000_000L
        val now = last + spacing   // spacing satisfied
        assertEquals(UploadAction.OBSERVE_TICK, decide(state(isCapturing = true, deadServiceTicks = 0, lastDeadServiceTickMs = last), now))
        assertEquals(UploadAction.OBSERVE_TICK, decide(state(isCapturing = true, deadServiceTicks = parkThreshold - 2, lastDeadServiceTickMs = last), now))
    }

    @Test fun `the budget parks the stuck capture once the last spaced observation lands`() {
        // deadServiceTicks == threshold - 1 → the post-increment value reaches the threshold → PARK.
        val last = 100_000_000L
        val s = state(isCapturing = true, deadServiceTicks = parkThreshold - 1, lastDeadServiceTickMs = last)
        assertEquals(UploadAction.PARK_STUCK_CAPTURE, decide(s, nowMs = last + spacing))
    }

    // ── AND-26: decide never self-clears — a spent budget keeps parking ──────

    @Test fun `a spent budget keeps parking rather than restarting the climb`() {
        // After a FAILED park the worker leaves the counters at the threshold (AND-26). The next spaced
        // observation must PARK again immediately, not fall back to OBSERVE and re-climb ~50 min. decide is
        // pure and reads the (still-spent) budget, so it returns PARK for any count at or above threshold.
        val last = 100_000_000L
        val now = last + spacing
        assertEquals(UploadAction.PARK_STUCK_CAPTURE, decide(state(isCapturing = true, deadServiceTicks = parkThreshold, lastDeadServiceTickMs = last), now))
        assertEquals(UploadAction.PARK_STUCK_CAPTURE, decide(state(isCapturing = true, deadServiceTicks = parkThreshold + 2, lastDeadServiceTickMs = last), now))
    }
}
