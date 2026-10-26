package com.routebase.gpsrecorder.autodetect

import android.Manifest
import android.annotation.SuppressLint
import android.app.AlarmManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.SystemClock
import android.util.Log
import androidx.core.content.ContextCompat
import com.routebase.gpsrecorder.GpsRecorderApp
import com.routebase.gpsrecorder.service.GpsRecordingService
import com.routebase.gpsrecorder.util.ActivityType
import com.routebase.gpsrecorder.util.EventLog
import com.routebase.gpsrecorder.util.hasLocationPermission
import com.routebase.gpsrecorder.work.FinalizeRecordingWorker
import com.google.android.gms.location.ActivityRecognition
import com.routebase.gpsrecorder.util.AutoState
import com.google.android.gms.location.ActivityTransition
import com.google.android.gms.location.ActivityTransitionRequest
import com.google.android.gms.location.DetectedActivity

/**
 * Always-on auto-detection state machine. Stateless singleton — all state lives in [com.routebase.gpsrecorder.util.PrefsManager]
 * because the transition/alarm receivers can wake a fresh process. Reacts to Activity Recognition
 * ENTER transitions plus one cooldown alarm, and drives the existing [GpsRecordingService] start/stop
 * (no new recording/upload code).
 *
 * Starts recording eagerly on the first RUNNING/ON_BICYCLE ENTER — this is covered by the
 * activity-recognition foreground-service background-start exemption (API 31+). WALKING deliberately
 * does NOT start a session (it produced constant false positives from walking around indoors/at work);
 * it stays registered only as a keep-alive motion signal so a slow section of a real run doesn't stop
 * the recording. Trade-off: a run that AR first tags as WALKING starts once it upgrades to RUNNING.
 * The "was this a real workout?" gate is the server-side min-duration/distance adjudicator + web review
 * queue, not a phone-side confirm window. Only the stop path uses an alarm, which carries no restriction.
 */
object AutoDetectController {

    private const val TAG = "AutoDetectController"

    // Cooldown before finalizing after motion stops: long for STILL (red lights / rest stops),
    // short for IN_VEHICLE (driving means the workout is over).
    private const val COOLDOWN_STILL_MS = 5 * 60_000L
    private const val COOLDOWN_VEHICLE_MS = 2 * 60_000L

    private const val REQ_TRANSITIONS = 0
    private const val REQ_COOLDOWN = 1

    // ── Public entry points ─────────────────────────────────────────────────

    fun enable(context: Context) {
        val app = context.applicationContext as GpsRecorderApp
        app.prefs.autoDetectEnabled = true
        app.prefs.autoState = stateOnEnable(
            capturing = app.prefs.isCapturing,
            autoDetected = app.prefs.currentAutoDetected,
            gracePaused = app.prefs.gracePause
        )
        registerTransitions(context)
    }

    /**
     * The state the machine must (re-)enter when transitions are registered.
     *
     * [enable] is not only the user's toggle — it is also the boot / app-update re-registration path
     * (BootReceiver), which runs one line after a still-capturing recording has been restarted. Forcing IDLE
     * there desyncs the machine from a live auto ride, and the IDLE branch ignores STILL/IN_VEHICLE, so the
     * cooldown never arms again and the AR stop is dead for the rest of the ride. The idle watchdog cannot
     * cover that: it fires on no GPS *displacement*, and a drive home keeps the anchor advancing — the
     * recording would run for the whole car journey.
     *
     * RECORDING, never the stored state: a COOLDOWN's alarm does not survive a reboot, so resuming it would
     * wait for a wake-up that never comes. The next stop signal re-arms a fresh cooldown.
     *
     * A MANUAL recording stays IDLE. Auto-detect must never stop a ride the user started by hand — and it
     * would: RECORDING + a STILL transition arms a cooldown, whose elapse calls finishRecording.
     *
     * Grace stays IDLE too — that IS the grace state (paused-but-open), and the IDLE branch is what resumes
     * it on the next Run/Ride.
     *
     * Pure (booleans in, state out) so the reboot-mid-ride cases are unit-testable without Robolectric.
     */
    internal fun stateOnEnable(capturing: Boolean, autoDetected: Boolean, gracePaused: Boolean): AutoState =
        if (capturing && autoDetected && !gracePaused) AutoState.RECORDING else AutoState.IDLE

