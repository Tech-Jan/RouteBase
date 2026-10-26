package com.routebase.gpsrecorder.data

/**
 * AND-24: the single rule for whether the phone should mint a public live-share for the current recording.
 * Pure so it is unit-testable without a network or prefs (mirrors [SessionConflictPolicy]).
 */
object ShareLivePolicy {
    /**
     * Mint exactly when the user asked to share this ride, a server session exists to mint against, and one
     * has not already been minted for it. The "already minted" guard is what makes the upload loop's
     * per-tick call idempotent — no duplicate links once the first mint lands.
     */
    fun shouldMint(shareLive: Boolean, hasSession: Boolean, alreadyMinted: Boolean): Boolean =
        shareLive && hasSession && !alreadyMinted
}
