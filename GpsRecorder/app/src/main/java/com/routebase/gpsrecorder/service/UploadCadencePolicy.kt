package com.routebase.gpsrecorder.service

/**
 * The NETWORK upload loop's per-tick delay decision, extracted verbatim from
 * [GpsRecordingService.startUploadLoop] so the cadence math — the WATCH_DECAY hysteresis and the failure
 * backoff — is unit-testable without Android deps, like [WatchdogPolicy] / [WebPausePolicy].
 *
 * Time is an INJECTED parameter, never a `System.currentTimeMillis()` call inside the policy: a policy
 * that reads the clock cannot be table-tested, which is the whole point. The caller stamps [lastWatchedMs]
 * from the repository's `lastWatched` flag before calling; this only turns the resulting state into the
 * next delay. Stamping and the `lastWatched` read stay in the loop (they touch the repository / a live
 * clock); the arithmetic lives here.
 */
object UploadCadencePolicy {

    // WATCHED: someone is on the live view, so freshness is worth a radio wake-up every 5 s.
    const val UPLOAD_INTERVAL_ACTIVE_MS = 5000L
    // NOT WATCHED: nobody is looking, so let the modem sleep. The saving is a cliff, not a slope —
    // an LTE radio stays in a high-power tail for ~5-10 s after each transmission, so at 5 s the next
    // upload lands before the tail expires and the radio NEVER idles. Any window comfortably past the
    // tail (~3-5x) captures most of the win; going far beyond it buys little and only makes a viewer
    // wait longer to be noticed. 60 s is that knee. Worst case a new viewer waits one window.
    const val UPLOAD_INTERVAL_IDLE_MS = 60_000L
    // Hysteresis: stay fast for this long after the last "someone is watching", so a viewer who glances
    // away and back does not hit a fresh idle-window stall each time.
    const val WATCH_DECAY_MS = 2 * 60_000L
    // The FAILURE backoff ceiling. Same value as the idle window, different meaning — do not merge them.
    const val MAX_UPLOAD_INTERVAL_MS = 60_000L

    // AND-05: consecutive append 404s before the upload loop gives up on this session. >1 so a single
    // 404 racing a legitimate resume/reconnect doesn't trip it; small enough that a truly dead session
    // stops getting hammered within a few ticks.
    const val UPLOAD_DEAD_404_THRESHOLD = 3

    /**
     * Given the tick outcome and clocks → the next loop delay. Byte-identical to the pre-extraction
     * `currentDelay = if (success) … else …` in [GpsRecordingService.startUploadLoop]: the same `<`
     * comparison for the hysteresis window, the same `* 2` doubling, the same [MAX_UPLOAD_INTERVAL_MS]
     * `coerceAtMost` cap. On failure the clocks are ignored (pure backoff).
     */
    fun nextDelay(success: Boolean, lastWatchedMs: Long, nowMs: Long, currentDelayMs: Long): Long =
        if (success) {
            // WATCH_DECAY_MS is deliberate hysteresis: a viewer who glances away and comes back must not
            // hit a fresh idle-window stall each time. `<` (not `<=`): at exactly the decay boundary the
            // cadence has already gone idle.
            val watchedRecently = nowMs - lastWatchedMs < WATCH_DECAY_MS
            if (watchedRecently) UPLOAD_INTERVAL_ACTIVE_MS else UPLOAD_INTERVAL_IDLE_MS
        } else {
            // Backoff on failure — double, capped at the ceiling.
            (currentDelayMs * 2).coerceAtMost(MAX_UPLOAD_INTERVAL_MS)
        }

    /**
     * AND-05: a run of at least [UPLOAD_DEAD_404_THRESHOLD] consecutive append 404s means the session is
     * gone (or web-paused — the caller's `when` over `classifyActiveSession()` adjudicates that). Pure
     * predicate lifted verbatim from `consecutive404s >= UPLOAD_DEAD_404_THRESHOLD`.
     */
    fun isDead404(consecutive404s: Int): Boolean = consecutive404s >= UPLOAD_DEAD_404_THRESHOLD
}
