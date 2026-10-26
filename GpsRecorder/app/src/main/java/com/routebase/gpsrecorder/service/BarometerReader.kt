package com.routebase.gpsrecorder.service

import android.content.Context
import android.hardware.Sensor
import android.hardware.SensorEvent
import android.hardware.SensorEventListener
import android.hardware.SensorManager
import com.routebase.gpsrecorder.util.EventLog
import com.routebase.gpsrecorder.util.PointEnvelope

/**
 * Reads the device barometer (`TYPE_PRESSURE`) and exposes the latest atmospheric pressure in hPa. The
 * phone does NO altitude conversion — it ships raw pressure to the server, which owns the elevation math
 * (DEM fusion, server-side ElevationFusion). A light EMA damps sensor jitter without baking in any
 * model. No-op (and [currentPressureHpa] stays null) on devices without a barometer.
 *
 * Lifecycle mirrors the location updates: [start] on recording start/resume, [stop] on pause/stop/destroy.
 */
class BarometerReader(context: Context) {

    private val sensorManager = context.getSystemService(Context.SENSOR_SERVICE) as SensorManager
    private val sensor: Sensor? = sensorManager.getDefaultSensor(Sensor.TYPE_PRESSURE)

    @Volatile private var ema: Double = Double.NaN
    // @Volatile: start()/stop() are reached from Main and (before the resume fix) from an IO coroutine —
    // a stale read here double-registers the sensor listener.
    @Volatile private var registered = false
    // Edge-trigger, like the service's gpsLostLogged: a stuck-bad barometer reports several times a second,
    // and the fingerprint we want is "this sensor went bad", not 30 000 copies of it.
    @Volatile private var rejectedLogged = false

    val hasBarometer: Boolean get() = sensor != null

    /** Latest smoothed pressure in hPa, or null if there is no barometer / no reading yet. */
    val currentPressureHpa: Double? get() = ema.takeUnless { it.isNaN() }

    private val listener = object : SensorEventListener {
        override fun onSensorChanged(event: SensorEvent) {
            // Reject an out-of-envelope sample BEFORE it reaches the EMA. The average is carried into every
            // later point, and the server 400-rejects a whole batch on one bad pressure — taking that
            // batch's GPS with it — so a single garbage reading here would cost track data for as long as it
            // took the EMA to decay back into range. Dropping the sample keeps the last good value instead.
            //
            // Dropped, never substituted: any value inside the envelope is by definition one the elevation
            // fusion will believe (300 hPa reads as ~9 km of altitude), so an in-band "bad" sentinel would
            // corrupt the channel it was meant to flag. The point still ships with its GPS; only the pressure
            // field goes null — the same null a phone without a barometer sends. What null cannot say is
            // "the sensor IS there and it is lying", so that goes in the diagnostic log instead.
            val sample = event.values[0].toDouble()   // hPa
            val raw = PointEnvelope.sanitizePressure(sample) ?: run {
                if (!rejectedLogged) {
                    rejectedLogged = true
                    EventLog.log("Barometer out of range (%.1f hPa) — dropping pressure, GPS unaffected"
                        .format(sample))
                }
                return
            }
            ema = if (ema.isNaN()) raw else EMA_ALPHA * raw + (1 - EMA_ALPHA) * ema
        }
        override fun onAccuracyChanged(sensor: Sensor?, accuracy: Int) {}
    }

    // Idempotent (mirrors the wakelock's isHeld guard): requestLocationUpdates can re-fire on a reconnect.
    fun start() {
        val s = sensor ?: return
        if (registered) return
        rejectedLogged = false   // one line per recording (and per resume), not one per process
        sensorManager.registerListener(listener, s, SensorManager.SENSOR_DELAY_NORMAL)
        registered = true
    }

    fun stop() {
        if (sensor == null || !registered) return
        sensorManager.unregisterListener(listener)
        registered = false
    }

    private companion object {
        // Gentle low-pass: enough to absorb per-sample noise, light enough to track real elevation change.
        const val EMA_ALPHA = 0.3
    }
}
