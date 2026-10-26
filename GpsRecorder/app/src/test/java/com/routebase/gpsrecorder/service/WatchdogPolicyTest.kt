package com.routebase.gpsrecorder.service

import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * Pins the local safety-watchdog decision extracted from GpsRecordingService.startWatchdogLoop. These are
 * field-tested thresholds (AU-03 re-arm; NO_MOVE_ABORT vs IDLE_STOP as deliberately DIFFERENT outcomes),
 * so the test drives each rule at its exact boundary and each suppression condition.
 */
class WatchdogPolicyTest {

    /** An auto recording that has fixes and has moved — the steady-state a live ride sits in. */
    private fun live(
        sessionPaused: Boolean = false,
        autoDetected: Boolean = true,
        gotFix: Boolean = true,
        moved: Boolean = true,
        recordingStartTimeMs: Long = 0L,
        lastMovedMs: Long = 0L,
    ) = WatchdogState(sessionPaused, autoDetected, gotFix, moved, recordingStartTimeMs, lastMovedMs)

    private fun decide(state: WatchdogState, nowMs: Long) = WatchdogPolicy.decide(state, nowMs)

    // ── Pause skips the whole tick, beating every other rule ────────────────

    @Test fun `paused skips the tick`() {
        assertEquals(WatchdogAction.SKIP_TICK, decide(live(sessionPaused = true), nowMs = 1_000_000L))
    }

    @Test fun `pause beats an otherwise-firing abort and grace-pause`() {
        // A state that WOULD abort (no fix, long past NO_FIX_ABORT_MS) still only SKIP_TICKs while paused.
        val wouldAbort = live(sessionPaused = true, gotFix = false, recordingStartTimeMs = 1L, lastMovedMs = 1L)
        assertEquals(WatchdogAction.SKIP_TICK, decide(wouldAbort, nowMs = WatchdogPolicy.NO_FIX_ABORT_MS + 10L))
        // A state that WOULD idle-grace-pause, likewise.
        val wouldPause = live(sessionPaused = true, lastMovedMs = 1L)
        assertEquals(WatchdogAction.SKIP_TICK, decide(wouldPause, nowMs = WatchdogPolicy.IDLE_STOP_MS + 10L))
    }

    // ── No-fix abort: exactly at threshold, not one ms before ───────────────

    @Test fun `no-fix abort fires exactly at the threshold`() {
        val s = live(gotFix = false, recordingStartTimeMs = 1_000L, lastMovedMs = 0L)
        val now = 1_000L + WatchdogPolicy.NO_FIX_ABORT_MS
        assertEquals(WatchdogAction.ABORT_NO_FIX, decide(s, now))
    }

    @Test fun `no-fix abort does not fire one ms before the threshold`() {
        val s = live(gotFix = false, recordingStartTimeMs = 1_000L, lastMovedMs = 0L)
        val now = 1_000L + WatchdogPolicy.NO_FIX_ABORT_MS - 1L
        assertEquals(WatchdogAction.NONE, decide(s, now))
    }

    @Test fun `no-fix abort needs a positive base clock`() {
        // Both start and lastMoved are 0 → noFixBase 0 → the guard suppresses the abort even long after.
        val s = live(gotFix = false, recordingStartTimeMs = 0L, lastMovedMs = 0L)
        assertEquals(WatchdogAction.NONE, decide(s, nowMs = 999_999_999L))
    }

    // ── AU-03: a resume (lastMovedMs later than start) defers the no-fix twin ─

    @Test fun `AU-03 lastMovedMs later than start pushes the no-fix deadline out`() {
        // start long ago, but a resume re-armed lastMovedMs recently → base is the LATER of the two, so the
        // no-fix window is measured from the resume, not from start. Would-have-fired-on-start stays NONE.
        val start = 1_000L
        val resume = 500_000L
        val s = live(gotFix = false, recordingStartTimeMs = start, lastMovedMs = resume)
        // Past NO_FIX_ABORT_MS from START, but not yet from the RESUME → no abort.
        assertEquals(WatchdogAction.NONE, decide(s, nowMs = resume + WatchdogPolicy.NO_FIX_ABORT_MS - 1L))
        // Once NO_FIX_ABORT_MS elapses from the resume, it fires.
        assertEquals(WatchdogAction.ABORT_NO_FIX, decide(s, nowMs = resume + WatchdogPolicy.NO_FIX_ABORT_MS))
    }

