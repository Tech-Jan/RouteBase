package com.routebase.gpsrecorder.work

/**
 * The upload-cadence decision for one [UploadWorker] tick. Extracted verbatim from [UploadWorker.doWork]
 * so the dead-service park budget and its short-circuit order are unit-testable without WorkManager, the
 * Retrofit/Room I/O, or the `GpsRecordingService.isServiceActive` static — like [FinalizeDecision] and
 * [com.routebase.gpsrecorder.service.WatchdogPolicy].
 *
 * PURE: it reads a snapshot ([UploadState]) and the clock and returns an [UploadAction]. It NEVER mutates
 * the dead-service counters — the worker stamps `lastDeadServiceTickMs`/`deadServiceTicks` on the tick and
 * resets them only after a successful park (AND-26). The Retrofit/Room flush, the `Result` mapping, the
 * `EventLog`/`Log` lines, and the AND-06 retry-abandon pass all stay in the worker.
 */
enum class UploadAction {
    /** Not configured yet (no server URL / API key) — nothing to deliver. Short-circuits before AND-06. */
    NOT_CONFIGURED,

    /** A live recording is capturing with its service alive — it owns the server's single active-session
     * slot, so a worker that woke mid-ride must not touch the queue. Retry, and reset the dead-service
     * budget (the service is confirmed alive). */
    YIELD_TO_LIVE_SERVICE,

    /** Nothing capturing per prefs, but a service is still up (mid-teardown) — wait rather than flush into
     * the race. Retry WITHOUT touching the dead-service budget. */
    WAIT_FOR_SERVICE_STOP,

    /** Capturing with no live service, but the last counted observation was too recent — the spacing gate
     * blocks this wakeup from burning budget. Retry, count nothing. */
    WAIT_FOR_TICK_SPACING,

    /** Capturing with no live service, spacing satisfied, budget not yet spent — count this observation
     * (worker stamps the tick) and retry, still hoping the service comes back. */
    OBSERVE_TICK,

    /** Capturing with no live service and the spaced-tick budget is spent — the capture is abandoned; hand
     * it to the upload queue like any other undelivered recording. */
    PARK_STUCK_CAPTURE,

    /** Nothing owns the active-session slot — drain the upload queue. */
    FLUSH,
}

/** The prefs + service-liveness snapshot one upload tick reads. Plain data — no Android, no WorkManager. */
data class UploadState(
    val isConfigured: Boolean,
    val isCapturing: Boolean,
    val isServiceActive: Boolean,
    val deadServiceTicks: Int,
    val lastDeadServiceTickMs: Long,
)

object UploadDecision {

    // AND-07: dead-service observations that find a capturing recording with no live service before giving
    // up on it ever coming back and parking it. Bounded so a genuinely dead capture doesn't block the whole
    // queue forever behind a comment that used to just claim this happened; long enough that a brief
    // START_STICKY restart delay is never mistaken for a permanently dead service.
    const val DEAD_SERVICE_PARK_THRESHOLD = 3

    // AND-04: observations must be SPACED IN TIME, not merely counted. Result.retry() re-runs this worker
    // under exponential backoff (initial ~30 s), so three RUNS can accumulate ~90 seconds after a kill — not
    // the ~90 minutes of independent half-hour ticks the threshold was sized for. 25 min admits the 30-min
    // periodic cadence (with jitter) and makes the park budget ≥ ~50 min of genuinely dead service.
    const val DEAD_SERVICE_TICK_SPACING_MS = 25L * 60 * 1000

    /**
     * Evaluated in the exact order the pre-extraction loop used. Every comparison operator (`<`) and
     * short-circuit is byte-identical to [UploadWorker.doWork] before the extraction.
     */
    fun decide(state: UploadState, nowMs: Long): UploadAction {
        if (!state.isConfigured) return UploadAction.NOT_CONFIGURED

        if (state.isCapturing) {
            // A live recording owns the server's single active-session slot.
            if (state.isServiceActive) return UploadAction.YIELD_TO_LIVE_SERVICE

            // AND-07: prefs say "capturing" but no service is alive to ever finish it — mid-restart
            // (START_STICKY), or dead for good. The old code retried this forever, which blocked the WHOLE
            // queue (this recording will never capture again) behind a comment claiming it would eventually
            // park it; it never did. Give the service a bounded number of SPACED observations to
            // legitimately come back before treating the capture as abandoned and handing it to the queue
            // like any other undelivered recording.
            // AND-04: a burst of wakeups inside the spacing window must NOT burn the budget.
            if (nowMs - state.lastDeadServiceTickMs < DEAD_SERVICE_TICK_SPACING_MS) {
                return UploadAction.WAIT_FOR_TICK_SPACING
            }
            // Spacing satisfied → this observation counts. The worker stamps lastDeadServiceTickMs = nowMs
            // and increments the budget for BOTH branches below; parking happens only once the incremented
            // count reaches the threshold (`deadServiceTicks + 1` is the post-increment value the worker
            // will persist).
            if (state.deadServiceTicks + 1 < DEAD_SERVICE_PARK_THRESHOLD) return UploadAction.OBSERVE_TICK
            return UploadAction.PARK_STUCK_CAPTURE
        }

        // Nothing capturing, but a service is still up — let it finish tearing down before we flush.
        if (state.isServiceActive) return UploadAction.WAIT_FOR_SERVICE_STOP

        return UploadAction.FLUSH
    }
}