    fun disable(context: Context) {
        val app = context.applicationContext as GpsRecorderApp
        app.prefs.autoDetectEnabled = false
        cancelCooldown(context)
        unregisterTransitions(context)
        app.prefs.autoState = AutoState.IDLE
    }

    /** Called by [ActivityTransitionReceiver] for each transition event. */
    fun onTransition(context: Context, detectedActivity: Int, isEnter: Boolean) {
        if (!isEnter) return   // v1 reacts to ENTER only ("now doing X")
        val app = context.applicationContext as GpsRecorderApp
        if (!app.prefs.autoDetectEnabled) return

        reconcile(app, context)
        // Diagnostic: record what AR actually reports, on-device. This is the one signal missing when
        // auto-start "does nothing" — it distinguishes AR misclassifying a ride (e.g. IN_VEHICLE) or
        // firing late from AR staying silent, none of which a start-only log can tell apart.
        EventLog.log("AR ENTER ${activityName(detectedActivity)} (state=${currentState(app)})")
        val type = activityTypeFor(detectedActivity)

        when (currentState(app)) {
            AutoState.IDLE -> {
                if (type != null && isStartMotion(detectedActivity)) {
                    // A Run/Ride within the grace window resumes the paused session — same activity
                    // continues instead of splitting (the 21:00–22:06 five-fragment ride).
                    if (app.prefs.gracePause && isRecordingActive(app)) {
                        resumeFromGrace(context, app)
                    } else if (!isRecordingActive(app)) {
                        // Nothing recording yet → begin. WALKING is excluded here (see isStartMotion)
                        // so walking around indoors/at work never auto-starts a session.
                        startRecording(context, app, type)
                    }
                }
            }
            AutoState.RECORDING -> {
                // Motion stopped → begin the cooldown grace period (keep recording meanwhile).
                // (No mid-recording retype: post-hotfix a session only ever auto-starts as Run/Ride
                // — isStartMotion excludes WALKING — so the old Walk→Run/Ride correction can't fire.)
                if (isStopSignal(detectedActivity)) {
                    app.prefs.autoState = AutoState.COOLDOWN
                    app.prefs.cooldownSignal = stopSignalName(detectedActivity)
                    scheduleCooldown(context, cooldownFor(detectedActivity))
                }
            }
            AutoState.COOLDOWN -> when {
                // Genuine resume (Run/Ride) before the grace period elapsed → stay recording.
                isStartMotion(detectedActivity) -> {
                    cancelCooldown(context)
                    app.prefs.autoState = AutoState.RECORDING
                }
                // Another stop signal refreshes the timer (e.g. STILL → IN_VEHICLE shortens it).
                isStopSignal(detectedActivity) -> {
                    app.prefs.cooldownSignal = stopSignalName(detectedActivity)
                    scheduleCooldown(context, cooldownFor(detectedActivity))
                }
                // else (a stray WALKING while stationary — the locker case): ignore, let the alarm fire.
            }
        }
    }

    /** Called by [AutoDetectAlarmReceiver] when the cooldown grace period elapses. */
    fun onCooldownElapsed(context: Context) {
        val app = context.applicationContext as GpsRecorderApp
        // The alarm may have been armed for an auto ride the user has since stopped — reconcile first,
        // or a stale COOLDOWN finishes the MANUAL ride that replaced it (the alarm survives manual stops).
        reconcile(app, context)
        if (currentState(app) != AutoState.COOLDOWN) return
        // AR said "stopped", but GPS displacement is the authority: slow technical riding reads as
        // STILL/WALKING to the accelerometer classifier. Re-arm instead of stopping while the movement
        // anchor keeps advancing (locker/indoors has no fixes → no displacement → stops as before).
        val msSinceMoved = System.currentTimeMillis() - app.prefs.lastMovedMs
        val signal = app.prefs.cooldownSignal
        if (app.prefs.lastMovedMs > 0 &&
            CooldownPolicy.shouldRearm(signal, msSinceMoved, app.prefs.recentSpeedKmh.toDouble())
        ) {
            EventLog.log("Cooldown re-armed: GPS moved ${msSinceMoved / 1000}s ago ($signal)")
            scheduleCooldown(context, cooldownFor(signalFor(signal)))
            return
        }
        finishRecording(context, app)
    }

