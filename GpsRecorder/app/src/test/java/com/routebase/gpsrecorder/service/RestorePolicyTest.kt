package com.routebase.gpsrecorder.service

import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * Pins B1/AND-05: a process death during the stop-drain must not resurrect a manually-stopped ride as an
 * active recording. The persisted stop flag decides the restore direction before any intent is handled.
 */
class RestorePolicyTest {

    @Test fun `active recording without a stop flag restores as before`() {
        // The pre-B1 behaviour, unchanged: reconnect / resume / paused-restore all proceed normally.
        assertEquals(RestoreAction.PROCEED, RestorePolicy.decide(isCapturing = true, stopRequested = false))
    }

    @Test fun `active recording with a pending stop resumes the STOP path`() {
        // THE FIX. The user ended this ride; the process died while the stop was draining. The only legal
        // direction is finishing that stop — drain what is left and finalize — never active recording.
        assertEquals(RestoreAction.RESUME_STOP, RestorePolicy.decide(isCapturing = true, stopRequested = true))
    }

    @Test fun `already-stopped state is untouched`() {
        // Nothing capturing → nothing to restore, with or without an orphaned stop flag (a death inside a
        // stop that had no recording to act on). RESUME_STOP here would spin up a stop for recording 0.
        assertEquals(RestoreAction.PROCEED, RestorePolicy.decide(isCapturing = false, stopRequested = false))
        assertEquals(RestoreAction.PROCEED, RestorePolicy.decide(isCapturing = false, stopRequested = true))
    }
}
