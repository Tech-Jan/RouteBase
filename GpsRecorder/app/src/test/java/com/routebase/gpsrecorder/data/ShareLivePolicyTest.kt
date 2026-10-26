package com.routebase.gpsrecorder.data

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * AND-24: the mint-or-not decision, in isolation — no prefs, no network. The upload loop calls
 * ensureShareLinkAsync every tick, so this rule is what keeps it from minting the wrong time or twice.
 */
class ShareLivePolicyTest {

    @Test fun mintsWhenAskedSessionExistsAndNotYetMinted() {
        assertTrue(ShareLivePolicy.shouldMint(shareLive = true, hasSession = true, alreadyMinted = false))
    }

    @Test fun doesNotMintWhenTheUserDidNotAsk() {
        assertFalse(ShareLivePolicy.shouldMint(shareLive = false, hasSession = true, alreadyMinted = false))
    }

    @Test fun doesNotMintBeforeAServerSessionExists() {
        // An offline start captures for a while with no session — there is nothing to mint against yet.
        assertFalse(ShareLivePolicy.shouldMint(shareLive = true, hasSession = false, alreadyMinted = false))
    }

    @Test fun doesNotReMintOnceMinted() {
        // Per-tick idempotency: once the link exists we must not spray duplicate links every upload tick.
        assertFalse(ShareLivePolicy.shouldMint(shareLive = true, hasSession = true, alreadyMinted = true))
    }
}
