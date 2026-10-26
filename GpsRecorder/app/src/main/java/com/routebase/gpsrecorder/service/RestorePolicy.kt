package com.routebase.gpsrecorder.service

/** What a service (re)start must do about persisted recording state, before any intent is handled. */
enum class RestoreAction {
    /** A stop was requested and never completed — re-enter the STOP path: drain, finalize. Never record. */
    RESUME_STOP,

    /** No pending stop — handle the intent normally (reconnect / resume / pause / fresh start). */
    PROCEED,
}

/**
 * B1/AND-05: the stop path can run for tens of seconds (queue drain + stop retries), and the process can
 * die inside it. [com.routebase.gpsrecorder.util.PrefsManager.stopRequested] survives that death; this is
 * the single rule for honoring it: a capture with a pending stop may only continue STOPPING. Resurrecting
 * it as an active recording — what every restore branch would otherwise do — turns a ride the user ended
 * into one still recording hours later.
 *
 * Applied to EVERY service entry (not just the START_STICKY restart): a stale notification Resume or
 * Pause landing on a fresh process must not sidestep the pending stop either.
 *
 * An already-stopped session (nothing capturing) always PROCEEDs, even if a stop flag was orphaned by a
 * death inside a no-op stop: with no recording id the stop path has nothing to act on, and the flag is
 * cleared at the next fresh session start.
 *
 * Pure — unit-tested without Android deps, like SessionConflictPolicy.
 */
object RestorePolicy {

    fun decide(isCapturing: Boolean, stopRequested: Boolean): RestoreAction =
        if (isCapturing && stopRequested) RestoreAction.RESUME_STOP else RestoreAction.PROCEED
}
