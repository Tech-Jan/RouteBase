package com.routebase.gpsrecorder.work

import android.content.Context
import androidx.work.CoroutineWorker
import androidx.work.ExistingWorkPolicy
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import androidx.work.workDataOf
import com.routebase.gpsrecorder.GpsRecorderApp
import com.routebase.gpsrecorder.service.GpsRecordingService
import com.routebase.gpsrecorder.util.EventLog
import java.util.concurrent.TimeUnit

/**
 * Ends the current recording when the recording service can't do it itself.
 *
 * Two callers, one job:
 *  - [scheduleGraceFinalize] — the auto grace window elapsed without a resume, so the paused session
 *    should now be stopped for real.
 *  - [finalizeNow] — the service is gone (OS-killed, or a boot/update FGS start was refused), so nobody
 *    is left to deliver the stop. Previously these paths only logged, and the session stayed open on the
 *    server for the next start to reconnect into — the 2h12m in-activity hole.
 *
 * Deliberately has NO network constraint: parking the recording is a local write and must happen even
 * offline, or the phone keeps pointing at a recording it will never finish (and refuses to start the
 * next one). Actual delivery is left to [UploadWorker], which is network-constrained and retries.
 *
 * WorkManager persistence is the point: this survives process death and reboot, which an in-memory
 * timer or a pending alarm on a dead service does not.
 */
class FinalizeRecordingWorker(context: Context, params: WorkerParameters) :
    CoroutineWorker(context, params) {

    override suspend fun doWork(): Result {
        val app = applicationContext as GpsRecorderApp
        val prefs = app.prefs
        val isCapturing = prefs.isCapturing
        val reason = inputData.getString(KEY_REASON)
        val reasonIsGrace = reason == REASON_GRACE

        // The grace timer is cancellable state: a Run/Ride inside the window clears gracePause and cancels
        // this work, but a race (this worker fired just as the user resumed) must not stop a recording that
        // is live. AND-25: claim the stop atomically against the resume path's endGrace() — a plain
        // check-then-act read of gracePause here could pass just before the resume clears it, then stop the
        // now-live ride. claimGraceStop() returns false when the resume won → abort.
        //
        // These are MUTATIONS and stay in the worker (claimGraceStop is the atomic claim, endGrace a write);
        // FinalizeDecision decides over the already-evaluated boolean. Both are gated on isCapturing so a
        // NOTHING_TO_DO tick never touches the grace window — matching the pre-extraction early return.
        val graceClaimed: Boolean = when {
            !isCapturing -> false                            // not reached by the NOTHING_TO_DO branch below
            reasonIsGrace -> prefs.claimGraceStop()          // false when the resume won
            else -> { prefs.endGrace(); true }               // non-grace reason ends the window outright
        }

        when (FinalizeDecision.decide(
            FinalizeState(
                isCapturing = isCapturing,
                reasonIsGrace = reasonIsGrace,
                graceClaimed = graceClaimed,
                isServiceActive = GpsRecordingService.isServiceActive,
            )
        )) {
            FinalizeAction.NOTHING_TO_DO -> return Result.success()     // already stopped — nothing to finalize
            FinalizeAction.ABORT_RESUME_WON -> return Result.success()  // the resume won the claim — change nothing
            FinalizeAction.DELEGATE_TO_SERVICE -> {
                // The service is alive (paused, still foreground) — let it run the normal stop: drain, stop,
                // and park whatever it couldn't deliver. Starting a service is allowed while it is foreground.
                try {
                    applicationContext.startService(GpsRecordingService.stopIntent(applicationContext))
                    return Result.success()
                } catch (_: Exception) {
                    // Died between the check and the call — fall through to the park path below.
                }
            }
            FinalizeAction.PARK_LOCALLY -> {}   // fall through to the park path below
        }

        // No service: hand the recording to the upload queue ourselves, then let UploadWorker deliver it
        // when there is a network. This also frees the phone to start the next recording immediately.
        EventLog.log("Finalizing recording without a service (reason=$reason)")
        app.repository.parkCurrentRecordingAsync()
        UploadWorker.enqueue(applicationContext)
        return Result.success()
    }

    companion object {
        private const val UNIQUE = "finalize-recording"
        private const val KEY_REASON = "reason"
        private const val REASON_GRACE = "grace"
        private const val REASON_NOW = "service_gone"

        /** End of the auto grace window: stop the paused session unless a resume cancelled us first. */
        fun scheduleGraceFinalize(context: Context, delayMs: Long) =
            enqueue(context, REASON_GRACE, delayMs)

        /** The service is gone and can't stop the recording — finalize it now. */
        fun finalizeNow(context: Context) = enqueue(context, REASON_NOW, 0L)

        /** A resume or a real stop happened — the pending finalize must not fire. */
        fun cancel(context: Context) {
            WorkManager.getInstance(context).cancelUniqueWork(UNIQUE)
        }

        private fun enqueue(context: Context, reason: String, delayMs: Long) {
            val request = OneTimeWorkRequestBuilder<FinalizeRecordingWorker>()
                .setInputData(workDataOf(KEY_REASON to reason))
                .setInitialDelay(delayMs, TimeUnit.MILLISECONDS)
                .build()
            // REPLACE: a "service gone" finalize supersedes a pending grace timer, and a new grace window
            // supersedes an older one.
            WorkManager.getInstance(context)
                .enqueueUniqueWork(UNIQUE, ExistingWorkPolicy.REPLACE, request)
        }
    }
}
