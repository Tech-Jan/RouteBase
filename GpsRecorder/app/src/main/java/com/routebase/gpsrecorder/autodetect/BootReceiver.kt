package com.routebase.gpsrecorder.autodetect

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import com.routebase.gpsrecorder.GpsRecorderApp
import com.routebase.gpsrecorder.service.GpsRecordingService
import com.routebase.gpsrecorder.util.EventLog
import com.routebase.gpsrecorder.util.hasLocationPermission
import com.routebase.gpsrecorder.work.FinalizeRecordingWorker

/**
 * After a reboot or app update: reconciles a recording the FGS was killed under (AU-02) and
 * re-registers activity-transition updates (neither survives boot/update). The transition
 * re-registration is a no-op unless the user has auto-detect enabled.
 */
class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        when (intent.action) {
            Intent.ACTION_BOOT_COMPLETED, Intent.ACTION_MY_PACKAGE_REPLACED -> {
                val app = context.applicationContext as GpsRecorderApp
                reconcileStrandedRecording(app, context)
                if (app.prefs.autoDetectEnabled) AutoDetectController.enable(context)
            }
        }
    }

    /**
     * AU-02: reboot/app-update killed the FGS mid-recording (START_STICKY does not survive either), so
     * prefs still say "capturing" with nothing running — no path captures points and the server session
     * lingers to the 12 h abandon. Best effort: restart the FGS (its recovery branches take over,
     * ignoring the placeholder intent). If API 35+ forbids starting a location FGS from a boot receiver,
     * hand the recording to the finalize worker, which parks it for upload and frees the phone to record
     * again — rather than leaving it stranded and blocking the next ride.
     */
    private fun reconcileStrandedRecording(app: GpsRecorderApp, context: Context) {
        if (!app.prefs.isCapturing) return
        // AND-20: without location permission the service would crash in its own startForeground (API
        // 34+) — this try/catch only covers the SEND. Park via the worker instead, the same fallback the
        // API-35+ branch already uses, so the stranded ride is delivered and the phone can record again.
        if (!hasLocationPermission(context)) {
            EventLog.log("Boot reconcile: location permission missing — parking the stranded recording")
            FinalizeRecordingWorker.finalizeNow(context)
            return
        }
        try {
            context.startForegroundService(Intent(context, GpsRecordingService::class.java))
        } catch (e: Exception) {
            FinalizeRecordingWorker.finalizeNow(context)
        }
    }
}
