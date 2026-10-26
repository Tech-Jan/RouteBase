package com.routebase.gpsrecorder.util

import java.util.Locale

/**
 * Pure formatters for the live-recording notification. No Android deps → unit-testable. Distance/pace
 * shown here are the on-device estimate (see [TrackStats]); the server owns the authoritative numbers.
 */

/** "850 m" under a km, "3.14 km" above. */
fun formatDistance(meters: Double): String =
    if (meters < 1000) "${meters.toInt()} m"
    else String.format(Locale.US, "%.2f km", meters / 1000)

/** Foot pace "5:30 /km"; blank until there's enough distance to be meaningful. */
fun formatPace(meters: Double, elapsedMs: Long): String {
    if (meters < MIN_PACE_DISTANCE_M || elapsedMs <= 0) return ""
    val secPerKm = (elapsedMs / 1000.0) / (meters / 1000.0)
    val min = (secPerKm / 60).toInt()
    val sec = (secPerKm % 60).toInt()
    return String.format(Locale.US, "%d:%02d /km", min, sec)
}

/** Ride speed "24.5 km/h"; blank until there's enough distance to be meaningful. */
fun formatSpeed(meters: Double, elapsedMs: Long): String {
    if (meters < MIN_PACE_DISTANCE_M || elapsedMs <= 0) return ""
    val kmh = (meters / 1000.0) / (elapsedMs / 3_600_000.0)
    return String.format(Locale.US, "%.1f km/h", kmh)
}

/** " · " between the parts, dropping any that are blank. */
fun joinStatus(vararg parts: String): String =
    parts.filter { it.isNotBlank() }.joinToString("  ·  ")

/** True for activity types where pace (min/km) reads better than speed. String? seam kept —
 *  callers hold raw prefs/DTO values; the classification itself lives in [ActivityType.isFoot]. */
fun isFootActivity(activityType: String?): Boolean =
    activityType != null && ActivityType(activityType).isFoot

private const val MIN_PACE_DISTANCE_M = 50.0
