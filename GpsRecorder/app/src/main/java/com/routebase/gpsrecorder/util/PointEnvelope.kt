package com.routebase.gpsrecorder.util

/**
 * Client-side mirror of the server's recording-point validator (RS-03,
 * `RecordingRequestValidator.Validate(RecordingPointsBatch)`). The server 400-rejects a whole batch
 * if ANY point is out of envelope; without this filter one poison fix (a fused-provider `time == 0`
 * glitch before first GPS sync, a speed spike, a wild altitude) would wedge the upload queue forever
 * (AQ-05). Filtering at capture keeps poison out of the queue; a bad coordinate/time drops the whole
 * fix, a bad altitude/speed/pressure is nulled so a glitchy sensor still yields usable track geometry.
 *
 * EVERY field the server bounds must be mirrored here, or the drop is not a nulled field but the batch's
 * whole GPS: an unmirrored bad value 400s the request, and the 400 handler deletes all 100 points in it.
 * Pressure was that hole — a sensor reading outside the server's band is nulled here now, and it is the
 * one field whose bad value PERSISTS (BarometerReader's EMA carries it into every later batch too), so it
 * is filtered at the sensor as well.
 *
 * Bounds match the server's coordinate, lower-time, altitude, speed and pressure checks. The future-time
 * bound is deliberately NOT enforced here: a GNSS fix carries satellite UTC, so on a fresh boot before NTP
 * settles — the phone clock running behind real time, exactly when auto-detect can start a recording —
 * every fix would read as "far future" against System.currentTimeMillis() and be dropped, yielding a silent
 * zero-point recording. The server's now+10 min bound (checked against its correct clock) is the real guard,
 * and a batch the server 400-rejects is dropped without wedging the queue (PointDelivery.uploadBatchLocked).
 * Pure — no Android deps — so it is JVM-unit-testable without Robolectric.
 */
object PointEnvelope {

    const val MIN_ALTITUDE = -500.0
    const val MAX_ALTITUDE = 10_000.0
    const val MIN_SPEED = 0.0
    const val MAX_SPEED = 150.0
    // Sea-level extremes on Earth span ~870–1085 hPa; the server's generous 300–1100 band is what a
    // reading must clear to be storable. Anything outside it is a bad sensor value, not a real altitude.
    const val MIN_PRESSURE = 300.0
    const val MAX_PRESSURE = 1100.0

    /** 2015-01-01T00:00:00Z in epoch millis — the server's lower timestamp bound. */
    const val MIN_TIME_MS = 1_420_070_400_000L

    /** True when the coordinate + time are storable. A false here means DROP the whole fix. */
    fun isValidFix(lat: Double, lng: Double, timeMs: Long): Boolean =
        lat.isFinite() && lat in -90.0..90.0 &&
            lng.isFinite() && lng in -180.0..180.0 &&
            timeMs >= MIN_TIME_MS

    /** The altitude if finite and in range, else null (keep the point, drop only the bad field). */
    fun sanitizeAltitude(altitude: Double?): Double? =
        altitude?.takeIf { it.isFinite() && it in MIN_ALTITUDE..MAX_ALTITUDE }

    /** The speed if finite and in range, else null. */
    fun sanitizeSpeed(speed: Double?): Double? =
        speed?.takeIf { it.isFinite() && it in MIN_SPEED..MAX_SPEED }

    /** The barometric pressure (hPa) if finite and in range, else null. */
    fun sanitizePressure(pressure: Double?): Double? =
        pressure?.takeIf { it.isFinite() && it in MIN_PRESSURE..MAX_PRESSURE }
}
