package com.routebase.gpsrecorder.autodetect

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class CooldownPolicyTest {

    @Test fun recentDisplacementRearmsStillCooldown() {
        // The 2026-07-11 failure: STILL fired during a slow technical descent (~3 km/h), GPS kept moving.
        assertTrue(CooldownPolicy.shouldRearm(CooldownPolicy.SIGNAL_STILL, 60_000L, 3.0))
    }

    @Test fun staleDisplacementStops() {
        // Genuine rest: anchor stopped advancing longer than the re-arm window ago.
        assertFalse(CooldownPolicy.shouldRearm(CooldownPolicy.SIGNAL_STILL, CooldownPolicy.REARM_WINDOW_MS, 0.0))
        assertFalse(CooldownPolicy.shouldRearm(CooldownPolicy.SIGNAL_STILL, 30 * 60_000L, 0.0))
    }

    @Test fun noFixesEverStops() {
        // lastMovedMs == 0 → msSince is huge (locker/indoors) → never re-arm.
        assertFalse(CooldownPolicy.shouldRearm(CooldownPolicy.SIGNAL_STILL, System.currentTimeMillis(), 0.0))
    }

    @Test fun vehicleAtCarSpeedStops() {
        // Bike in the car: displacement is recent forever — the speed exception must end the session.
        assertFalse(CooldownPolicy.shouldRearm(CooldownPolicy.SIGNAL_IN_VEHICLE, 10_000L, 60.0))
    }

    @Test fun vehicleAtRidingSpeedRearms() {
        // Fast smooth descent misread as IN_VEHICLE, but 18 km/h is riding, not driving.
        assertTrue(CooldownPolicy.shouldRearm(CooldownPolicy.SIGNAL_IN_VEHICLE, 10_000L, 18.0))
    }

    @Test fun stillIgnoresSpeedException() {
        // The car-speed exception is IN_VEHICLE-only; STILL with recent displacement always re-arms.
        assertTrue(CooldownPolicy.shouldRearm(CooldownPolicy.SIGNAL_STILL, 10_000L, 60.0))
    }

    @Test fun negativeClockSkewStops() {
        assertFalse(CooldownPolicy.shouldRearm(CooldownPolicy.SIGNAL_STILL, -5_000L, 10.0))
    }

    @Test fun rearmWindowBoundaryIsExclusive() {
        assertTrue(CooldownPolicy.shouldRearm(CooldownPolicy.SIGNAL_STILL, CooldownPolicy.REARM_WINDOW_MS - 1, 0.0))
    }
}
