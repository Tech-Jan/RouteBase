package com.routebase.gpsrecorder.service

import com.routebase.gpsrecorder.data.SessionLiveness
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins AND-41's pure decision: a web-mirrored pause re-probes on a WIDE interval and clears only on ACTIVE;
 * a LOCAL pause (user tap / grace) never re-probes and so is never auto-resumed (constraint 1).
 */
class WebPausePolicyTest {

    // ── shouldProbe: only a web-mirrored pause, only on the wide interval ────

    @Test fun `a local pause never re-probes, even after a long wait`() {
        // Constraint 1: pausedByWeb = false → a user tap or grace-pause is never auto-resumed.
        assertFalse(WebPausePolicy.shouldProbe(pausedByWeb = false, msSinceLastProbe = 0L))
        assertFalse(WebPausePolicy.shouldProbe(pausedByWeb = false, msSinceLastProbe = 10 * WebPausePolicy.REPROBE_INTERVAL_MS))
    }

    @Test fun `a web pause does not re-probe before the wide interval elapses`() {
        // Constraint 2: the probe is a network call — it must not ride the paused loop's fast flag-check.
        assertFalse(WebPausePolicy.shouldProbe(pausedByWeb = true, msSinceLastProbe = 0L))
        assertFalse(WebPausePolicy.shouldProbe(pausedByWeb = true, msSinceLastProbe = WebPausePolicy.REPROBE_INTERVAL_MS - 1L))
    }

    @Test fun `a web pause re-probes exactly at the interval and after`() {
        assertTrue(WebPausePolicy.shouldProbe(pausedByWeb = true, msSinceLastProbe = WebPausePolicy.REPROBE_INTERVAL_MS))
        assertTrue(WebPausePolicy.shouldProbe(pausedByWeb = true, msSinceLastProbe = WebPausePolicy.REPROBE_INTERVAL_MS + 1L))
    }

    // ── shouldClearPause: only ACTIVE resumes ───────────────────────────────

    @Test fun `only an ACTIVE verdict clears the web pause`() {
        assertTrue(WebPausePolicy.shouldClearPause(SessionLiveness.ACTIVE))
    }

    @Test fun `a still-paused or ambiguous verdict keeps the pause`() {
        // WEB_PAUSED: the web hasn't resumed yet. DEAD / UNKNOWN: never resurrect uploads onto a gone or
        // unverified session — leave it paused and re-probe later.
        assertFalse(WebPausePolicy.shouldClearPause(SessionLiveness.WEB_PAUSED))
        assertFalse(WebPausePolicy.shouldClearPause(SessionLiveness.DEAD))
        assertFalse(WebPausePolicy.shouldClearPause(SessionLiveness.UNKNOWN))
    }

    // ── The interval is wide, not the active upload cadence ─────────────────

    @Test fun `the re-probe interval is minutes, well above the active upload cadence`() {
        // Guards against a future edit collapsing it toward the 5 s active cadence (the battery regression
        // the mirror exists to avoid). 3 min here; assert it stays clearly in the minutes range.
        assertEquals(3 * 60_000L, WebPausePolicy.REPROBE_INTERVAL_MS)
        assertTrue(WebPausePolicy.REPROBE_INTERVAL_MS >= 60_000L)
    }
}
