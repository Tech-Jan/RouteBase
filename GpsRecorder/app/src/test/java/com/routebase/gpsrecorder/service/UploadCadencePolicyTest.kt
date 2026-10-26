package com.routebase.gpsrecorder.service

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins the upload-loop cadence decision extracted from GpsRecordingService.startUploadLoop. Cadence
 * governs how often the phone wakes the modem (battery) and how promptly a live viewer sees new points,
 * and had NO tests before this extraction — so this drives the WATCH_DECAY hysteresis at its exact
 * `< vs <=` boundary, the failure backoff through its doubling to the clamp, and the AND-05 dead-session
 * predicate at threshold-minus-one vs threshold. Mirrors WatchdogPolicyTest's shape.
 */
class UploadCadencePolicyTest {

    private val ACTIVE = UploadCadencePolicy.UPLOAD_INTERVAL_ACTIVE_MS   // 5 s
    private val IDLE = UploadCadencePolicy.UPLOAD_INTERVAL_IDLE_MS       // 60 s
    private val DECAY = UploadCadencePolicy.WATCH_DECAY_MS               // 2 min
    private val CAP = UploadCadencePolicy.MAX_UPLOAD_INTERVAL_MS         // 60 s

    private fun next(success: Boolean, lastWatchedMs: Long, nowMs: Long, currentDelayMs: Long) =
        UploadCadencePolicy.nextDelay(success, lastWatchedMs, nowMs, currentDelayMs)

    // ── Watched-recently keeps the fast cadence ─────────────────────────────

    @Test fun `a watched success (stamped now) stays on the active cadence`() {
        // lastWatchedMs == now → 0 ms since last watched → deep inside the decay window → ACTIVE, and it
        // ignores the (idle) currentDelay it was called with.
        assertEquals(ACTIVE, next(success = true, lastWatchedMs = 1_000_000L, nowMs = 1_000_000L, currentDelayMs = IDLE))
    }

    @Test fun `watched mid-window stays active`() {
        val now = 1_000_000L
        assertEquals(ACTIVE, next(success = true, lastWatchedMs = now - DECAY / 2, nowMs = now, currentDelayMs = ACTIVE))
    }

    // ── Crossing WATCH_DECAY_MS decays to idle — the `< vs <=` boundary ──────

    @Test fun `one ms before the decay boundary is still active`() {
        val now = 1_000_000L
        assertEquals(ACTIVE, next(success = true, lastWatchedMs = now - (DECAY - 1), nowMs = now, currentDelayMs = ACTIVE))
    }

    @Test fun `exactly at the decay boundary has already decayed to idle`() {
        // now - lastWatched == DECAY; the policy uses `< DECAY`, so this is FALSE → IDLE. Flip the operator
        // to `<=` and this test goes RED — that is the whole point of pinning the exact boundary.
        val now = 1_000_000L
        assertEquals(IDLE, next(success = true, lastWatchedMs = now - DECAY, nowMs = now, currentDelayMs = ACTIVE))
    }

    @Test fun `long after the last watch is idle`() {
        assertEquals(IDLE, next(success = true, lastWatchedMs = 0L, nowMs = 10 * DECAY, currentDelayMs = ACTIVE))
    }

    // ── Failure doubles and clamps at the ceiling ───────────────────────────

    @Test fun `failure doubles the current delay`() {
        assertEquals(2 * ACTIVE, next(success = false, lastWatchedMs = 0L, nowMs = 0L, currentDelayMs = ACTIVE))
    }

    @Test fun `failure doubling clamps at the ceiling`() {
        assertEquals(CAP, next(success = false, lastWatchedMs = 0L, nowMs = 0L, currentDelayMs = CAP / 2))    // 30s → 60s exactly
        assertEquals(CAP, next(success = false, lastWatchedMs = 0L, nowMs = 0L, currentDelayMs = 40_000L))    // 80s → clamped 60s
        assertEquals(CAP, next(success = false, lastWatchedMs = 0L, nowMs = 0L, currentDelayMs = CAP))        // already capped → stays
    }

    @Test fun `failure backoff is a repeated-doubling walk to the cap`() {
        var d = ACTIVE
        d = next(false, 0L, 0L, d); assertEquals(10_000L, d)
        d = next(false, 0L, 0L, d); assertEquals(20_000L, d)
        d = next(false, 0L, 0L, d); assertEquals(40_000L, d)
        d = next(false, 0L, 0L, d); assertEquals(CAP, d)      // 80s clamped to 60s
        d = next(false, 0L, 0L, d); assertEquals(CAP, d)      // stays clamped
    }

    @Test fun `failure ignores the watch clocks entirely`() {
        // Even "watched right now", a FAILED tick backs off — cadence only reads watched-ness on success.
        assertEquals(2 * ACTIVE, next(success = false, lastWatchedMs = 999L, nowMs = 999L, currentDelayMs = ACTIVE))
    }

    // ── A success resets the backoff (ignores currentDelay) ─────────────────

    @Test fun `a watched success resets an inflated backoff to active`() {
        // currentDelay sitting at the failure cap; a watched success ignores it and returns to ACTIVE.
        assertEquals(ACTIVE, next(success = true, lastWatchedMs = 5_000L, nowMs = 5_000L, currentDelayMs = CAP))
    }

    @Test fun `an unwatched success resets backoff to idle, not the inflated delay`() {
        assertEquals(IDLE, next(success = true, lastWatchedMs = 0L, nowMs = 10 * DECAY, currentDelayMs = CAP))
    }

    // ── AND-05 dead-session predicate: threshold-minus-one vs threshold ──────

    @Test fun `isDead404 is false below the threshold`() {
        assertFalse(UploadCadencePolicy.isDead404(UploadCadencePolicy.UPLOAD_DEAD_404_THRESHOLD - 1))
        assertFalse(UploadCadencePolicy.isDead404(0))
    }

    @Test fun `isDead404 fires exactly at the threshold`() {
        assertTrue(UploadCadencePolicy.isDead404(UploadCadencePolicy.UPLOAD_DEAD_404_THRESHOLD))
    }

    @Test fun `isDead404 stays true above the threshold`() {
        assertTrue(UploadCadencePolicy.isDead404(UploadCadencePolicy.UPLOAD_DEAD_404_THRESHOLD + 1))
    }
}
