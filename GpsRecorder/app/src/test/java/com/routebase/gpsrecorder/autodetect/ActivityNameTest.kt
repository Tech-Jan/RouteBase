package com.routebase.gpsrecorder.autodetect

import com.google.android.gms.location.DetectedActivity
import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * Pins [AutoDetectController.activityName] — the label written to the diagnostic EventLog for every
 * received AR transition. The whole point of the 1.6.3 diagnostic build is that a commute log reads
 * `AR ENTER IN_VEHICLE` vs `AR ENTER ON_BICYCLE`, so the mapping must be exact and stable.
 */
class ActivityNameTest {

    @Test fun `the workout-relevant activities map to their names`() {
        assertEquals("ON_BICYCLE", AutoDetectController.activityName(DetectedActivity.ON_BICYCLE))
        assertEquals("RUNNING", AutoDetectController.activityName(DetectedActivity.RUNNING))
        assertEquals("WALKING", AutoDetectController.activityName(DetectedActivity.WALKING))
        assertEquals("STILL", AutoDetectController.activityName(DetectedActivity.STILL))
        assertEquals("IN_VEHICLE", AutoDetectController.activityName(DetectedActivity.IN_VEHICLE))
    }

    @Test fun `an unrecognized constant still logs its numeric code, never crashes`() {
        // A future Play Services constant must leave a readable fingerprint, not an empty label.
        assertEquals("activity#999", AutoDetectController.activityName(999))
    }
}
