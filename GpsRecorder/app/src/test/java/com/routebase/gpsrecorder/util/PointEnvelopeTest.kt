package com.routebase.gpsrecorder.util

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * First JVM unit tests in the app (AQ-05). Pins [PointEnvelope] to the server's RS-03 bounds so a
 * poison GPS fix can't wedge the upload queue. No Robolectric — [PointEnvelope] is pure.
 */
class PointEnvelopeTest {

    private val validTime = 1_600_000_000_000L // 2020-09-13

    @Test fun validFixAccepted() {
        assertTrue(PointEnvelope.isValidFix(47.0, 11.0, validTime))
    }

    @Test fun nanLatRejected() {
        assertFalse(PointEnvelope.isValidFix(Double.NaN, 11.0, validTime))
    }

    @Test fun infiniteLngRejected() {
        assertFalse(PointEnvelope.isValidFix(47.0, Double.POSITIVE_INFINITY, validTime))
    }

    @Test fun outOfRangeLatRejected() {
        assertFalse(PointEnvelope.isValidFix(90.1, 11.0, validTime))
        assertFalse(PointEnvelope.isValidFix(-90.1, 11.0, validTime))
    }

    @Test fun outOfRangeLngRejected() {
        assertFalse(PointEnvelope.isValidFix(47.0, 180.1, validTime))
    }

    @Test fun boundaryCoordsAccepted() {
        assertTrue(PointEnvelope.isValidFix(90.0, 180.0, validTime))
        assertTrue(PointEnvelope.isValidFix(-90.0, -180.0, validTime))
    }

    @Test fun zeroTimeRejected() {
        // The real fused-provider glitch: loc.time == 0 before first GPS time sync.
        assertFalse(PointEnvelope.isValidFix(47.0, 11.0, 0L))
    }

    @Test fun preEpochBoundRejected() {
        assertFalse(PointEnvelope.isValidFix(47.0, 11.0, PointEnvelope.MIN_TIME_MS - 1))
    }

    @Test fun minTimeBoundAccepted() {
        assertTrue(PointEnvelope.isValidFix(47.0, 11.0, PointEnvelope.MIN_TIME_MS))
    }

    @Test fun farFutureTimestampAccepted() {
        // No client future bound by design: a GNSS fix stamped with correct satellite UTC must survive a
        // phone clock running behind real time (fresh boot before NTP). The server's now+10 min bound,
        // checked against its correct clock, is the guard. See PointEnvelope docs.
        assertTrue(PointEnvelope.isValidFix(47.0, 11.0, validTime + 100L * 365 * 24 * 60 * 60 * 1000))
    }

    @Test fun altitudeInRangeKept() {
        assertEquals(1000.0, PointEnvelope.sanitizeAltitude(1000.0)!!, 0.0)
        assertEquals(-500.0, PointEnvelope.sanitizeAltitude(-500.0)!!, 0.0)
        assertEquals(10_000.0, PointEnvelope.sanitizeAltitude(10_000.0)!!, 0.0)
    }

    @Test fun altitudeOutOfRangeNulled() {
        assertNull(PointEnvelope.sanitizeAltitude(-501.0))
        assertNull(PointEnvelope.sanitizeAltitude(10_001.0))
        assertNull(PointEnvelope.sanitizeAltitude(Double.NaN))
        assertNull(PointEnvelope.sanitizeAltitude(null))
    }

    @Test fun speedInRangeKept() {
        assertEquals(5.0, PointEnvelope.sanitizeSpeed(5.0)!!, 0.0)
        assertEquals(150.0, PointEnvelope.sanitizeSpeed(150.0)!!, 0.0)
        assertEquals(0.0, PointEnvelope.sanitizeSpeed(0.0)!!, 0.0)
    }

    @Test fun speedOutOfRangeNulled() {
        assertNull(PointEnvelope.sanitizeSpeed(151.0))
        assertNull(PointEnvelope.sanitizeSpeed(-0.1))
        assertNull(PointEnvelope.sanitizeSpeed(Double.NaN))
        assertNull(PointEnvelope.sanitizeSpeed(null))
    }

    @Test fun pressureInRangeKept() {
        assertEquals(1013.25, PointEnvelope.sanitizePressure(1013.25)!!, 0.0)
        assertEquals(300.0, PointEnvelope.sanitizePressure(300.0)!!, 0.0)
        assertEquals(1100.0, PointEnvelope.sanitizePressure(1100.0)!!, 0.0)
    }

    @Test fun pressureOutOfRangeNulled() {
        // The gap this closes: the server 400-rejects the WHOLE batch on one bad pressure, and the client's
        // 400 handler then deletes all 100 of its GPS points. Pressure was the only server-bounded field the
        // client did not mirror, so a glitchy barometer could silently cost the ride its track.
        assertNull(PointEnvelope.sanitizePressure(0.0))          // a driver reporting nothing
        assertNull(PointEnvelope.sanitizePressure(299.9))
        assertNull(PointEnvelope.sanitizePressure(1100.1))
        assertNull(PointEnvelope.sanitizePressure(Double.NaN))
        assertNull(PointEnvelope.sanitizePressure(Double.POSITIVE_INFINITY))
        assertNull(PointEnvelope.sanitizePressure(null))         // no barometer — stays null, as before
    }
}