    // ── State-machine helpers ───────────────────────────────────────────────

    /** DetectedActivity constant → readable name for the diagnostic EventLog. Pure (unit-tested). */
    internal fun activityName(detected: Int): String = when (detected) {
        DetectedActivity.ON_BICYCLE -> "ON_BICYCLE"
        DetectedActivity.RUNNING -> "RUNNING"
        DetectedActivity.WALKING -> "WALKING"
        DetectedActivity.STILL -> "STILL"
        DetectedActivity.IN_VEHICLE -> "IN_VEHICLE"
        DetectedActivity.ON_FOOT -> "ON_FOOT"
        DetectedActivity.TILTING -> "TILTING"
        DetectedActivity.UNKNOWN -> "UNKNOWN"
        else -> "activity#$detected"
    }

    /** Motion → recordable [ActivityType]; `.wire` crosses to prefs/intent in [startRecording]. */
    private fun activityTypeFor(detected: Int): ActivityType? = when (detected) {
        DetectedActivity.ON_BICYCLE -> ActivityType.RIDE
        DetectedActivity.RUNNING -> ActivityType.RUN
        DetectedActivity.WALKING -> ActivityType.WALK
        else -> null   // STILL / IN_VEHICLE / unknown are not workouts
    }

    private fun isStopSignal(detected: Int): Boolean =
        detected == DetectedActivity.STILL || detected == DetectedActivity.IN_VEHICLE

    /** Persisted name for prefs.cooldownSignal (the alarm receiver runs in a fresh process). */
    private fun stopSignalName(detected: Int): String =
        if (detected == DetectedActivity.IN_VEHICLE) CooldownPolicy.SIGNAL_IN_VEHICLE
        else CooldownPolicy.SIGNAL_STILL

    /** Inverse of [stopSignalName] for re-arm scheduling. */
    private fun signalFor(name: String?): Int =
        if (name == CooldownPolicy.SIGNAL_IN_VEHICLE) DetectedActivity.IN_VEHICLE
        else DetectedActivity.STILL

    /** Motions that may *start* a recording. WALKING is excluded (false positives); it only keeps a
     * recording alive once started. */
    private fun isStartMotion(detected: Int): Boolean =
        detected == DetectedActivity.RUNNING || detected == DetectedActivity.ON_BICYCLE

    private fun cooldownFor(detected: Int): Long =
        if (detected == DetectedActivity.IN_VEHICLE) COOLDOWN_VEHICLE_MS else COOLDOWN_STILL_MS

    /** A recording is capturing right now — online or offline, session created or not. Note this says
     * nothing about the upload queue: a backlog of undelivered recordings must never block a new ride. */
    private fun isRecordingActive(app: GpsRecorderApp): Boolean = app.prefs.isCapturing

    /** Persisted state, defaulting to IDLE if the stored name is ever unrecognized (AU-10.2: a future
     * enum rename after an app update would otherwise crash every transition/alarm receiver). */
    private fun currentState(app: GpsRecorderApp): AutoState =
        app.prefs.autoState

    /** If we think we're recording but nothing is (user stopped manually / finalized) — or what records
     * is a MANUAL ride that replaced the auto one (user stopped it and started their own) — reset to
     * IDLE. The second case is stateOnEnable's invariant applied to the stale-state path: a leftover
     * RECORDING/COOLDOWN would adopt the manual ride and the cooldown's elapse would stop it mid-workout. */
    private fun reconcile(app: GpsRecorderApp, context: Context) {
        val state = currentState(app)
        if (state != AutoState.RECORDING && state != AutoState.COOLDOWN) return
        if (!isRecordingActive(app) || !app.prefs.currentAutoDetected) {
            cancelCooldown(context)
            app.prefs.autoState = AutoState.IDLE
        }
    }

