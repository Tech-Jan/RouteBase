package com.routebase.gpsrecorder.util

import org.junit.Assert.assertEquals
import org.junit.Test

class StatsFormatTest {

    @Test fun distanceSwitchesUnitAtOneKm() {
        assertEquals("0 m", formatDistance(0.0))
        assertEquals("850 m", formatDistance(850.0))
        assertEquals("1.00 km", formatDistance(1000.0))
        assertEquals("3.14 km", formatDistance(3140.0))
    }

    @Test fun paceIsMinutesPerKm() {
        // 1 km in 5 min → 5:00 /km.
        assertEquals("5:00 /km", formatPace(1000.0, 5 * 60_000L))
        assertEquals("4:30 /km", formatPace(2000.0, 9 * 60_000L))
    }

    @Test fun speedIsKmPerHour() {
        // 10 km in 30 min → 20 km/h.
        assertEquals("20.0 km/h", formatSpeed(10_000.0, 30 * 60_000L))
    }

    @Test fun blankUntilEnoughDistance() {
        assertEquals("", formatPace(10.0, 60_000L))
        assertEquals("", formatSpeed(10.0, 60_000L))
    }

    @Test fun joinStatusDropsBlankParts() {
        assertEquals("3.14 km  ·  1:00 elapsed", joinStatus("3.14 km", "", "1:00 elapsed"))
    }

    @Test fun footActivityClassification() {
        assertEquals(true, isFootActivity("Run"))
        assertEquals(true, isFootActivity("Hike"))
        assertEquals(false, isFootActivity("Ride"))
        assertEquals(false, isFootActivity(null))
    }
}
