package com.routebase.gpsrecorder.util

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * These params are what a queued offline recording's session is created FROM, sometimes hours after the
 * ride. A silently mis-parsed field would publish the activity with the wrong type/visibility, or a wrong
 * timezone offset across the whole track — so every malformed form must decode to null (unrestorable)
 * rather than to plausible-looking garbage.
 */
class RecordingParamsCodecTest {

    private val params = RecordingParams("Ride", "private", "1.5.0", 120, true)

    @Test fun roundTrips() {
        val encoded = RecordingParamsCodec.encode(params)
        assertEquals(params, RecordingParamsCodec.decode(encoded))
    }

    @Test fun roundTripsNegativeOffsetAndManualStart() {
        val p = RecordingParams("Run", "public", "1.5.0", -480, false)
        assertEquals(p, RecordingParamsCodec.decode(RecordingParamsCodec.encode(p)))
    }

    @Test fun roundTripsWithShareLive() {
        val p = RecordingParams("Ride", "private", "1.5.0", 120, autoDetected = false, shareLive = true)
        assertEquals(p, RecordingParamsCodec.decode(RecordingParamsCodec.encode(p)))
    }

    @Test fun decodesLegacyFiveFieldAsNotShared() {
        // A row encoded before shareLive existed (5 fields) must still restore — as shareLive=false — so an
        // app upgrade mid-recording, or an already-queued offline ride, is not dropped as unrestorable.
        val decoded = RecordingParamsCodec.decode("Ride|private|1.5.0|120|true")
        assertEquals(RecordingParams("Ride", "private", "1.5.0", 120, true, shareLive = false), decoded)
    }

    @Test fun decodeRejectsNonBooleanShareFlag() {
        assertNull(RecordingParamsCodec.decode("Ride|private|1.5.0|120|true|maybe"))
    }

    @Test fun rejectsSeparatorInAField() {
        // Would shift every later field: the tz offset would parse from the version string.
        assertNull(RecordingParamsCodec.encode(params.copy(activityType = "Ri|de")))
    }

    @Test fun decodeRejectsWrongFieldCount() {
        // Valid counts are 5 (legacy) or 6 (current); anything else is unrestorable.
        assertNull(RecordingParamsCodec.decode("Ride|private|1.5.0|120"))               // 4: too few
        assertNull(RecordingParamsCodec.decode("Ride|private|1.5.0|120|true|false|x"))  // 7: too many
    }

    @Test fun decodeRejectsNonNumericOffset() {
        assertNull(RecordingParamsCodec.decode("Ride|private|1.5.0|noon|true"))
    }

    @Test fun decodeRejectsNonBooleanAutoFlag() {
        // "yes".toBoolean() would silently be false — an auto ride would skip the web review queue.
        assertNull(RecordingParamsCodec.decode("Ride|private|1.5.0|120|yes"))
    }

    @Test fun decodeRejectsEmptyTypeOrVisibility() {
        assertNull(RecordingParamsCodec.decode("|private|1.5.0|120|true"))
        assertNull(RecordingParamsCodec.decode("Ride||1.5.0|120|true"))
    }

    @Test fun decodeRejectsNullAndEmpty() {
        assertNull(RecordingParamsCodec.decode(null))
        assertNull(RecordingParamsCodec.decode(""))
    }
}
