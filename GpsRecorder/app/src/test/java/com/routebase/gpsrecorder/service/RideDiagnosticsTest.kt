package com.routebase.gpsrecorder.service

import com.routebase.gpsrecorder.util.BatterySnapshot
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins the ride power instrument. This one is unusual: its output is READ BY A HUMAN to decide what to
 * optimise next, so a wrong number is worse than no number — it would send the next change at the wrong
 * target. Hence the emphasis on the cases that produce misleading readings if unguarded: a charged ride
 * (level can RISE, so a drain rate is nonsense), a ride too short to extrapolate, a suspended process
 * banking time it never spent, and the double-summary a clean stop plus onDestroy would otherwise write.
 *
 * Mirrors UploadCadencePolicyTest's shape — every clock is injected, no Android, no Robolectric.
 */
class RideDiagnosticsTest {

    private val t0 = 1_000_000L
    private val minute = 60_000L

    private fun sample(
        level: Int = 90,
        tempC: Float = 30f,
        charging: Boolean = false,
        screenOn: Boolean = false,
    ) = BatterySnapshot(level, tempC, charging, screenOn)

    private fun started(): RideDiagnostics = RideDiagnostics().apply { reset(t0) }

    // ── The drain rate: the number the whole instrument exists for ──────────

    @Test fun `drain rate is battery drop extrapolated to an hour`() {
        val d = started()
        d.onTick(sample(level = 90, tempC = 28f), t0)
        // 30 min later, 6 points lower → 12 %/hr.
        d.onTick(sample(level = 84, tempC = 34f), t0 + 30 * minute)
        val summary = d.summaryOnce(t0 + 30 * minute)!!
        assertTrue("expected 12.0%/hr in: $summary", summary.contains("12.0%/hr"))
        assertTrue("expected the level span in: $summary", summary.contains("90%→84%"))
    }

    @Test fun `a ride that was ever on charge reports no rate rather than a flattered one`() {
        val d = started()
        d.onTick(sample(level = 60, charging = true), t0)
        d.onTick(sample(level = 75, charging = false), t0 + 30 * minute)
        val summary = d.summaryOnce(t0 + 30 * minute)!!
        // The level ROSE. Reporting -30%/hr, or worse a positive drain, would be a lie either way.
        assertTrue("expected the charged caveat in: $summary", summary.contains("n/a, charged"))
    }

    @Test fun `a ride too short to extrapolate reports no rate`() {
        val d = started()
        d.onTick(sample(level = 90), t0)
        d.onTick(sample(level = 89), t0 + minute)
        // 1% over 1 min extrapolates to 60%/hr — arithmetically true, physically meaningless.
        assertTrue(d.summaryOnce(t0 + minute)!!.contains("n/a, too short"))
    }

    @Test fun `a ride with no readings at all reports n a, not zero`() {
        val d = started()
        d.onTick(null, t0 + minute)
        val summary = d.summaryOnce(t0 + 10 * minute)!!
        assertTrue("expected n/a levels in: $summary", summary.contains("batt n/a→n/a"))
        assertTrue("expected n/a temp in: $summary", summary.contains("temp n/a→n/a"))
    }

    // ── Temperature ────────────────────────────────────────────────────────

    @Test fun `peak temperature survives a later cool-down`() {
        val d = started()
        d.onTick(sample(tempC = 28.4f), t0)
        d.onTick(sample(tempC = 41.2f), t0 + 20 * minute)
        d.onTick(sample(tempC = 39.1f), t0 + 40 * minute)
        val summary = d.summaryOnce(t0 + 40 * minute)!!
        assertTrue("expected first→last in: $summary", summary.contains("28.4C→39.1C"))
        assertTrue("expected the peak in: $summary", summary.contains("peak 41.2C"))
    }

    // ── Cadence attribution — the live-viewer hypothesis ────────────────────

    @Test fun `time lands on the cadence that actually spent it, not the next one`() {
        val d = started()
        // Two 5 s ticks, then the cadence widens to 60 s for one tick. The 60 s interval must be
        // credited with the 60 s wait that PRECEDED the switch decision, not with the 5 s ones.
        d.onUpload(success = true, points = 5, intervalMs = 5_000L, nowMs = t0 + 5_000L)
        d.onUpload(success = true, points = 5, intervalMs = 5_000L, nowMs = t0 + 10_000L)
        d.onUpload(success = true, points = 60, intervalMs = 60_000L, nowMs = t0 + 70_000L)
        val summary = d.summaryOnce(t0 + 70_000L)!!
        // 10 s at 5 s and 60 s at 60 s, over a 70 s ride → 14% / 85%.
        assertTrue("expected both cadences in: $summary", summary.contains("cadence 5s 14% / 60s 85%"))
        assertTrue("expected the point total in: $summary", summary.contains("pts 70"))
    }

    @Test fun `failed uploads are counted separately and contribute no points`() {
        val d = started()
        d.onUpload(success = true, points = 60, intervalMs = 60_000L, nowMs = t0 + minute)
        d.onUpload(success = false, points = 0, intervalMs = 60_000L, nowMs = t0 + 2 * minute)
        d.onUpload(success = false, points = 0, intervalMs = 60_000L, nowMs = t0 + 3 * minute)
        val summary = d.summaryOnce(t0 + 3 * minute)!!
        assertTrue("expected the ok/fail split in: $summary", summary.contains("uploads ok 1 fail 2"))
        assertTrue("expected only delivered points in: $summary", summary.contains("pts 60"))
    }

    // ── Suspended-process guard ────────────────────────────────────────────

