package com.routebase.gpsrecorder.util

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class TrackStatsTest {

    @Test fun firstFixSeedsAnchorWithZeroDistance() {
        val s = TrackStats()
        s.addFix(52.0, 13.0)
        assertEquals(0.0, s.distanceMeters, 0.0)
    }

    @Test fun accumulatesRealMovement() {
        val s = TrackStats()
        s.addFix(52.0000, 13.0000)
        s.addFix(52.0100, 13.0000)  // ~1.11 km north
        // Haversine of 0.01° latitude ≈ 1112 m.
        assertEquals(1112.0, s.distanceMeters, 5.0)
    }

    @Test fun ignoresSubMeterJitterAtStandstill() {
        val s = TrackStats()
        s.addFix(52.0, 13.0)
        repeat(50) { s.addFix(52.0 + 0.000001, 13.0 + 0.000001) }  // ~0.15 m wobble, below floor
        assertEquals(0.0, s.distanceMeters, 0.0)
    }

    @Test fun resetClearsDistanceAndAnchor() {
        val s = TrackStats()
        s.addFix(52.0, 13.0)
        s.addFix(52.01, 13.0)
        assertTrue(s.distanceMeters > 0)
        s.reset()
        assertEquals(0.0, s.distanceMeters, 0.0)
        s.addFix(48.0, 11.0)  // new anchor, no jump from the old location
        assertEquals(0.0, s.distanceMeters, 0.0)
    }
}
