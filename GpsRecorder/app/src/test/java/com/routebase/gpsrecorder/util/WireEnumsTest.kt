package com.routebase.gpsrecorder.util

import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * Table tests for the lenient wire-vocabulary factories (C2). The contract under pin: unknown
 * input NEVER throws — SessionStatus widens to UNKNOWN (a newer server may add states),
 * Visibility fails CLOSED to PRIVATE (the app must never invent a wire value or widen exposure).
 */
class WireEnumsTest {

    @Test
    fun `session status maps every known wire value and widens unknowns`() {
        val cases = mapOf(
            "recording" to SessionStatus.RECORDING,
            "paused" to SessionStatus.PAUSED,
            "stopped" to SessionStatus.STOPPED,
            "finalized" to SessionStatus.FINALIZED,
            "finalize_failed" to SessionStatus.FINALIZE_FAILED,
            "abandoned" to SessionStatus.ABANDONED,
            "some-future-state" to SessionStatus.UNKNOWN,
            "" to SessionStatus.UNKNOWN,
            null to SessionStatus.UNKNOWN,
        )
        for ((wire, expected) in cases) {
            assertEquals("from($wire)", expected, SessionStatus.from(wire))
        }
    }

    @Test
    fun `visibility maps the closed set case-insensitively and fails closed to PRIVATE`() {
        val cases = mapOf(
            "private" to Visibility.PRIVATE,
            "friends" to Visibility.FRIENDS,
            "public" to Visibility.PUBLIC,
            "Private" to Visibility.PRIVATE,   // spinner labels are capitalized server values (AU-09)
            "Friends" to Visibility.FRIENDS,
            "Public" to Visibility.PUBLIC,
            "null" to Visibility.PRIVATE,      // the literal "null"-on-wire hazard, closed
            "Öffentlich" to Visibility.PRIVATE, // a localized label must not leak on the wire
            "" to Visibility.PRIVATE,
            null to Visibility.PRIVATE,
        )
        for ((wire, expected) in cases) {
            assertEquals("fromOrPrivate($wire)", expected, Visibility.fromOrPrivate(wire))
        }
    }

    @Test
    fun `wire spellings match the server const classes`() {
        // The values RecorderWireParityTests scrapes — a rename here must fail locally too.
        assertEquals(
            listOf("recording", "paused", "stopped", "finalized", "finalize_failed", "abandoned", ""),
            SessionStatus.entries.map { it.wire },
        )
        assertEquals(listOf("private", "friends", "public"), Visibility.entries.map { it.wire })
    }
}