    @Test fun `a long gap between ticks is clamped, not banked`() {
        val d = started()
        d.onTick(sample(screenOn = true), t0)
        // The loop did not run for an hour (process suspended). Screen-on time must not absorb it: the
        // phone was not observed in that state, and an unclamped fold would blame the display for a ride
        // it slept through.
        d.onTick(sample(screenOn = true), t0 + 60 * minute)
        val summary = d.summaryOnce(t0 + 60 * minute)!!
        // 2 min clamp over a 60 min ride → 3%.
        assertTrue("expected the clamped share in: $summary", summary.contains("scr 3%"))
    }

    @Test fun `charging and screen time are attributed to the state that preceded the tick`() {
        val d = started()
        d.onTick(sample(charging = true, screenOn = true), t0)
        // One minute spent charging+screen-on, then both go off for a minute.
        d.onTick(sample(charging = false, screenOn = false), t0 + minute)
        d.onTick(sample(charging = false, screenOn = false), t0 + 2 * minute)
        val summary = d.summaryOnce(t0 + 2 * minute)!!
        assertTrue("expected half the ride charging in: $summary", summary.contains("chg 50%"))
        assertTrue("expected half the ride screen-on in: $summary", summary.contains("scr 50%"))
    }

    // ── Checkpoints ────────────────────────────────────────────────────────

    @Test fun `a checkpoint is emitted every ten minutes and not between`() {
        val d = started()
        assertNull("no checkpoint before the window elapses", d.onTick(sample(), t0 + 9 * minute))
        val first = d.onTick(sample(level = 85, tempC = 36.2f), t0 + 10 * minute)
        assertNotNull("a checkpoint is due at ten minutes", first)
        assertTrue("expected elapsed + level + temp in: $first", first!!.contains("+10m"))
        assertTrue("expected the level in: $first", first.contains("batt 85%"))
        assertTrue("expected the temp in: $first", first.contains("temp 36.2C"))
        assertNull("the window restarts after a checkpoint", d.onTick(sample(), t0 + 11 * minute))
        assertNotNull("and fires again a window later", d.onTick(sample(), t0 + 20 * minute))
    }

    // ── The summary latch ──────────────────────────────────────────────────

    @Test fun `the summary is emitted once, so stop plus onDestroy does not double-log`() {
        val d = started()
        d.onTick(sample(), t0 + minute)
        assertNotNull("the first caller gets the summary", d.summaryOnce(t0 + 10 * minute))
        assertNull("the second caller gets nothing", d.summaryOnce(t0 + 10 * minute))
    }

    // ── Killed mid-ride: the instance that never saw reset() ────────────────

    @Test fun `a tick on an unseeded instance does not log a checkpoint from the epoch`() {
        // The OS kills the service mid-ride; START_STICKY brings it back and the reconnect path does NOT
        // call reset(). With zero clocks the first tick is ~55 years past due and logged `+29257917m`.
        val d = RideDiagnostics()
        assertNull("the seeding tick must not also fire a checkpoint", d.onTick(sample(), t0))
        val due = d.onTick(sample(), t0 + 10 * minute)
        assertEquals("+10m", due!!.substringBefore(" "))
    }

    @Test fun `a ride restarted mid-way is summarised, and says so`() {
        val d = RideDiagnostics()
        d.onTick(sample(level = 80), t0)
        d.onTick(sample(level = 74), t0 + 30 * minute)
        val summary = d.summaryOnce(t0 + 30 * minute)!!
        // Measuring the tail is useful; presenting it as a whole ride is not — 12%/hr over an unknown
        // fraction of the ride is exactly the number that would send the next change at the wrong target.
        assertTrue("expected the partial label in: $summary", summary.contains("(partial, restarted mid-ride)"))
        assertTrue("the tail is still measured: $summary", summary.contains("12.0%/hr"))
    }

    @Test fun `a reset ride is not labelled partial`() {
        val d = started()
        d.onTick(sample(level = 80), t0)
        assertTrue(!d.summaryOnce(t0 + 10 * minute)!!.contains("partial"))
    }

    @Test fun `a diagnostics that never started produces no summary`() {
        // A service instance that came up for a reconnect that then failed has no ride to report; a
        // zero-start summary would read as a 55-year ride from the epoch.
        assertNull(RideDiagnostics().summaryOnce(t0))
    }

    @Test fun `reset clears a previous ride`() {
        val d = started()
        d.onUpload(success = true, points = 500, intervalMs = 5_000L, nowMs = t0 + 5_000L)
        d.onTick(sample(level = 90), t0 + minute)
        assertNotNull(d.summaryOnce(t0 + minute))

        d.reset(t0 + 60 * minute)
        d.onTick(sample(level = 50), t0 + 61 * minute)
        val summary = d.summaryOnce(t0 + 70 * minute)!!
        assertTrue("the new ride must not inherit points: $summary", summary.contains("pts 0"))
        assertTrue("nor the old level: $summary", summary.contains("batt 50%→50%"))
        assertTrue("nor the old cadence: $summary", summary.contains("cadence n/a"))
    }

    // ── Formatting helpers ─────────────────────────────────────────────────

    @Test fun `intervals render as the constants a reader recognises`() {
        assertEquals("5s", formatInterval(5_000L))
        assertEquals("60s", formatInterval(60_000L))
        assertEquals("5m", formatInterval(5 * minute))
        assertEquals("n/a", formatInterval(0L))
    }

    @Test fun `durations render hours only when there are hours`() {
        assertEquals("47m", formatDuration(47 * minute))
        assertEquals("1h47m", formatDuration(107 * minute))
        assertEquals("0m", formatDuration(0L))
    }
}
