package com.routebase.gpsrecorder.autodetect

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent

/** Fires when the post-motion cooldown grace period elapses → finalize the auto-detected recording. */
class AutoDetectAlarmReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        AutoDetectController.onCooldownElapsed(context)
    }
}
