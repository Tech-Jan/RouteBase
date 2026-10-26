package com.routebase.gpsrecorder.data

import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * Pins B2/AND-07's verdict rule. The dangerous edges: a rejected key must never be OK (that is the bug —
 * persisting a typo'd key), and a rejected key must never be UNREACHABLE either (the offline escape hatch
 * would then offer to save a key the server just refused).
 */
class CredentialVerifierTest {

    @Test fun `a JSON-list 200 proves the credentials`() {
        assertEquals(CredentialVerdict.OK, CredentialVerifier.verdictFor(200, "[]"))
        assertEquals(CredentialVerdict.OK, CredentialVerifier.verdictFor(200, """[{"sessionId"""))
        // Whitespace-led JSON is still JSON.
        assertEquals(CredentialVerdict.OK, CredentialVerifier.verdictFor(200, " ["))
    }

    @Test fun `a BOM or extra whitespace before the JSON list still proves the credentials (AND-43)`() {
        val bom = Char(0xFEFF)
        assertEquals(CredentialVerdict.OK, CredentialVerifier.verdictFor(200, "$bom[]"))
        assertEquals(CredentialVerdict.OK, CredentialVerifier.verdictFor(200, "$bom  [{}"))
        // Still not fooled by a non-JSON body that merely leads with a BOM.
        assertEquals(CredentialVerdict.UNREACHABLE, CredentialVerifier.verdictFor(200, "$bom<html>"))
    }

    @Test fun `an auth rejection is BAD_KEY, never the offline hatch`() {
        assertEquals(CredentialVerdict.BAD_KEY, CredentialVerifier.verdictFor(401, ""))
        assertEquals(CredentialVerdict.BAD_KEY, CredentialVerifier.verdictFor(403, ""))
        // Even when some layer decorates the rejection with a body.
        assertEquals(CredentialVerdict.BAD_KEY, CredentialVerifier.verdictFor(401, "[\"nope\"]"))
    }

    @Test fun `a 200 that is not the endpoint's contract does not validate the key`() {
        // An https intermediary (status page, captive proxy) answering 200 never judged the key.
        assertEquals(CredentialVerdict.UNREACHABLE, CredentialVerifier.verdictFor(200, "<!DOCTYP"))
        assertEquals(CredentialVerdict.UNREACHABLE, CredentialVerifier.verdictFor(200, ""))
    }

    @Test fun `anything else is UNREACHABLE — wrong URL, server trouble, redirects`() {
        assertEquals(CredentialVerdict.UNREACHABLE, CredentialVerifier.verdictFor(404, "Not Found"))
        assertEquals(CredentialVerdict.UNREACHABLE, CredentialVerifier.verdictFor(500, ""))
        // followRedirects(false): a redirect surfaces as its own code and must not read as verified.
        assertEquals(CredentialVerdict.UNREACHABLE, CredentialVerifier.verdictFor(302, ""))
    }
}
