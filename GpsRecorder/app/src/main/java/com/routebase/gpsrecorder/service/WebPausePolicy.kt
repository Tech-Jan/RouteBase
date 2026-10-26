package com.routebase.gpsrecorder.service

import com.routebase.gpsrecorder.data.SessionLiveness

/**
 * AND-41: once the upload loop mirrors a web-side pause locally (sessionPaused = true, see
 * [GpsRecordingService.startUploadLoop]'s WEB_PAUSED branch), its own next line short-circuits on
 * sessionPaused and the 404 probe never runs again — so a later web-side RESUME is invisible and uploads
 * stall until the user taps Resume on the phone. Nothing is lost (GPS keeps capturing, points queue), so
 * this is a stall to unstick, not a data bug.
 *
 * This is the pure half: given (pausedByWeb, ms since the last probe, and a probe verdict) decide whether
 * to re-probe this tick and whether to clear the pause. Unit-tested without Android deps, like
 * [RestorePolicy] / [com.routebase.gpsrecorder.autodetect.CooldownPolicy]. The wiring — issuing the probe,
 * flipping the prefs, refreshing the notification — stays in the loop.
 */
object WebPausePolicy {

    /**
     * Re-probe interval while web-paused. WIDE by design: the probe is a network call
     * ([com.routebase.gpsrecorder.data.RecordingRepository.classifyActiveSession]) and the whole point of
     * mirroring the pause was to stop hammering the radio (startUploadLoop's cadence comments). A few
     * minutes' latency to notice a web RESUME is fine; keeping the modem awake is not.
     */
    const val REPROBE_INTERVAL_MS = 3 * 60_000L

    /**
     * Should the loop issue a liveness probe this tick? Only a WEB-mirrored pause re-probes — a LOCAL pause
     * (user tap or auto grace-pause) has pausedByWeb = false and is NEVER auto-resumed. Gated on a wide
     * interval so the probe can't ride the paused loop's fast flag-check cadence.
     */
    fun shouldProbe(pausedByWeb: Boolean, msSinceLastProbe: Long): Boolean =
        pausedByWeb && msSinceLastProbe >= REPROBE_INTERVAL_MS

    /**
     * Given a probe verdict, should the web-mirrored pause be cleared (resume uploading here too)? Only an
     * ACTIVE session means the web resumed it. WEB_PAUSED keeps the mirror; DEAD and UNKNOWN are ambiguous
     * — leave the pause in place and re-probe later rather than resurrect uploads onto a gone session.
     */
    fun shouldClearPause(verdict: SessionLiveness): Boolean = verdict == SessionLiveness.ACTIVE
}