    private fun startRecording(context: Context, app: GpsRecorderApp, activityType: ActivityType) {
        // AND-20: ACTIVITY_RECOGNITION can outlive location permission. Starting the location-type FGS
        // without it crashes in startForeground (API 34+) and the START_STICKY restart re-crashes in a
        // loop while the ride goes silently unrecorded. Stand down and stay IDLE instead.
        if (!hasLocationPermission(context)) {
            EventLog.log("Auto-start refused: location permission missing")
            app.prefs.autoState = AutoState.IDLE
            return
        }
        app.prefs.autoState = AutoState.RECORDING
        // Set synchronously so the recording's type is persisted before the service coroutine starts.
        app.prefs.currentActivityType = activityType.wire
        val intent = GpsRecordingService.startIntent(
            context, activityType.wire, app.prefs.autoDetectVisibility, autoDetected = true
        )
        try {
            context.startForegroundService(intent)
            Log.i(TAG, "Auto-started recording: ${activityType.wire}")
            EventLog.log("Auto-started recording: ${activityType.wire}")
        } catch (e: Exception) {
            // ForegroundServiceStartNotAllowedException (API 31+) if the AR-transition exemption did not
            // apply. Reset so the next transition retries instead of wedging in RECORDING.
            Log.e(TAG, "Auto-start blocked: ${e.message}", e)
            EventLog.log("Auto-start BLOCKED (${e.javaClass.simpleName}): ${e.message}")
            app.prefs.autoState = AutoState.IDLE
        }
    }

    /** Cooldown elapsed for real → hand the recording to the service's grace pause (pause now, finalize
     * in ~15 min unless a Run/Ride resumes it). */
    private fun finishRecording(context: Context, app: GpsRecorderApp) {
        app.prefs.autoState = AutoState.IDLE
        cancelCooldown(context)
        // The service is normally still running (foreground) during COOLDOWN, so delivering the intent is
        // allowed. If the OS killed it, startService throws (API 31+ background-start restriction) — the
        // old code only logged that, leaving the server session open forever, and the next auto-start
        // 409-reconnected INTO it hours later (the 2h12m in-activity hole). Hand it to the worker instead:
        // it can finalize a recording with no service, and needs no network to do the local part.
        try {
            context.startService(GpsRecordingService.autoPauseIntent(context))
            Log.i(TAG, "Auto-stop → grace pause (cooldown elapsed)")
        } catch (e: Exception) {
            Log.e(TAG, "Grace pause undeliverable (service gone?): ${e.message}", e)
            EventLog.log("Auto-stop with no service — finalizing via worker")
            FinalizeRecordingWorker.finalizeNow(context)
        }
    }

    /** Run/Ride detected while a grace-paused session is still open → resume it (same activity). */
    private fun resumeFromGrace(context: Context, app: GpsRecorderApp) {
        app.prefs.autoState = AutoState.RECORDING
        try {
            // startForegroundService: covered by the AR background-start exemption when the service is
            // dead (killed during grace); a live paused service just receives the intent.
            context.startForegroundService(GpsRecordingService.resumeIntent(context))
            Log.i(TAG, "Grace resume: continuing paused auto session")
        } catch (e: Exception) {
            Log.e(TAG, "Grace resume blocked: ${e.message}", e)
            app.prefs.autoState = AutoState.IDLE
        }
    }

    // ── Activity Recognition registration ────────────────────────────────────

    private fun hasPermission(context: Context): Boolean =
        Build.VERSION.SDK_INT < Build.VERSION_CODES.Q ||   // pre-Q it is an install-time GMS permission
            ContextCompat.checkSelfPermission(context, Manifest.permission.ACTIVITY_RECOGNITION) ==
            PackageManager.PERMISSION_GRANTED

