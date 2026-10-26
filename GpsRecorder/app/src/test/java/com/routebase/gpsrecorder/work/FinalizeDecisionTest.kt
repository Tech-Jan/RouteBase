package com.routebase.gpsrecorder.work

import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * Pins the finalize branch selection extracted from FinalizeRecordingWorker.doWork. The rule that carries
 * incident history is AND-25: a grace finalize that raced a just-landed resume must NOT stop the now-live
 * ride. The worker performs the atomic claim ([PrefsManager.claimGraceStop]) and passes the result in as
 * [FinalizeState.graceClaimed]; this pins that a LOST claim aborts and a WON claim proceeds.
 */
class FinalizeDecisionTest {

    private fun state(
        isCapturing: Boolean = true,
        reasonIsGrace: Boolean = false,
        graceClaimed: Boolean = true,
        isServiceActive: Boolean = false,
    ) = FinalizeState(isCapturing, reasonIsGrace, graceClaimed, isServiceActive)

    private fun decide(state: FinalizeState) = FinalizeDecision.decide(state)

    // ── Nothing to finalize ─────────────────────────────────────────────────

    @Test fun `not capturing does nothing, whatever else is set`() {
        assertEquals(FinalizeAction.NOTHING_TO_DO, decide(state(isCapturing = false)))
        // A grace reason with a lost claim and a live service still resolves to NOTHING_TO_DO first — this
        // is the pre-extraction early return, before the grace window is ever touched.
        assertEquals(
            FinalizeAction.NOTHING_TO_DO,
            decide(state(isCapturing = false, reasonIsGrace = true, graceClaimed = false, isServiceActive = true)),
        )
    }

    // ── AND-25: the atomic grace claim decides resume-vs-finalize ────────────

    @Test fun `AND-25 a lost grace claim aborts and changes nothing`() {
        // claimGraceStop() returned false → a resume/stop already won the window. Abort regardless of the
        // service state — the abort out-ranks both DELEGATE and PARK.
        assertEquals(FinalizeAction.ABORT_RESUME_WON, decide(state(reasonIsGrace = true, graceClaimed = false, isServiceActive = true)))
        assertEquals(FinalizeAction.ABORT_RESUME_WON, decide(state(reasonIsGrace = true, graceClaimed = false, isServiceActive = false)))
    }

    @Test fun `AND-25 a won grace claim proceeds to the stop`() {
        // claimGraceStop() returned true → this finalize owns the stop. Service alive → delegate; gone → park.
        assertEquals(FinalizeAction.DELEGATE_TO_SERVICE, decide(state(reasonIsGrace = true, graceClaimed = true, isServiceActive = true)))
        assertEquals(FinalizeAction.PARK_LOCALLY, decide(state(reasonIsGrace = true, graceClaimed = true, isServiceActive = false)))
    }

    // ── A non-grace reason (service gone / boot refused) never aborts ────────

    @Test fun `a non-grace reason never aborts, even with graceClaimed false`() {
        // The worker called endGrace() (not the claim) for a non-grace reason, so the abort gate — keyed on
        // reasonIsGrace — never fires here. A stale graceClaimed=false must not divert it.
        assertEquals(FinalizeAction.DELEGATE_TO_SERVICE, decide(state(reasonIsGrace = false, graceClaimed = false, isServiceActive = true)))
        assertEquals(FinalizeAction.PARK_LOCALLY, decide(state(reasonIsGrace = false, graceClaimed = false, isServiceActive = false)))
    }

    @Test fun `graceClaimed is consulted only for a grace reason`() {
        // For a non-grace reason the action is identical whether graceClaimed is true or false.
        for (claimed in listOf(true, false)) {
            assertEquals(FinalizeAction.DELEGATE_TO_SERVICE, decide(state(reasonIsGrace = false, graceClaimed = claimed, isServiceActive = true)))
            assertEquals(FinalizeAction.PARK_LOCALLY, decide(state(reasonIsGrace = false, graceClaimed = claimed, isServiceActive = false)))
        }
    }

    // ── The DELEGATE→PARK fall-through on a startService throw is worker code ─
    // decide only chooses DELEGATE_TO_SERVICE; the try/catch that falls through to the park path when the
    // service died between the check and the call stays in the worker (a runtime I/O failure, not a pure
    // input) and is verified by diff-read, not here.
}
