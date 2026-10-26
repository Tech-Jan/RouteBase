package com.routebase.gpsrecorder.autodetect

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import com.google.android.gms.location.ActivityTransition
import com.google.android.gms.location.ActivityTransitionResult

/** Receives Activity Recognition ENTER/EXIT transitions and feeds them to [AutoDetectController]. */
class ActivityTransitionReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (!ActivityTransitionResult.hasResult(intent)) return
        val result = ActivityTransitionResult.extractResult(intent) ?: return
        for (event in result.transitionEvents) {
            AutoDetectController.onTransition(
                context,
                event.activityType,
                event.transitionType == ActivityTransition.ACTIVITY_TRANSITION_ENTER
            )
        }
    }
}
