package com.routebase.gpsrecorder.data

import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * Pins the 409 resolution rule. The dangerous branch is CLOSE_STALE: it calls stopSession on a session this
 * phone did not create, and the server FINALIZES what it stops. Getting the guard wrong there ends someone's
 * ride mid-ride.
 */
class SessionConflictPolicyTest {

    private fun decide(queued: Boolean = false, older: Boolean = false, auto: Boolean = false) =
        SessionConflictPolicy.decide(queuedOwnsIt = queued, startedBeforeUs = older, autoDetected = auto)

    @Test fun `a queued recording's session is delivered, never adopted`() {
        // Adopting it would append the live ride to a FINISHED ride's activity — the contamination the
        // upload queue exists to prevent. True regardless of age or how the recording started.
        assertEquals(ConflictAction.DELIVER_QUEUED, decide(queued = true))
        assertEquals(ConflictAction.DELIVER_QUEUED, decide(queued = true, older = true))
        assertEquals(ConflictAction.DELIVER_QUEUED, decide(queued = true, older = true, auto = true))
    }

    @Test fun `a session no older than us is our own in-flight one — adopt it`() {
        // The start request whose response never reached us.
        assertEquals(ConflictAction.ADOPT, decide(older = false))
        assertEquals(ConflictAction.ADOPT, decide(older = false, auto = true))
    }

    @Test fun `a MANUAL start closes an older unclaimed session`() {
        // The user just tapped Record and is standing there: a leftover ghost blocking them is exactly what
        // they want gone.
        assertEquals(ConflictAction.CLOSE_STALE, decide(older = true, auto = false))
    }

    @Test fun `an AUTO start NEVER closes an older unclaimed session`() {
        // THE FIX. An older unclaimed session is equally a ghost and the user's own live ride on another
        // device (the web /Record page, a second phone) — SessionResponse carries nothing to tell them
        // apart. Closing it also finalizes it, so an unattended auto-start would silently end a ride in
        // progress. It backs off instead: capture continues locally and uploads as its own activity.
        assertEquals(ConflictAction.BACK_OFF, decide(older = true, auto = true))
    }

    @Test fun `backing off is the ONLY difference auto makes`() {
        // Auto must not change the other three branches — it is a tiebreak for the ambiguous case alone.
        for (queued in listOf(true, false)) for (older in listOf(true, false)) {
            val manual = decide(queued = queued, older = older, auto = false)
            val auto = decide(queued = queued, older = older, auto = true)
            val ambiguous = !queued && older
            if (ambiguous) assertEquals(ConflictAction.BACK_OFF, auto) else assertEquals(manual, auto)
        }
    }
}
