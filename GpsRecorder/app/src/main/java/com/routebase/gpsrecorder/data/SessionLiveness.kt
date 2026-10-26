package com.routebase.gpsrecorder.data

/**
 * AND-22: what a `sessions/active` probe found when the upload loop hit a run of append 404s.
 *
 * A paused session 404s appends exactly like a gone one (see RecordingRepository.reconnectSession /
 * deliverLocked), so a 404 alone cannot tell "the session expired" from "the ride was paused from the
 * web". The loop probes before it gives up: only [DEAD] is a true dead session that must stop retrying.
 */
enum class SessionLiveness {
    /** Our session is no longer in the active list — expired/abandoned server-side. Give up on it. */
    DEAD,

    /** Our session is active but paused (paused from the web). Not dead — mirror the pause, keep it. */
    WEB_PAUSED,

    /** Our session is active and recording (the 404s were transient). Keep uploading. */
    ACTIVE,

    /** The probe itself failed (network blip). Ambiguous — don't declare the session dead on this. */
    UNKNOWN,
}
