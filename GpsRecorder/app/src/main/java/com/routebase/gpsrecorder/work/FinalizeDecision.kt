package com.routebase.gpsrecorder.work

/**
 * The branch selection for one [FinalizeRecordingWorker] tick, extracted verbatim from
 * [FinalizeRecordingWorker.doWork] so it is unit-testable without WorkManager or a running service — like
 * [UploadDecision] and [com.routebase.gpsrecorder.service.WatchdogPolicy].
 *
 * PURE over booleans. The grace mutations stay in the worker on purpose (AND-25): [PrefsManager.claimGraceStop]
 * is an atomic CLAIM, not a query, and [PrefsManager.endGrace] a write — a "pure" function that called
 * either under the hood would be untestable in exactly the way this extraction exists to fix. The worker
 * evaluates the claim in the original order and passes its result in as [FinalizeState.graceClaimed]; the
 * `startService` call, its try/catch fall-through, and the `EventLog` line all stay in the worker too.
 */
enum class FinalizeAction {
    /** Already stopped — nothing to finalize. */
    NOTHING_TO_DO,

    /** A grace finalize that LOST the atomic claim: a resume revived the window first. Abort, change
     * nothing (AND-25 — a plain gracePause read here would let this stop a now-live ride). */
    ABORT_RESUME_WON,

    /** The service is alive (paused, still foreground) — let it run the normal stop (drain/stop/park). The
     * worker falls through to [PARK_LOCALLY] if startService throws (it died between the check and the
     * call — that fall-through IS the 2h12m hole). */
    DELEGATE_TO_SERVICE,

    /** No service to run the stop — hand the recording to the upload queue ourselves so the phone can start
     * the next recording immediately. */
    PARK_LOCALLY,
}

/**
 * The snapshot one finalize tick decides over. Plain booleans — no Android, no WorkManager.
 *
 * [graceClaimed] is the ALREADY-EVALUATED result of the worker's grace handling: for a grace reason it is
 * [PrefsManager.claimGraceStop]'s return (true = won the claim, may stop); for a non-grace reason the worker
 * called [PrefsManager.endGrace] and passes true (there is no claim to lose). Only read when
 * [reasonIsGrace] is true.
 */
data class FinalizeState(
    val isCapturing: Boolean,
    val reasonIsGrace: Boolean,
    val graceClaimed: Boolean,
    val isServiceActive: Boolean,
)

object FinalizeDecision {

    /**
     * Same order and short-circuits as [FinalizeRecordingWorker.doWork] before the extraction: not
     * capturing, then the grace claim, then service-liveness.
     */
    fun decide(state: FinalizeState): FinalizeAction {
        if (!state.isCapturing) return FinalizeAction.NOTHING_TO_DO   // already stopped — nothing to finalize

        // AND-25: a grace finalize proceeds only if it WON the atomic claim against the resume path. A lost
        // claim means a resume/stop already ended the window → abort. (A non-grace reason called endGrace()
        // in the worker and is never gated here.)
        if (state.reasonIsGrace && !state.graceClaimed) return FinalizeAction.ABORT_RESUME_WON

        // Service alive → let it run the normal stop. Starting a service is allowed while it is foreground.
        if (state.isServiceActive) return FinalizeAction.DELEGATE_TO_SERVICE

        // No service: park the recording locally and let UploadWorker deliver it when there is a network.
        return FinalizeAction.PARK_LOCALLY
    }
}
