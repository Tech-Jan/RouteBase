package com.routebase.gpsrecorder.work

import android.content.Context
import android.util.Log
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import com.routebase.gpsrecorder.GpsRecorderApp
import com.routebase.gpsrecorder.service.GpsRecordingService
import com.routebase.gpsrecorder.util.EventLog
import java.util.concurrent.TimeUnit

private const val TAG = "UploadWorker"

/**
 * Delivers the upload queue — recordings that are finished but whose points and/or stop call never
 * reached the server — in the background, so upload no longer waits for the user to reopen the app.
 *
 *  - [enqueue] — one-shot, fired whenever a recording is parked (stop / grace finalize / app resume).
 *  - [schedulePeriodic] — a ~30 min safety net that also recovers from a process kill mid-recording.
 *
 * Both carry a CONNECTED constraint, so WorkManager only runs them when there is a network. The flush
 * refuses to touch the queue while a recording is capturing (the server allows one active session per
 * user), so a worker that wakes mid-ride simply reschedules.
 */
class UploadWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {

    override suspend fun doWork(): Result {
        val app = applicationContext as GpsRecorderApp
        val prefs = app.prefs
        // Read the snapshot and decide up front (pure — no side effects). The dead-service counters and the
        // service-liveness static are untouched by the AND-06 pass below, so reading them before it is
        // equivalent to the pre-extraction order.
        val now = System.currentTimeMillis()
        val action = UploadDecision.decide(
            UploadState(
                isConfigured = prefs.isConfigured,
                isCapturing = prefs.isCapturing,
                isServiceActive = GpsRecordingService.isServiceActive,
                deadServiceTicks = prefs.deadServiceTicks,
                lastDeadServiceTickMs = prefs.lastDeadServiceTickMs,
            ),
            now,
        )
        if (action == UploadAction.NOT_CONFIGURED) return Result.success()

        // AND-06: finish any Discard whose abandon call failed offline, regardless of what else is going
        // on — these sessions own no local recording pointer, so nothing here can conflict with them.
        runCatching { app.repository.retryPendingAbandonsAsync() }
            .onFailure { EventLog.log("Retry-abandon pass failed: ${it.message}") }

        when (action) {
            UploadAction.NOT_CONFIGURED -> {}   // handled above, before AND-06
            UploadAction.YIELD_TO_LIVE_SERVICE -> {
                prefs.deadServiceTicks = 0
                prefs.lastDeadServiceTickMs = 0L
                return Result.retry()   // a live recording owns the server's single active-session slot
            }
            UploadAction.WAIT_FOR_SERVICE_STOP -> return Result.retry()
            UploadAction.WAIT_FOR_TICK_SPACING -> return Result.retry()
            UploadAction.OBSERVE_TICK -> {
                prefs.lastDeadServiceTickMs = now
                prefs.deadServiceTicks++
                return Result.retry()
            }
            UploadAction.PARK_STUCK_CAPTURE -> {
                prefs.lastDeadServiceTickMs = now
                prefs.deadServiceTicks++
                Log.w(TAG, "No live service for ${prefs.deadServiceTicks} spaced tick(s) — parking the stuck capture")
                EventLog.log("Recording's service never came back — parked for delivery")
                val parked = runCatching { app.repository.parkCurrentRecordingAsync() }
                if (parked.isFailure) return Result.retry()
                // Reset only AFTER a successful park (AND-26): resetting first meant every transient park
                // failure restarted the whole spaced-tick budget, adding ~50 min per failure to how long the
                // dead capture blocked the queue.
                prefs.deadServiceTicks = 0
                prefs.lastDeadServiceTickMs = 0L
                // fall through to the queue flush — the just-parked recording is now queued work
            }
            UploadAction.FLUSH -> {}   // fall through to the queue flush
        }

        if (!app.repository.hasQueuedWork()) return Result.success()

        return try {
            app.repository.flushQueueAsync()
            if (app.repository.hasQueuedWork()) Result.retry() else Result.success()
        } catch (e: Exception) {
            EventLog.log("Upload queue error: ${e.message}")
            Result.retry()
        }
    }

    companion object {
        private const val ONE_TIME = "upload-pending"
        private const val PERIODIC = "upload-pending-periodic"

        private val networkConstraint = Constraints.Builder()
            .setRequiredNetworkType(NetworkType.CONNECTED)
            .build()

        /** Fire once, as soon as there is a network, to deliver freshly-queued recordings. */
        fun enqueue(context: Context) {
            val request = OneTimeWorkRequestBuilder<UploadWorker>()
                .setConstraints(networkConstraint)
                .build()
            // REPLACE, not KEEP: a KEEP would be absorbed by an already-finished-but-retrying run and the
            // recording just parked could sit until the periodic net caught it.
            WorkManager.getInstance(context)
                .enqueueUniqueWork(ONE_TIME, ExistingWorkPolicy.REPLACE, request)
        }

        /** Safety net: periodic drain that also recovers a recording the OS killed mid-flight. */
        fun schedulePeriodic(context: Context) {
            val request = PeriodicWorkRequestBuilder<UploadWorker>(30, TimeUnit.MINUTES)
                .setConstraints(networkConstraint)
                .build()
            WorkManager.getInstance(context)
                .enqueueUniquePeriodicWork(PERIODIC, ExistingPeriodicWorkPolicy.KEEP, request)
        }
    }
}
