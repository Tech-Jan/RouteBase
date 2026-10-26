package com.routebase.gpsrecorder.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * Pins the offset-tolerance of the server-timestamp parse. The value feeds
 * `RecordingRepository.startedBefore`, whose result gates CLOSE_STALE (SessionConflictPolicyTest) —
 * a parse that silently fails would degrade the staleness check to "never older", so the accepted
 * grammar is worth pinning explicitly: both the server's historical `Z` form and the standard
 * explicit-offset form AR-02 established must parse, on every runtime.
 */
class ServerTimestampsTest {

    private val expectedMs = 1_784_628_000_000L // 2026-07-21T10:00:00Z

    @Test
    fun `trailing-Z form parses (the recording DTO's wire format)`() {
        assertEquals(expectedMs, ServerTimestamps.parseEpochMs("2026-07-21T10:00:00Z"))
    }

    @Test
    fun `explicit +00-00 offset parses (the server's standard form since AR-02)`() {
        assertEquals(expectedMs, ServerTimestamps.parseEpochMs("2026-07-21T10:00:00+00:00"))
    }

    @Test
    fun `non-zero offset parses to the same instant, not the same wall clock`() {
        assertEquals(expectedMs, ServerTimestamps.parseEpochMs("2026-07-21T12:00:00+02:00"))
    }

    @Test
    fun `fractional seconds parse`() {
        assertEquals(expectedMs + 123, ServerTimestamps.parseEpochMs("2026-07-21T10:00:00.123Z"))
    }

    @Test
    fun `garbage and offset-less strings are null, never a throw`() {
        // Offset-less is null BY DESIGN: without an offset the string is not an instant, and guessing
        // one here would re-create the exact ambiguity AR-02 removed server-side.
        assertNull(ServerTimestamps.parseEpochMs("not a timestamp"))
        assertNull(ServerTimestamps.parseEpochMs("2026-07-21T10:00:00"))
        assertNull(ServerTimestamps.parseEpochMs(""))
    }
}
