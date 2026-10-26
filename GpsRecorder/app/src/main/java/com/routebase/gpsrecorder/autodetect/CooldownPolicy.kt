package com.routebase.gpsrecorder.autodetect

/**
 * Decides whether an elapsed cooldown should re-arm instead of stopping the recording.
 *
 * Activity Recognition is accelerometer-only and misreads slow technical riding (braking, feet down,
 * no cadence) as STILL/WALKING — on the 2026-07-11 ride it cut sessions at 1.9–9.9 km/h five times in
 * one continuous hour. GPS displacement is the authoritative "still moving" signal: if the movement
 * anchor advanced recently, the workout is not over, whatever AR says.
 *
 * Exception: IN_VEHICLE with sustained car-speed must still stop — riding home with the bike in the
 * car shows displacement forever. Slow technical riding never sustains >[VEHICLE_SPEED_STOP_KMH].
 * The locker/indoors case keeps working: no fixes → no displacement → no re-arm.
 *
 * Pure logic — unit-tested without Android deps.
 */
object CooldownPolicy {

    /** Displacement younger than this at cooldown elapse means "still moving" → re-arm. */
    const val REARM_WINDOW_MS = 3 * 60_000L

    /** IN_VEHICLE above this sustained speed is a real vehicle, not a fast descent → never re-arm. */
    const val VEHICLE_SPEED_STOP_KMH = 25.0

    fun shouldRearm(cooldownSignal: String?, msSinceLastMoved: Long, recentSpeedKmh: Double): Boolean {
        if (msSinceLastMoved < 0 || msSinceLastMoved >= REARM_WINDOW_MS) return false
        if (cooldownSignal == SIGNAL_IN_VEHICLE && recentSpeedKmh > VEHICLE_SPEED_STOP_KMH) return false
        return true
    }

    // Persisted names for prefs.cooldownSignal (grep-able, independent of DetectedActivity ints).
    const val SIGNAL_STILL = "STILL"
    const val SIGNAL_IN_VEHICLE = "IN_VEHICLE"
}
