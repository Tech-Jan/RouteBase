package com.routebase.gpsrecorder.util

import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.BatteryManager
import android.os.PowerManager
import androidx.core.content.ContextCompat

/**
 * One reading of the phone's power state, for the ride instrument in
 * [com.routebase.gpsrecorder.service.RideDiagnostics].
 *
 * Plain scalars on purpose — no Android types — so the accumulator that folds these can be table-tested
 * on the JVM without Robolectric.
 *
 * [temperatureC] is the BATTERY's sensor, not the SoC or the modem: it lags a hot radio and understates
 * it. It is still the only thermal signal available without root, and it separates the cases that
 * actually matter here (charging while riding / screen baking in the sun / a genuine GNSS grind).
 */
data class BatterySnapshot(
    val levelPercent: Int,
    val temperatureC: Float,
    val charging: Boolean,
    val screenOn: Boolean,
)

/**
 * Reads the power state without registering anything: a null receiver on ACTION_BATTERY_CHANGED returns
 * the STICKY intent the system already holds, so this costs no broadcast, no wakeup and no permission.
 * That is the whole design constraint — an instrument that draws power corrupts what it measures.
 *
 * Returns null if the sticky intent or its scale is missing, so a reading is never silently invented.
 */
fun readBattery(context: Context): BatterySnapshot? {
    // ContextCompat over the bare 2-arg registerReceiver: targetSdk 36 requires the exported flag, and
    // the bare call is a lint error even for the null-receiver sticky read.
    val intent: Intent = ContextCompat.registerReceiver(
        context, null, IntentFilter(Intent.ACTION_BATTERY_CHANGED), ContextCompat.RECEIVER_NOT_EXPORTED
    ) ?: return null
    val level = intent.getIntExtra(BatteryManager.EXTRA_LEVEL, -1)
    val scale = intent.getIntExtra(BatteryManager.EXTRA_SCALE, -1)
    if (level < 0 || scale <= 0) return null
    return BatterySnapshot(
        levelPercent = level * 100 / scale,
        // EXTRA_TEMPERATURE is tenths of a degree C.
        temperatureC = intent.getIntExtra(BatteryManager.EXTRA_TEMPERATURE, 0) / 10f,
        charging = intent.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0) != 0,
        screenOn = context.getSystemService(PowerManager::class.java)?.isInteractive == true,
    )
}
