package com.routebase.gpsrecorder.service

/**
 * The local safety-watchdog decision for one tick of an in-progress recording. Extracted verbatim from
 * [GpsRecordingService.startWatchdogLoop] so the thresholds and their short-circuit order are unit-testable
 * without Android deps — like [RestorePolicy] / [com.routebase.gpsrecorder.autodetect.CooldownPolicy].
 *
 * The GPS-loss edge log and the notification refresh are NOT decisions in this sense — one is a latch over
 * `gpsLostLogged`, one is unconditional — so they stay in the loop and run whenever this returns [NONE].
 */
enum class WatchdogAction {
    /** No abort/pause fired — fall through to the GPS-loss edge log and the notification refresh. */
    NONE,

    /** Paused: skip the whole tick (the notification already shows "Resume"). Beats every other rule. */
    SKIP_TICK,

    /** Auto recording that never captured a usable fix (indoor false start) → stop OUTRIGHT. */
    ABORT_NO_FIX,

    /** Auto recording that got fixes but never displaced 50 m (a jostled phone) → stop OUTRIGHT. */
    ABORT_NO_MOVE,

    /** Auto recording that DID move and then went idle → grace-pause (resumable, not a stop). */
    GRACE_PAUSE,
}

/** The prefs + clocks one watchdog tick reads. Plain data — no Android, no service state. */
data class WatchdogState(
    val sessionPaused: Boolean,
    val autoDetected: Boolean,
    val gotFix: Boolean,
    val moved: Boolean,
    val recordingStartTimeMs: Long,
    val lastMovedMs: Long,
)

object WatchdogPolicy {

    // Auto-detected recording with zero usable fixes by this point is an indoor false start → abort.
    const val NO_FIX_ABORT_MS = 3 * 60_000L

    // Auto-detected recording that HAS fixes but has never displaced MOVE_THRESHOLD_M by this point is
    // a false trigger → abort. Kept under COOLDOWN_STILL_MS (5 min), because the case it exists for is
    // exactly the one where AR never reports STILL and that cooldown never arms.
    const val NO_MOVE_ABORT_MS = 4 * 60_000L

    // Auto-detected recording that hasn't moved this long is over — finalize before the OS kills the
    // idle foreground service (which is what produced the 3h locker gap).
    const val IDLE_STOP_MS = 8 * 60_000L

    /**
     * Evaluated in a fixed order the field-tested rules depend on: no-fix, then false-start, then idle.
     * Every comparison operator (`>=`, `>`) and short-circuit is byte-identical to the pre-extraction loop.
     */
    fun decide(state: WatchdogState, nowMs: Long): WatchdogAction {
        // Pause freezes lastMovedMs by design, so "idle" says nothing about a paused recording — and the
        // notification is already showing "Resume". Skip the whole tick, as before.
        if (state.sessionPaused) return WatchdogAction.SKIP_TICK

        // No-fix abort: an auto-detected recording that never captured a usable GPS fix (indoor false
        // start — every fix filtered as >MAX_ACCURACY_METRES) would otherwise run to the 5-min STILL
        // cooldown burning battery and creating an empty session. Give up at 3 min; the normal stop
        // finalizes it and the server discards the 0-point auto-recording.
        // AU-03: base the no-fix window on the later of session start and the last re-arm, so a resume
        // (which pushes lastMovedMs to now) also defers this twin — not just idle-stop.
        val noFixBase = maxOf(state.recordingStartTimeMs, state.lastMovedMs)
        if (state.autoDetected && !state.gotFix &&
            noFixBase > 0 &&
            nowMs - noFixBase >= NO_FIX_ABORT_MS
        ) return WatchdogAction.ABORT_NO_FIX

        // False-start abort: an auto recording that IS getting fixes but has NEVER displaced 50 m is not a
        // workout — a phone jostled at a desk or in a bag, which AR reports as RUNNING and often never as
        // STILL, so the STILL cooldown never arms to end it. Today such a run holds high-accuracy GPS and a
        // wakelock all the way to the 8-min idle stop; this halves it.
        //
        // Distinct from IDLE_STOP_MS below, which ends a ride that DID move and then stopped — that one
        // earns the grace window so it can be resumed. This one was never a ride, so it stops outright and
        // the server's adjudicator discards the 0-distance recording.
        //
        // Window is under the 5-min STILL cooldown (so it fires in the case that cooldown misses) but far
        // above any real workout's first 50 m, which takes well under a minute.
        if (state.autoDetected && state.gotFix &&
            !state.moved && state.recordingStartTimeMs > 0 &&
            nowMs - state.recordingStartTimeMs >= NO_MOVE_ABORT_MS
        ) return WatchdogAction.ABORT_NO_MOVE

        // Idle auto-stop: an auto session that hasn't moved (or has had no fixes at all — locker / indoors)
        // for IDLE_STOP_MS is over. Grace-pausing here (instead of finalizing outright) ends GPS/battery use
        // before the phone sits long enough to be OS-killed, while a resume within the grace window
        // continues the same activity; the finalize worker (which survives process death) does the real stop.
        if (state.autoDetected && state.gotFix &&
            state.lastMovedMs > 0 &&
            nowMs - state.lastMovedMs >= IDLE_STOP_MS
        ) return WatchdogAction.GRACE_PAUSE

        return WatchdogAction.NONE
    }
}