    // ── False-start abort vs idle grace-pause: the gotFix/moved split ───────

    @Test fun `false-start abort fires exactly at the threshold`() {
        // Has fixes, never moved → false start. Measured from recordingStartTimeMs.
        val s = live(gotFix = true, moved = false, recordingStartTimeMs = 2_000L)
        val now = 2_000L + WatchdogPolicy.NO_MOVE_ABORT_MS
        assertEquals(WatchdogAction.ABORT_NO_MOVE, decide(s, now))
    }

    @Test fun `false-start abort does not fire one ms early`() {
        val s = live(gotFix = true, moved = false, recordingStartTimeMs = 2_000L)
        val now = 2_000L + WatchdogPolicy.NO_MOVE_ABORT_MS - 1L
        assertEquals(WatchdogAction.NONE, decide(s, now))
    }

    @Test fun `a ride that moved and then went idle grace-pauses, not aborts`() {
        // moved = true → the false-start abort is suppressed; idle-stop is measured from lastMovedMs.
        val s = live(gotFix = true, moved = true, recordingStartTimeMs = 1L, lastMovedMs = 10_000L)
        val now = 10_000L + WatchdogPolicy.IDLE_STOP_MS
        assertEquals(WatchdogAction.GRACE_PAUSE, decide(s, now))
    }

    @Test fun `idle grace-pause does not fire one ms early`() {
        val s = live(gotFix = true, moved = true, recordingStartTimeMs = 1L, lastMovedMs = 10_000L)
        val now = 10_000L + WatchdogPolicy.IDLE_STOP_MS - 1L
        assertEquals(WatchdogAction.NONE, decide(s, now))
    }

    @Test fun `never-moved outranks idle even past the idle window`() {
        // A recording that got fixes but NEVER moved, long idle: the false-start abort (checked first) wins
        // over the grace-pause — it was never a ride, so it stops outright rather than being made resumable.
        // With recordingStartTimeMs == lastMovedMs, both windows are open; order decides ABORT_NO_MOVE.
        val s = live(gotFix = true, moved = false, recordingStartTimeMs = 1_000L, lastMovedMs = 1_000L)
        val now = 1_000L + WatchdogPolicy.IDLE_STOP_MS   // well past both thresholds
        assertEquals(WatchdogAction.ABORT_NO_MOVE, decide(s, now))
    }

    // ── A manual recording is never auto-stopped ────────────────────────────

    @Test fun `manual recording suppresses all three auto rules`() {
        val now = 999_999_999L
        assertEquals(WatchdogAction.NONE, decide(live(autoDetected = false, gotFix = false, recordingStartTimeMs = 1L, lastMovedMs = 1L), now))
        assertEquals(WatchdogAction.NONE, decide(live(autoDetected = false, gotFix = true, moved = false, recordingStartTimeMs = 1L), now))
        assertEquals(WatchdogAction.NONE, decide(live(autoDetected = false, gotFix = true, moved = true, lastMovedMs = 1L), now))
    }

    // ── The no-fix vs false-start split turns on gotFix ─────────────────────

    @Test fun `no fix yet routes to no-fix abort, not false-start`() {
        // !gotFix → no-fix path; the false-start path requires gotFix. Same clock, different outcome.
        val s = live(gotFix = false, moved = false, recordingStartTimeMs = 1_000L, lastMovedMs = 1_000L)
        assertEquals(WatchdogAction.ABORT_NO_FIX, decide(s, nowMs = 1_000L + WatchdogPolicy.NO_FIX_ABORT_MS))
    }

    @Test fun `a healthy moving ride is NONE`() {
        val s = live(gotFix = true, moved = true, recordingStartTimeMs = 1L, lastMovedMs = 999_990_000L)
        assertEquals(WatchdogAction.NONE, decide(s, nowMs = 999_999_999L))
    }
}
