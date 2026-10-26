package com.routebase.gpsrecorder.util

import kotlin.math.atan2
import kotlin.math.cos
import kotlin.math.sin
import kotlin.math.sqrt

/**
 * Running distance estimate for the live notification. Pure (haversine, no Android deps) so it is
 * unit-testable without Robolectric. NOT the source of truth — the server recomputes the authoritative
 * track distance; this only needs to be close enough to show a plausible number while recording.
 *
 * A per-segment floor ([MIN_SEGMENT_M]) absorbs standstill GPS jitter: the anchor only advances once a
 * fix lands at least that far away, so a stationary phone doesn't inflate the total. Fixes are already
 * accuracy-filtered (≤40 m) upstream, so the floor can be small.
 */
class TrackStats {

    // @Volatile: written by the main-looper location callback, read from the IO watchdog/upload/stop
    // coroutines (notification text + EventLog lines) and reset from the start coroutine — the same
    // memory-visibility discipline as the service's own @Volatile statics. Display-only values, so this
    // is about staleness/tearing in a notification string, never about uploaded data.
    @Volatile private var lastLat = Double.NaN
    @Volatile private var lastLng = Double.NaN

    @Volatile var distanceMeters = 0.0
        private set

    /** Feeds one accepted fix. Accumulates the segment from the anchor when it clears the jitter floor. */
    fun addFix(lat: Double, lng: Double) {
        if (lastLat.isNaN()) {
            lastLat = lat; lastLng = lng
            return
        }
        val seg = haversineMeters(lastLat, lastLng, lat, lng)
        if (seg >= MIN_SEGMENT_M) {
            distanceMeters += seg
            lastLat = lat; lastLng = lng
        }
    }

    /** Clears distance + anchor for a new session (or a reconnect that re-seeds). */
    fun reset() {
        lastLat = Double.NaN
        lastLng = Double.NaN
        distanceMeters = 0.0
    }

    companion object {
        private const val MIN_SEGMENT_M = 2.0
        private const val EARTH_RADIUS_M = 6_371_000.0

        fun haversineMeters(lat1: Double, lng1: Double, lat2: Double, lng2: Double): Double {
            val dLat = Math.toRadians(lat2 - lat1)
            val dLng = Math.toRadians(lng2 - lng1)
            val a = sin(dLat / 2) * sin(dLat / 2) +
                cos(Math.toRadians(lat1)) * cos(Math.toRadians(lat2)) *
                sin(dLng / 2) * sin(dLng / 2)
            return EARTH_RADIUS_M * 2 * atan2(sqrt(a), sqrt(1 - a))
        }
    }
}