    @SuppressLint("MissingPermission")   // guarded by hasPermission()
    private fun registerTransitions(context: Context) {
        if (!hasPermission(context)) {
            Log.w(TAG, "ACTIVITY_RECOGNITION not granted; skipping transition registration")
            EventLog.log("Auto-detect registration skipped: ACTIVITY_RECOGNITION not granted")
            return
        }
        val transitions = mutableListOf<ActivityTransition>()
        for (activityType: Int in intArrayOf(
            DetectedActivity.ON_BICYCLE, DetectedActivity.RUNNING, DetectedActivity.WALKING,
            DetectedActivity.STILL, DetectedActivity.IN_VEHICLE
        )) {
            transitions += ActivityTransition.Builder()
                .setActivityType(activityType)
                .setActivityTransition(ActivityTransition.ACTIVITY_TRANSITION_ENTER)
                .build()
        }
        // Visibility: the registration outcome is the one signal that was missing when auto-detect
        // silently went dead after the fresh install — a failed request only ever hit logcat. Now the
        // event log answers "did the subscription take?" on-device. Only fires on toggle/boot/update,
        // so it is not chatty.
        ActivityRecognition.getClient(context)
            .requestActivityTransitionUpdates(ActivityTransitionRequest(transitions), transitionPendingIntent(context))
            .addOnSuccessListener { EventLog.log("Auto-detect transitions registered") }
            .addOnFailureListener {
                Log.e(TAG, "requestActivityTransitionUpdates failed: ${it.message}", it)
                EventLog.log("Auto-detect registration FAILED: ${it.message}")
            }
    }

    @SuppressLint("MissingPermission")   // guarded by hasPermission(), mirroring registerTransitions
    private fun unregisterTransitions(context: Context) {
        if (!hasPermission(context)) {
            // Permission was revoked after registering (Settings → Permissions → Physical activity → Deny).
            // GMS still throws SecurityException from removeActivityTransitionUpdates in that state even
            // though the subscription itself is dead-lettered by the OS revoke — nothing to unregister.
            Log.w(TAG, "ACTIVITY_RECOGNITION not granted; skipping transition unregistration")
            return
        }
        try {
            ActivityRecognition.getClient(context)
                .removeActivityTransitionUpdates(transitionPendingIntent(context))
                .addOnFailureListener { Log.e(TAG, "removeActivityTransitionUpdates failed: ${it.message}", it) }
        } catch (e: SecurityException) {
            // Revoked between the check above and this call — same race the register path can't hit
            // (it guards then immediately calls), but disable() is user-triggered and less time-critical.
            Log.e(TAG, "removeActivityTransitionUpdates denied: ${e.message}", e)
        }
    }

    private fun transitionPendingIntent(context: Context): PendingIntent =
        PendingIntent.getBroadcast(
            context, REQ_TRANSITIONS,
            Intent(context, ActivityTransitionReceiver::class.java),
            // AR fills the transition result into the intent → must be mutable.
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_MUTABLE
        )

    // ── Cooldown alarm ───────────────────────────────────────────────────────

    private fun scheduleCooldown(context: Context, delayMs: Long) {
        val am = context.getSystemService(Context.ALARM_SERVICE) as AlarmManager
        // Inexact + allow-while-idle: a few minutes of slop on a 5-minute grace period is fine, and it
        // stays Doze-friendly (no exact-alarm permission needed).
        am.setAndAllowWhileIdle(
            AlarmManager.ELAPSED_REALTIME_WAKEUP,
            SystemClock.elapsedRealtime() + delayMs,
            cooldownPendingIntent(context)
        )
    }

    private fun cancelCooldown(context: Context) {
        val am = context.getSystemService(Context.ALARM_SERVICE) as AlarmManager
        am.cancel(cooldownPendingIntent(context))
    }

    private fun cooldownPendingIntent(context: Context): PendingIntent =
        PendingIntent.getBroadcast(
            context, REQ_COOLDOWN,
            Intent(context, AutoDetectAlarmReceiver::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
}
