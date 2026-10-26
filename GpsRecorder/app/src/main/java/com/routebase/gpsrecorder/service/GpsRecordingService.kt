package com.routebase.gpsrecorder.service

import android.app.Service
import android.content.Context
import android.content.Intent
import android.location.Location
import android.os.IBinder
import android.os.Looper
import android.os.PowerManager
import android.util.Log
import com.routebase.gpsrecorder.GpsRecorderApp
import com.routebase.gpsrecorder.util.ActivityType
import com.routebase.gpsrecorder.util.AutoState
import com.routebase.gpsrecorder.util.Visibility
import com.routebase.gpsrecorder.data.GpsPointEntity
import com.routebase.gpsrecorder.data.SessionLiveness
import com.routebase.gpsrecorder.util.EventLog
import com.routebase.gpsrecorder.util.PointEnvelope
import com.routebase.gpsrecorder.util.hasLocationPermission
import com.routebase.gpsrecorder.util.readBattery
import com.routebase.gpsrecorder.util.TrackStats
import com.routebase.gpsrecorder.util.formatDistance
import com.routebase.gpsrecorder.work.FinalizeRecordingWorker
import com.routebase.gpsrecorder.work.UploadWorker
import com.google.android.gms.location.FusedLocationProviderClient
import com.google.android.gms.location.LocationCallback
import com.google.android.gms.location.LocationRequest
import com.google.android.gms.location.LocationResult
import com.google.android.gms.location.LocationServices
import com.google.android.gms.location.Priority
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.withContext
import kotlinx.coroutines.sync.withLock
import retrofit2.HttpException
import java.time.Instant

class GpsRecordingService : Service() {

    private val serviceJob = SupervisorJob()
    private val scope = CoroutineScope(Dispatchers.Main + serviceJob)

    private lateinit var fusedClient: FusedLocationProviderClient
    private lateinit var barometer: BarometerReader
    private lateinit var notifier: RecordingNotifier
    // @Volatile: written from Main (start/pause/stop/destroy) and read/released from the IO upload loop,
    // which calls stopRecording/autoGracePause. Adds a memory barrier only — no semantic change.
    @Volatile private var uploadJob: Job? = null
    // The safety watchdogs (no-fix abort, idle auto-stop, GPS-loss notice, notification refresh) used to
    // ride the upload loop's delay. That conflated a NETWORK cadence with LOCAL timers: whenever uploads
    // failed, the backoff stretched the upload delay to MAX_UPLOAD_INTERVAL_MS (60 s) and silently
    // degraded the 20 s GPS-loss notice and the notification clock to 60 s too — exactly when reception
    // is bad and you most want them. They are one loop each now. This one costs no radio: pure local CPU.
    @Volatile private var watchdogJob: Job? = null
    // Kept held only while fixes are being requested (see requestLocationUpdates / releaseWakeLock).
    @Volatile private var wakeLock: PowerManager.WakeLock? = null
    // Guards against re-entrant stops (double taps, manual + auto-detect overlap) — stopSession
    // can run for tens of seconds while activeSessionId is still set, so the button stays "Stop".
    @Volatile private var stopping = false
    // AND-05: the upload loop has given up on this session (repeated 404 — expired / abandoned
    // server-side) and stopped retrying. Instance-scoped, not persisted: a fresh service instance
    // (reconnect, resume) deserves its own retry budget rather than inheriting a stale verdict.
    @Volatile private var uploadDead = false
    // Tail of the batch-persist chain (see locationCallback) — written only from the main looper.
    private var enqueueJob: Job? = null
    // AND-21: serializes the pause/resume SERVER calls so a rapid pause→resume→pause applies in the order
    // the intents arrived — each was launched as its own coroutine before, racing on Dispatchers.IO, so the
    // server could land in a state that disagrees with the phone. Written only from the main looper
    // (onStartCommand → pause/resume), so chaining each call behind the previous via join() is FIFO. Only
    // the network call is chained; the local GPS/notification teardown/re-arm still applies immediately.
    @Volatile private var pauseResumeJob: Job? = null
    private var pointCount: Int
        get() = currentPointCount
        set(value) { currentPointCount = value }
    private val startMutex = Mutex()

    private val locationCallback = object : LocationCallback() {
        override fun onLocationResult(result: LocationResult) {
            // Fixes arrive coalesced (setMaxUpdateDelayMillis) — persist ALL of result.locations,
            // not just lastLocation, or track density collapses to one point per batch.
            val locations = result.locations
            if (locations.isEmpty()) return

            // Drop low-quality fixes (wifi/cell fallback, multipath) before they become map spikes.
            // The server applies a second speed-based teleport filter for anything that slips through.
            // Also drop anything the server's RS-03 validator would 400-reject (bad coord/time), which
            // would otherwise wedge the whole upload queue on that poison point (AQ-05); a bad
            // altitude/speed is nulled below rather than dropped so glitchy sensors keep track geometry.
            val nowMs = System.currentTimeMillis()
            val accepted = locations.filter { loc ->
                (!loc.hasAccuracy() || loc.accuracy <= MAX_ACCURACY_METRES) &&
                    PointEnvelope.isValidFix(loc.latitude, loc.longitude, loc.time)
            }
            // Latest barometric reading, stamped on the whole coalesced batch (pressure changes far slower
            // than the ~5 s batch window). Null on devices without a barometer. Sanitized like altitude and
            // speed below: the server bounds pressure too, and an out-of-envelope value there 400s the batch
            // and costs it its GPS — a bad barometer must never take the track down with it.
            val pressure = PointEnvelope.sanitizePressure(barometer.currentPressureHpa)
            val entities = accepted.map { loc ->
                GpsPointEntity(
                    lat = loc.latitude,
                    lng = loc.longitude,
                    altitude = PointEnvelope.sanitizeAltitude(if (loc.hasAltitude()) loc.altitude else null),
                    speed = PointEnvelope.sanitizeSpeed(if (loc.hasSpeed()) loc.speed.toDouble() else null),
                    pressureHpa = pressure,
                    timestampUtc = Instant.ofEpochMilli(loc.time).toString()
                )
            }
            if (entities.isEmpty()) {
                // Whole batch rejected by the accuracy floor or the envelope. Edge-log once so a run that
                // captures nothing (e.g. every fix a low-accuracy fallback during signal loss) leaves a
                // fingerprint instead of silently producing a zero-point recording.
                if (!allFixesDroppedLogged) {
                    EventLog.log("Dropped all ${locations.size} fix(es) in this batch (accuracy/envelope)")
                    allFixesDroppedLogged = true
                }
                return
            }
            allFixesDroppedLogged = false
            pointCount += entities.size
            accepted.forEach { trackStats.addFix(it.latitude, it.longitude) }
            updateRecentSpeed(nowMs)
            // Edge-log GPS recovery so a mid-run signal gap is visible in the diagnostic log.
            val prevFix = lastFixMs
            lastFixMs = nowMs
            if (prevFix > 0 && nowMs - prevFix > GPS_LOSS_NOTICE_MS) {
                EventLog.log("GPS recovered after ${(nowMs - prevFix) / 1000}s gap")
            }
            gpsLostLogged = false
            // Persist once: the no-fix abort gates on this, not on the in-memory count (which resets on
            // a START_STICKY reconnect).
            if (!app.prefs.currentSessionGotFix) app.prefs.currentSessionGotFix = true
            updateMovement(accepted.last())
            // FIFO: Dispatchers.IO gives no ordering across launches, and Room insert order becomes the
            // track's PERMANENT point order (uploads read ORDER BY id; server keeps receipt order and
            // finalize does not re-sort). Chain each batch on the previous one — main-looper callback,
            // so the handoff itself is single-threaded.
            val previousEnqueue = enqueueJob
            enqueueJob = scope.launch(Dispatchers.IO) {
                previousEnqueue?.join()
                try {
                    app.repository.enqueuePoints(entities)
                } catch (e: kotlinx.coroutines.CancellationException) {
                    throw e
                } catch (e: Exception) {
                    // AND-12: a Room write failure (disk full) must not crash the process — SupervisorJob
                    // does not swallow uncaught child exceptions, and START_STICKY would restart into a
                    // crash loop. These points are lost either way; the service must survive to keep the
                    // session/stop bookkeeping, the notification and the wakelock alive.
                    Log.e(TAG, "enqueuePoints failed — ${entities.size} point(s) lost: ${e.message}", e)
                    EventLog.log("Point persist failed (${entities.size} lost): ${e.message}")
                }
            }
            // One refresh per batch (~every LOCATION_MAX_BATCH_MS), not per fix — keeps the point count
            // fresh even offline (when the upload loop skips its refresh) without per-second wakeups.
            updateNotification()
        }
    }

    // Rolling ~2-min average speed from the trackStats cumulative distance, persisted (throttled) so the
    // cooldown alarm receiver can apply CooldownPolicy's vehicle-speed exception in a fresh process.
    private fun updateRecentSpeed(nowMs: Long) {
        val dist = trackStats.distanceMeters
        // A fresh session reset trackStats — stale samples would yield a negative delta; start over.
        if (speedSamples.isNotEmpty() && speedSamples.last().second > dist) speedSamples.clear()
        speedSamples.addLast(nowMs to dist)
        while (speedSamples.size > 1 && nowMs - speedSamples.first().first > SPEED_WINDOW_MS) {
            speedSamples.removeFirst()
        }
        if (nowMs - lastSpeedWriteMs < SPEED_WRITE_MS) return
        val (t0, d0) = speedSamples.first()
        val dtSec = (nowMs - t0) / 1000.0
        if (dtSec < 30) return   // too little window to trust an average yet
        lastSpeedWriteMs = nowMs
        app.prefs.recentSpeedKmh = ((dist - d0) / dtSec * 3.6).toFloat()
    }

    // Advances the movement anchor when a fix lands more than MOVE_THRESHOLD_M from it, stamping
    // lastMovedMs. The idle watchdog stops the recording when this stops being refreshed. Absence of
    // fixes (locker/indoors) therefore reads as "not moving" and also trips the watchdog — intended.
    private fun updateMovement(loc: Location) {
        val lat = anchorLat
        if (lat.isNaN()) { anchorLat = loc.latitude; anchorLng = loc.longitude; return }
        val out = FloatArray(1)
        Location.distanceBetween(lat, anchorLng, loc.latitude, loc.longitude, out)
        if (out[0] > MOVE_THRESHOLD_M) {
            anchorLat = loc.latitude
            anchorLng = loc.longitude
            app.prefs.lastMovedMs = System.currentTimeMillis()
            // Sticky, unlike lastMovedMs (which start/reconnect/resume re-arm): this is what tells the
            // false-start abort that the recording was a real ride at some point.
            if (!app.prefs.currentSessionMoved) app.prefs.currentSessionMoved = true
        }
    }

    private val app: GpsRecorderApp
        get() = application as GpsRecorderApp

    override fun onCreate() {
        super.onCreate()
        isServiceActive = true
        fusedClient = LocationServices.getFusedLocationProviderClient(this)
        barometer = BarometerReader(this)
        notifier = RecordingNotifier(this, app.prefs)
        notifier.createChannel()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        // B1/AND-05: a stop that never finished (process died mid-drain) survives in prefs. Whatever
        // delivered this start — the START_STICKY restart, a stale notification action, a user tap —
        // the session's only legal direction is finishing that stop, never active recording.
        // stopRecording() is a no-op when this instance is already stopping, so live-service intents
        // arriving mid-stop behave exactly as before.
        if (RestorePolicy.decide(app.prefs.isCapturing, app.prefs.stopRequested) == RestoreAction.RESUME_STOP) {
            // Foreground refused (location revoked, AND-20): the UploadWorker's dead-service park is the
            // fallback that finishes this stop — never a sticky crash-restart loop.
            if (!startForegroundNotification()) return START_NOT_STICKY
            if (!stopping) EventLog.log("Restarted with a stop pending — finishing the stop, not the ride")
            stopRecording()
            return START_STICKY
        }
        when (intent?.action) {
            // AND-29: promote to a real foreground service before pausing. A cold ACTION_PAUSE — the process
            // was killed and a notification action (or a stale/queued intent) starts a fresh instance via
            // startService — otherwise runs pauseRecording on a started-but-NOT-foreground shell that never
            // calls startForeground, so the OS parks it into a dead service that drifts (the recording is
            // left paused with no live foreground to Resume/Stop from). startForeground first makes it a
            // legitimate foreground service showing the paused notification; refuse cleanly if that is
            // refused (location revoked, AND-20), and don't linger as an empty shell when nothing captures.
            ACTION_PAUSE  -> {
                if (!startForegroundNotification()) return START_NOT_STICKY
                if (app.prefs.isCapturing) {
                    pauseRecording()
                } else {
                    EventLog.log("Pause ignored: nothing is capturing")
                    stopForeground(STOP_FOREGROUND_REMOVE)
                    stopSelf()
                }
            }
            // startForeground first: a grace resume may cold-start the service via
            // startForegroundService (killed during grace) — the FGS contract requires it.
            ACTION_RESUME -> {
                if (!startForegroundNotification()) return START_NOT_STICKY
                // AND-10: a stale notification action (or an orphaned pause flag) can deliver Resume with
                // nothing capturing. Running the full resume would put up a recording notification whose
                // every point is dropped (recordingId == 0) and burn GPS + wakelock on nothing.
                if (app.prefs.isCapturing) {
                    doResumeRecording()
                } else {
                    EventLog.log("Resume ignored: nothing is capturing")
                    stopForeground(STOP_FOREGROUND_REMOVE)
                    stopSelf()
                }
            }
            // AND-29's promote-first, for Stop too: a stale notification Stop delivered to a cold instance
            // otherwise runs the whole multi-second drain as a started-but-not-foreground service with no
            // wakelock (background limits + Doze can kill it mid-stop). Best-effort — a refused promotion
            // (location revoked) must still let the stop run and park the points; stopRecording's finally
            // removes the notification and stops the service either way.
            ACTION_STOP   -> { startForegroundNotification(); stopRecording() }
            ACTION_AUTO_PAUSE -> autoGracePause()
            else -> {
                if (intent == null) {
                    // START_STICKY restart with no intent (OS killed the process). Only reconnect to
                    // an existing recording — never start a fresh "Other" one the user didn't ask for.
                    // The placeholder args below are ignored: each recovery branch of startRecording's
                    // `when` (paused / has session / offline capture) resumes with the persisted params.
                    // AND-06: isCapturing alone — sessionPaused WITHOUT a recording id is an orphaned flag
                    // (a queued Pause that landed after the stop cleared prefs). Reconnecting on it would
                    // restore a paused shell with no recording to resume; clear it instead.
                    if (!app.prefs.isCapturing && app.prefs.sessionPaused) {
                        app.prefs.sessionPaused = false
                        app.prefs.pausedByWeb = false   // AND-41: clear the modifier with the orphaned flag.
                    }
                    if (app.prefs.isCapturing) {
                        EventLog.log("Service restarted by OS (START_STICKY) — reconnecting")
                        startRecording(activityType = ActivityType.OTHER.wire, visibility = Visibility.PRIVATE.wire, autoDetected = false, shareLive = false)
                    } else {
                        // Nothing to recover — honor the FGS start contract, then stop.
                        startForegroundNotification()
                        stopForeground(STOP_FOREGROUND_REMOVE)
                        stopSelf()
                    }
                } else {
                    startRecording(
                        activityType = intent.getStringExtra(EXTRA_ACTIVITY_TYPE) ?: ActivityType.OTHER.wire,
                        visibility = intent.getStringExtra(EXTRA_VISIBILITY) ?: Visibility.PRIVATE.wire,
                        autoDetected = intent.getBooleanExtra(EXTRA_AUTO_DETECTED, false),
                        shareLive = intent.getBooleanExtra(EXTRA_SHARE_LIVE, false)
                    )
                }
            }
        }
        return START_STICKY
    }

    // ── Start ─────────────────────────────────────────────────────────────

    private fun startRecording(activityType: String, visibility: String, autoDetected: Boolean, shareLive: Boolean) {
        if (!startForegroundNotification()) return

        scope.launch {
            startMutex.withLock {
                try {
                    when {
                        app.prefs.sessionPaused && app.prefs.isCapturing -> {
                            // Service was killed while paused — restore paused state without resuming.
                            // isCapturing too (AND-06): the flag alone can be an orphan from a Pause that
                            // landed after a stop; restoring on it would put up a paused shell that claims
                            // no recording id and requests no GPS — the next ride silently unrecorded.
                            updateNotification()
                        }
                        app.prefs.isCapturing && isStaleAutoRecording() -> {
                            // A START_STICKY restart can lag the kill by hours. Resuming an auto recording
                            // that has been idle that long would stitch a giant hole into one activity (the
                            // 2h12m case) — park it for delivery and bow out, so the next real ride starts
                            // clean.
                            val idleMin = (System.currentTimeMillis() - app.prefs.lastMovedMs) / 60_000
                            EventLog.log("Resume refused: idle $idleMin min — recording queued for upload")
                            app.repository.parkCurrentRecordingAsync()
                            // onDestroy enqueues the drain (after isServiceActive flips false).
                            stopForeground(STOP_FOREGROUND_REMOVE)
                            stopSelf()
                            return@withLock
                        }
                        app.prefs.isRecording && app.prefs.isCapturing -> {
                            // Service restarted by OS mid-recording — reconnect to the existing session.
                            // isCapturing too: a session id WITHOUT a recording id is the stranded state a
                            // stop-races-start once produced — reconnecting to it would drop every point
                            // (enqueuePoints' recordingId==0 guard); fall to `else` and start fresh instead.
                            Log.i(TAG, "Reconnecting to existing session ${app.prefs.activeSessionId}")
                            anchorLat = Double.NaN   // re-seed on the next fix (reconnectSession re-arms lastMovedMs)
                            app.repository.reconnectSession()
                            requestLocationUpdates()
                            startRecordingLoops()
                        }
                        app.prefs.needsSessionCreation -> {
                            // AQ-06: OS killed the process during a recording that started offline, so it has
                            // no session yet. Resume capture ONLY — the upload loop's tryCreateSession()
                            // creates the server session with the ORIGINAL params. Must NOT fall into `else`,
                            // which would overwrite currentActivityType→"Other"/currentAutoDetected→false
                            // (disarming the auto-session watchdogs) and claim a second recording id for a
                            // recording that already owns one.
                            Log.i(TAG, "Resuming offline recording after restart (session not created yet)")
                            anchorLat = Double.NaN
                            app.prefs.lastMovedMs = System.currentTimeMillis()  // re-arm idle-stop, like reconnect
                            requestLocationUpdates()
                            startRecordingLoops()
                        }
                        else -> {
                            pointCount = 0
                            trackStats.reset()
                            diagnostics.reset(System.currentTimeMillis())
                            lastFixMs = 0L
                            gpsLostLogged = false
                            EventLog.log("Recording started: $activityType (auto=$autoDetected)")
                            app.prefs.currentActivityType = activityType
                            app.prefs.currentAutoDetected = autoDetected
                            app.prefs.currentSessionGotFix = false
                            app.prefs.currentSessionMoved = false
                            // A stop flag orphaned by a death inside a no-op stop (nothing was capturing,
                            // so no clearSession ran) must not mark THIS recording stop-pending.
                            app.prefs.stopRequested = false
                            app.prefs.lastMovedMs = System.currentTimeMillis()
                            anchorLat = Double.NaN
                            app.repository.startSession(
                                activityType = activityType,
                                visibility = visibility,
                                clientVersion = packageManager.getPackageInfo(packageName, 0).versionName ?: "1.0.0",
                                autoDetected = autoDetected,
                                shareLive = shareLive
                            )
                            requestLocationUpdates()
                            startRecordingLoops()
                        }
                    }
                } catch (e: kotlinx.coroutines.CancellationException) {
                    // Service torn down while the start was in flight (Stop → onDestroy → scope cancel).
                    // The recovery below would re-register GPS and re-acquire the wakelock on a DESTROYED
                    // service — nothing would ever release them again. Let the cancellation propagate.
                    throw e
                } catch (e: Exception) {
                    // Offline, server down, or a 409 (a session is already active). None of these stop the
                    // ride: the recording already owns its id and its params, so capture starts anyway and
                    // the upload loop's tryCreateSession creates the session — resolving any 409 — as soon
                    // as it can. Points are safe in the local queue until then.
                    Log.w(TAG, "startSession failed; capturing offline meanwhile: ${e.message}")
                    requestLocationUpdates()
                    startRecordingLoops()
                }
            }
        }
    }

    /**
     * An auto recording that hasn't moved for [STALE_RECONNECT_MS] at restart time is a dead ride the OS
     * killed, not one in progress. A paused recording is excluded: pause freezes lastMovedMs by design, so
     * "idle" says nothing about it.
     */
    private fun isStaleAutoRecording(): Boolean =
        app.prefs.currentAutoDetected && !app.prefs.sessionPaused && app.prefs.lastMovedMs > 0 &&
            System.currentTimeMillis() - app.prefs.lastMovedMs >= STALE_RECONNECT_MS

    // ── Pause ─────────────────────────────────────────────────────────────

    private fun pauseRecording() {
        // AND-06: a queued Pause landing after the recording ended (stop cleared prefs) must not re-write
        // sessionPaused with nothing capturing — that orphaned flag used to turn the next Start into the
        // paused-restore branch: notification up, no recording id, no GPS, ride silently unrecorded.
        if (stopping || !app.prefs.isCapturing) return
        fusedClient.removeLocationUpdates(locationCallback)
        barometer.stop()
        releaseWakeLock()
        cancelRecordingLoops()

        // Set flag and refresh notification immediately so the UI flips to "Resume" at once.
        app.prefs.sessionPaused = true
        app.prefs.pausedByWeb = false   // AND-41: a local pause (user tap / grace) is never auto-resumed.
        updateNotification()

        // AND-21: chain the server pause behind any in-flight pause/resume call so it applies in intent
        // order. previous?.join() only waits for completion (it never rethrows the previous call's error).
        val previous = pauseResumeJob
        pauseResumeJob = scope.launch(Dispatchers.IO) {
            previous?.join()
            try {
                app.repository.pauseSession()
            } catch (e: kotlinx.coroutines.CancellationException) {
                throw e
            } catch (e: Exception) {
                Log.e(TAG, "pauseSession failed: ${e.message}", e)
            }
        }
    }

    // ── Resume ────────────────────────────────────────────────────────────

    private fun doResumeRecording() {
        // A resume (grace auto-resume or manual button) always ends the grace window: the recording
        // continues, so the pending finalize must not fire. AND-25: endGrace() closes it atomically against
        // the finalize worker's claimGraceStop(), so a finalize racing this resume can't stop the live ride.
        if (app.prefs.gracePause) {
            app.prefs.endGrace()
            EventLog.log("Grace resume — same activity continues")
        }
        FinalizeRecordingWorker.cancel(this)
        // Clear flag and refresh notification immediately so the UI flips to "Pause" at once.
        app.prefs.sessionPaused = false
        app.prefs.pausedByWeb = false   // AND-41: a manual resume ends the pause, web-mirrored or not.
        updateNotification()

        // AU-03: pause froze lastMovedMs (updates removed). Re-arm both watchdog clocks — otherwise an
        // auto session paused >IDLE_STOP_MS and resumed in place self-stops ~5 s after Resume (its first
        // fixes land within MOVE_THRESHOLD_M of the stale anchor, so lastMovedMs never advances).
        app.prefs.lastMovedMs = System.currentTimeMillis()
        anchorLat = Double.NaN   // re-seed on the next fix

        // Local GPS/loops re-arm now, synchronously on the main looper: onStartCommand runs on it, so a
        // Stop/onDestroy (also main) can't interleave and re-register GPS on a torn-down service — the race
        // the old post-network coroutine guarded with a CancellationException rethrow. acquireWakeLock's
        // check-then-create stays single-threaded (main) for the same reason. Decoupled from the server
        // call so a resume never has to wait behind an offline pause's HTTP timeout.
        requestLocationUpdates()
        startRecordingLoops()

        // AND-21: the server resume is chained behind any in-flight pause/resume call so it applies in the
        // order the intents arrived (see pauseResumeJob). Network only — the local re-arm already ran above.
        val previous = pauseResumeJob
        pauseResumeJob = scope.launch(Dispatchers.IO) {
            previous?.join()
            try {
                app.repository.resumePausedSession(app.prefs.activeSessionId)
            } catch (e: kotlinx.coroutines.CancellationException) {
                throw e
            } catch (e: Exception) {
                Log.e(TAG, "resumePausedSession failed: ${e.message}", e)
            }
        }
    }

    // ── Auto grace pause ──────────────────────────────────────────────────

    /** Auto-detect decided the workout ended (cooldown elapsed / idle). Instead of finalizing — which
     * split one evening ride into five activities — pause the session and keep it open for GRACE_MS:
     * a Run/Ride within the window resumes it (same activity), otherwise [FinalizeRecordingWorker] stops
     * it for real. Manual recordings never take this path. */
    private fun autoGracePause() {
        if (stopping || app.prefs.sessionPaused) return
        if (!app.prefs.currentAutoDetected) {
            // Only auto recordings get a grace window — and the only way this intent reaches a MANUAL
            // recording is a stale auto-detect state (its cooldown armed for a ride the user replaced).
            // The old stopRecording() "safety" here was the opposite: it ended the user's manual ride
            // mid-workout. Auto-detect must never stop a ride the user started by hand — no-op.
            EventLog.log("Auto grace-pause ignored: current recording is manual")
            return
        }
        EventLog.log(
            "Grace pause (${pointCount} pts, ${formatDistance(trackStats.distanceMeters)}) — " +
                "finalize in ${GRACE_MS / 60_000} min unless resumed"
        )
        // Flags before pauseRecording so its notification refresh already shows the auto-paused text.
        app.prefs.gracePause = true
        app.prefs.graceUntilMs = System.currentTimeMillis() + GRACE_MS
        // The idle-stop path reaches here without going through AutoDetectController, whose state machine
        // would otherwise stay RECORDING — a Run/Ride ENTER would then fall into the no-op RECORDING
        // branch instead of the IDLE grace-resume, stranding the recording paused until finalize.
        app.prefs.autoState = AutoState.IDLE
        pauseRecording()
        FinalizeRecordingWorker.scheduleGraceFinalize(this, GRACE_MS)
    }

    // ── Stop ──────────────────────────────────────────────────────────────

    private fun stopRecording() {
        if (stopping) return
        stopping = true
        // B1/AND-05: persist the stop intent before the drain starts — `stopping` alone dies with the
        // process, and the restore paths would resurrect this manually-stopped ride as active. Cleared
        // by clearSession() when the stop completes (delivered or parked).
        app.prefs.stopRequested = true
        // A real stop during grace (user button / finalize worker) ends the window for good. AND-25:
        // endGrace() closes it under the same lock claimGraceStop() uses, so a grace-finalize firing at the
        // same instant sees the window already closed and aborts instead of double-stopping.
        app.prefs.endGrace()
        FinalizeRecordingWorker.cancel(this)
        EventLog.log("Stop requested (${pointCount} pts, ${formatDistance(trackStats.distanceMeters)})")
        diagnostics.summaryOnce(System.currentTimeMillis())?.let { EventLog.log(it) }
        cancelRecordingLoops()
        fusedClient.removeLocationUpdates(locationCallback)
        barometer.stop()
        scope.launch {
            try {
                // startMutex: a Stop tapped while the start's session-create HTTP call is still in flight
                // must wait for it — an unserialized stop cleared prefs, then the landing response wrote
                // activeSessionId into them (currentRecordingId already 0), and the NEXT ride's reconnect
                // branch dropped every point.
                startMutex.withLock { app.repository.stopSession() }
            } catch (e: Exception) {
                // stopSession parks the recording on every failure it can foresee; a throw here means it
                // died before doing so, and the recording is still the phone's — park it, don't strand it.
                // NonCancellable (AND-09): this catch also sees the service's own destruction (onDestroy →
                // serviceJob.cancel() → CancellationException), and a park attempted in the cancelled scope
                // is a silent no-op — the dead-service worker net would deliver eventually, but only after
                // its spaced-ticks budget, with a window in which a new Start reconnects half-stopped.
                Log.e(TAG, "stopSession failed: ${e.message}", e)
                runCatching { withContext(NonCancellable) { app.repository.parkCurrentRecordingAsync() } }
            } finally {
                // Release only after the final flush — stopSession can run for tens of seconds and we
                // don't want the CPU sleeping mid-upload.
                releaseWakeLock()
                // Anything the stop couldn't deliver (offline / limiter / failed stop call) is now in the
                // queue; onDestroy enqueues the drain — after isServiceActive flips false, so the worker
                // doesn't just see the service alive, Result.retry(), and back off ~30 s first.
                stopForeground(STOP_FOREGROUND_REMOVE)
                stopSelf()
            }
        }
    }

    // ── Internals ─────────────────────────────────────────────────────────

    private fun requestLocationUpdates() {
        // AND-20: auto-detect/boot paths can reach here with location revoked (ACTIVITY_RECOGNITION
        // outlives it), and the unguarded register threw SecurityException — once even INSIDE the start
        // path's recovery catch, where the second throw escaped the coroutine and crash-looped the sticky
        // service. Record the fact and bow out; the ride is simply not recordable without location.
        if (!hasLocationPermission(this)) {
            EventLog.log("GPS updates refused: location permission missing")
            return
        }
        acquireWakeLock()
        barometer.start()
        val request = LocationRequest.Builder(Priority.PRIORITY_HIGH_ACCURACY, LOCATION_INTERVAL_MS)
            .setMinUpdateIntervalMillis(LOCATION_INTERVAL_MS)
            // Let the GPS/modem batch fixes for up to LOCATION_MAX_BATCH_MS and deliver them in a burst
            // so the app processor can sleep in between — the dominant battery win. Track stays 1 Hz;
            // only delivery is coalesced. Matches the upload loop's cadence, so no added upload latency.
            .setMaxUpdateDelayMillis(LOCATION_MAX_BATCH_MS)
            .build()
        try {
            fusedClient.requestLocationUpdates(request, locationCallback, Looper.getMainLooper())
        } catch (e: SecurityException) {
            // Revoked between the check above and the register — same stance, never a crash loop.
            EventLog.log("GPS updates refused: ${e.message}")
        }
    }

    // Partial wakelock keeps the CPU running so the upload loop and fix processing survive Doze /
    // screen-off — the foreground service alone does not guarantee this. No timeout: the recording can
    // run for hours; the release paths (pause / stop / destroy) below bound its lifetime instead.
    private fun acquireWakeLock() {
        val lock = wakeLock ?: getSystemService(PowerManager::class.java)
            .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, WAKE_LOCK_TAG)
            .also { it.setReferenceCounted(false); wakeLock = it }
        if (!lock.isHeld) lock.acquire()
    }

    private fun releaseWakeLock() {
        wakeLock?.let { if (it.isHeld) it.release() }
    }

    /** Both recording loops. Every start path calls this, so neither can be started without the other. */
    private fun startRecordingLoops() {
        startWatchdogLoop()
        startUploadLoop()
    }

    /** Both recording loops. Every teardown path (pause / stop / destroy) calls this, so neither can leak. */
    private fun cancelRecordingLoops() {
        uploadJob?.cancel()
        uploadJob = null
        watchdogJob?.cancel()
        watchdogJob = null
    }

    /**
     * The LOCAL timers: no-fix abort, idle auto-stop, the GPS-loss notice and the notification refresh.
     * Fixed tick, no network — so the upload cadence can be widened for battery without dragging a 20 s
     * threshold and a live clock out to a minute with it (see [watchdogJob]).
     */
    private fun startWatchdogLoop() {
        watchdogJob?.cancel()
        watchdogJob = scope.launch(Dispatchers.IO) {
            while (true) {
                delay(WATCHDOG_INTERVAL_MS)
                // The four safety decisions (skip-while-paused, no-fix abort, false-start abort, idle
                // grace-pause) live in WatchdogPolicy — pure, unit-tested. Read the prefs into a snapshot,
                // decide, then run the same side effect exactly where it ran before: the Dispatchers.Main
                // hops (stop/pause mutate pauseResumeJob, whose chaining is only safe on the main looper —
                // this loop is IO, AND-34) and the return@launch teardown all stay here.
                val state = WatchdogState(
                    sessionPaused = app.prefs.sessionPaused,
                    autoDetected = app.prefs.currentAutoDetected,
                    gotFix = app.prefs.currentSessionGotFix,
                    moved = app.prefs.currentSessionMoved,
                    recordingStartTimeMs = app.prefs.recordingStartTimeMs,
                    lastMovedMs = app.prefs.lastMovedMs,
                )
                when (WatchdogPolicy.decide(state, System.currentTimeMillis())) {
                    WatchdogAction.SKIP_TICK -> continue
                    WatchdogAction.ABORT_NO_FIX -> {
                        Log.i(TAG, "No usable GPS fix after ${WatchdogPolicy.NO_FIX_ABORT_MS / 60_000} min — aborting auto recording")
                        withContext(Dispatchers.Main) { stopRecording() }
                        return@launch
                    }
                    WatchdogAction.ABORT_NO_MOVE -> {
                        Log.i(TAG, "No movement since start after ${WatchdogPolicy.NO_MOVE_ABORT_MS / 60_000} min — false auto start")
                        EventLog.log("Auto recording never moved — aborted as a false start")
                        withContext(Dispatchers.Main) { stopRecording() }
                        return@launch
                    }
                    WatchdogAction.GRACE_PAUSE -> {
                        Log.i(TAG, "No movement for ${WatchdogPolicy.IDLE_STOP_MS / 60_000} min — grace-pausing idle recording")
                        withContext(Dispatchers.Main) { autoGracePause() }
                        return@launch
                    }
                    WatchdogAction.NONE -> { /* fall through to the GPS-loss latch + notification refresh */ }
                }

                // Edge-log a GPS gap once (not every tick) so the diagnostic log shows when fixes stop.
                val fix = lastFixMs
                if (fix > 0 && System.currentTimeMillis() - fix > GPS_LOSS_NOTICE_MS && !gpsLostLogged) {
                    EventLog.log("No GPS for ${(System.currentTimeMillis() - fix) / 1000}s")
                    gpsLostLogged = true
                }

                // Refresh on the watchdog tick (not on upload success) so the GPS-loss warning + elapsed
                // clock stay live even when offline, when fixes have stopped, or when the upload cadence
                // is idle-wide.
                updateNotification()

                // Power/thermal sampling rides this tick on purpose — no new wakeups, and the battery
                // read is a sticky-broadcast fetch. Logs only when a checkpoint is due.
                diagnostics.onTick(readBattery(this@GpsRecordingService), System.currentTimeMillis())
                    ?.let { EventLog.log(it) }
            }
        }
    }

    /**
     * The NETWORK loop: session creation + point upload, and nothing else.
     *
     * Cadence is gated on whether anyone is actually on the live view. At 5 s the LTE modem's tail timer
     * never expires, so the radio stays awake for the WHOLE ride — the dominant battery cost of the upload
     * path, and pure waste when nobody is watching (for an auto-recording, that is always). Points are still
     * captured at 1 Hz either way; only their delivery is delayed, and they queue locally meanwhile.
     */
    private fun startUploadLoop() {
        uploadJob?.cancel()
        uploadDead = false
        uploadJob = scope.launch(Dispatchers.IO) {
            var currentDelay = UploadCadencePolicy.UPLOAD_INTERVAL_ACTIVE_MS
            // Seeded to now, so a ride STARTS fast and decays to idle only after WATCH_DECAY_MS with nobody
            // watching. A viewer who opens the live view in the first minutes gets a live feed immediately.
            var lastWatchedMs = System.currentTimeMillis()
            // AND-05: consecutive appends rejected 404 (the server no longer recognizes this session — it
            // expired, or a grace-resume that should have revived it failed). Any other outcome, including
            // a genuine append success OR a different failure (offline, 5xx), resets this: only a run of
            // 404s specifically means the session itself is gone, not just unreachable right now.
            var consecutive404s = 0
            // AND-41: wall-clock of the last web-pause re-probe. Only advanced while pausedByWeb; drives the
            // WIDE re-probe below so a web RESUME is noticed without the probe riding the fast paused cadence.
            var lastWebProbeMs = 0L
            while (true) {
                delay(currentDelay)
                if (app.prefs.sessionPaused) {
                    // AND-41: a pause the web mirrored here (the WEB_PAUSED branch below) would otherwise
                    // stick forever — this early-continue means the 404 probe never runs again, so a later
                    // web-side RESUME is invisible and uploads stall until the user taps Resume locally.
                    // While the pause is web-mirrored, keep a WIDE re-probe alive (network call — must not
                    // ride the paused loop's 5 s flag-check) and clear it when the session goes ACTIVE,
                    // exactly as a local Resume would. A LOCAL pause (user tap / grace) has pausedByWeb=false
                    // and is never auto-resumed. classifyActiveSession swallows its own errors → UNKNOWN;
                    // only a real teardown CancellationException propagates (AND-27 class), which is correct.
                    if (WebPausePolicy.shouldProbe(app.prefs.pausedByWeb, System.currentTimeMillis() - lastWebProbeMs)) {
                        lastWebProbeMs = System.currentTimeMillis()
                        if (WebPausePolicy.shouldClearPause(app.repository.classifyActiveSession())) {
                            app.prefs.sessionPaused = false
                            app.prefs.pausedByWeb = false
                            EventLog.log("Session resumed from the web — resuming uploads here too")
                            updateNotification()
                        }
                    }
                    // Reset backoff while paused so uploads resume promptly, not at the inflated interval.
                    currentDelay = UploadCadencePolicy.UPLOAD_INTERVAL_ACTIVE_MS
                    continue
                }

                var success = false
                var uploadedPoints = 0

                // Started offline and the session doesn't exist yet — create it once a network is back.
                // CE rethrown (AND-27 class): swallowing the cancellation of a teardown would keep the
                // loop running one more tick and log phantom failures at the exact moment of the stop.
                if (app.prefs.needsSessionCreation) {
                    try { app.repository.tryCreateSession() }
                    catch (e: kotlinx.coroutines.CancellationException) { throw e }
                    catch (_: Exception) {}
                }

                // AND-24: once the session exists, mint the public live-share if the user chose Share live.
                // No-op unless asked and not yet minted — so this is a cheap pref read every tick, and it
                // retries (an offline start gets its link when connectivity returns).
                try { app.repository.ensureShareLinkAsync() }
                catch (e: kotlinx.coroutines.CancellationException) { throw e }
                catch (_: Exception) {}

                // Upload points if session exists.
                if (app.prefs.isRecording) {
                    try {
                        uploadedPoints = app.repository.uploadPendingPoints()
                        success = true
                        consecutive404s = 0
                    } catch (e: HttpException) {
                        if (e.code() == 404) {
                            consecutive404s++
                            Log.w(TAG, "uploadPendingPoints 404 ($consecutive404s/${UploadCadencePolicy.UPLOAD_DEAD_404_THRESHOLD})")
                            if (UploadCadencePolicy.isDead404(consecutive404s)) {
                                // AND-22: a run of 404s means the session is gone OR it was paused from the
                                // web (a paused session 404s appends the same way). Probe the active-session
                                // list before giving up — declaring a web-paused session dead would force the
                                // user to stop and restart a perfectly live ride.
                                when (app.repository.classifyActiveSession()) {
                                    SessionLiveness.WEB_PAUSED -> {
                                        // Not dead — mirror the pause locally so the loop stops hammering and
                                        // the UI flips to Resume; the session stays alive and a Resume
                                        // (button or reconnect) continues it. GPS capture keeps running;
                                        // queued points deliver on resume.
                                        consecutive404s = 0
                                        if (!app.prefs.sessionPaused) {
                                            app.prefs.sessionPaused = true
                                            // AND-41: mark this as a web mirror so the paused-tick loop
                                            // re-probes for a web RESUME (a local pause never does). Seed the
                                            // probe clock: we just probed, so the next re-probe is one wide
                                            // interval out.
                                            app.prefs.pausedByWeb = true
                                            lastWebProbeMs = System.currentTimeMillis()
                                            EventLog.log("Session paused from the web — pausing here too")
                                            updateNotification()
                                        }
                                    }
                                    SessionLiveness.DEAD -> {
                                        // Do NOT auto-reconnect: a fresh session here could silently be the
                                        // wrong ride (another device's), and the whole point is to stop
                                        // hammering a dead one. The user must Stop (which parks whatever local
                                        // points remain — see RecordingRepository.stopSession) and restart.
                                        uploadDead = true
                                        EventLog.log(
                                            "Upload stopped: session no longer exists (404 x$consecutive404s) — " +
                                                "stop and start a new recording"
                                        )
                                        updateNotification()
                                        return@launch
                                    }
                                    // ACTIVE (the 404s were transient) or UNKNOWN (the probe itself failed) —
                                    // don't declare the session dead; reset and let the next ticks retry.
                                    SessionLiveness.ACTIVE, SessionLiveness.UNKNOWN -> consecutive404s = 0
                                }
                            }
                        } else {
                            consecutive404s = 0
                            Log.w(TAG, "uploadPendingPoints failed: ${e.message}")
                        }
                    } catch (e: kotlinx.coroutines.CancellationException) {
                        throw e   // teardown, not an upload failure — no phantom log (AND-27 class)
                    } catch (e: Exception) {
                        consecutive404s = 0
                        Log.w(TAG, "uploadPendingPoints failed: ${e.message}")
                    }
                }

                // The server answers "is anyone watching" on every append (AppendPointsResponse.watched);
                // stamp lastWatchedMs on a watched success so the WATCH_DECAY hysteresis measures from it.
                // The next delay (hysteresis on success, doubling backoff on failure) is UploadCadencePolicy;
                // one clock read per tick feeds both the stamp and the decision (see report — immaterial vs
                // the original's two reads at minute-scale thresholds).
                val nowMs = System.currentTimeMillis()
                if (success && app.repository.lastWatched) lastWatchedMs = nowMs
                // Before the reassignment below: currentDelay is still the interval this wait was spent
                // at, which is the one the elapsed time belongs to.
                diagnostics.onUpload(success, uploadedPoints, currentDelay, nowMs)
                currentDelay = UploadCadencePolicy.nextDelay(success, lastWatchedMs, nowMs, currentDelay)
            }
        }
    }

    private fun updateNotification() = notifier.post(trackStats.distanceMeters, lastFixMs, uploadDead)

    /**
     * The FGS start contract: every entry path must post the ongoing notification before it does anything.
     * API 34+ throws SecurityException from startForeground when the location-type service starts without
     * location permission (AND-20) — catch it and stop, or every START_STICKY restart re-crashes in a loop
     * while the user sees nothing. Returns false when the service must bow out.
     */
    private fun startForegroundNotification(): Boolean = try {
        startForeground(
            RecordingNotifier.NOTIFICATION_ID,
            notifier.build(trackStats.distanceMeters, lastFixMs, uploadDead)
        )
        true
    } catch (e: SecurityException) {
        EventLog.log("Foreground start refused (location permission revoked?): ${e.message}")
        stopSelf()
        false
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onDestroy() {
        // If prefs still say "recording" here, the OS is tearing us down mid-session (the 3h30m case) —
        // log it so the gap has a fingerprint. A clean stop clears isRecording before onDestroy.
        if (app.prefs.isCapturing) EventLog.log("Service destroyed while still recording (OS kill?)")
        // Latched, so this only fires for a ride that never reached stopRecording (the OS-kill case).
        diagnostics.summaryOnce(System.currentTimeMillis())?.let { EventLog.log(it) }
        isServiceActive = false
        // Drain the upload queue once the service is truly gone. Enqueuing here (not from the stop path,
        // where isServiceActive is still true) means the worker's first run sees the service down and
        // drains immediately instead of Result.retry()-ing into a ~30 s backoff. Self-gates on
        // hasQueuedWork(), so a teardown with an empty queue is a no-op.
        UploadWorker.enqueue(this)
        fusedClient.removeLocationUpdates(locationCallback)
        barometer.stop()
        releaseWakeLock()
        cancelRecordingLoops()
        serviceJob.cancel()
        super.onDestroy()
    }

    companion object {
        /** Visible to MainActivity for the in-app status display. Same process, no IPC needed. */
        @Volatile var currentPointCount = 0
            private set

        /** True while the service is alive. Guards against flush calls racing with an active recording. */
        @Volatile var isServiceActive = false
            private set

        // Rolling movement anchor for the idle auto-stop (NaN = unset, re-seeded on the next fix after a
        // session start / reconnect). Static so it survives a service instance swap within the process.
        @Volatile private var anchorLat = Double.NaN
        @Volatile private var anchorLng = Double.NaN

        // Live-notification distance estimate (server owns the authoritative total). Static so it survives
        // an instance swap; reset only on a fresh session start (0 after a process kill — acceptable).
        private val trackStats = TrackStats()
        // Wall-clock of the last accepted fix; 0 = none yet. Drives the notification's GPS-loss warning.
        @Volatile private var lastFixMs = 0L
        // Rolling (timeMs, cumulative distance) samples behind updateRecentSpeed. Main-thread only
        // (locationCallback); static so the window survives an instance swap like trackStats.
        private val speedSamples = ArrayDeque<Pair<Long, Double>>()
        private var lastSpeedWriteMs = 0L
        // Per-ride power/thermal/cadence instrument (see RideDiagnostics). Static for the same reason as
        // trackStats: it must survive a service instance swap mid-ride.
        private val diagnostics = RideDiagnostics()
        // Edge-trigger so a signal gap is logged once, not every upload tick.
        @Volatile private var gpsLostLogged = false
        // Edge-trigger so a batch with every fix rejected (accuracy floor / envelope) is logged once.
        @Volatile private var allFixesDroppedLogged = false

        private const val TAG = "GpsRecordingService"
        const val ACTION_PAUSE  = "com.routebase.gpsrecorder.ACTION_PAUSE"
        const val ACTION_RESUME = "com.routebase.gpsrecorder.ACTION_RESUME"
        const val ACTION_STOP   = "com.routebase.gpsrecorder.ACTION_STOP"
        const val ACTION_AUTO_PAUSE = "com.routebase.gpsrecorder.ACTION_AUTO_PAUSE"
        const val EXTRA_ACTIVITY_TYPE = "activity_type"
        const val EXTRA_VISIBILITY = "visibility"
        const val EXTRA_AUTO_DETECTED = "auto_detected"
        const val EXTRA_SHARE_LIVE = "share_live"
        private const val WAKE_LOCK_TAG = "GpsRecorder::recording"
        // Discard fixes with a horizontal-accuracy radius worse than this (metres). Good outdoor GPS is
        // 3–10 m; a fix reporting >40 m is a wifi/cell fallback likely to land far off route.
        private const val MAX_ACCURACY_METRES = 40f
        private const val LOCATION_INTERVAL_MS = 1000L
        // Coalesce up to this many ms of fixes into one delivery so the AP can sleep between bursts.
        private const val LOCATION_MAX_BATCH_MS = 5000L
        // The upload cadence intervals, the WATCH_DECAY hysteresis, the failure-backoff ceiling and the
        // UPLOAD_DEAD_404_THRESHOLD moved to UploadCadencePolicy with the decision itself.

        // The local-watchdog tick. Short and fixed: it drives a 20 s GPS-loss threshold and the
        // notification's elapsed clock, neither of which may inherit the upload loop's backoff.
        private const val WATCHDOG_INTERVAL_MS = 10_000L
        // The no-fix / false-start / idle-stop thresholds moved to WatchdogPolicy with the decision itself.
        // A fix must land more than this far (metres) from the anchor to count as movement. Above the 40 m
        // accuracy filter so a stationary phone's fix jitter never keeps a recording alive.
        private const val MOVE_THRESHOLD_M = 50f
        // Grace window after an auto-stop: the session stays paused-but-open so a Run/Ride can resume it
        // (one activity, Strava-auto-pause semantics); the finalize worker stops it for real afterwards.
        const val GRACE_MS = 15 * 60_000L
        // An auto session idle this long at reconnect time is a dead session, not a live ride — refuse to
        // stitch a giant hole into it; finalize and let the next start create a fresh one.
        private const val STALE_RECONNECT_MS = 30 * 60_000L
        // Averaging window / prefs-write throttle for the recentSpeedKmh sample (CooldownPolicy input).
        private const val SPEED_WINDOW_MS = 2 * 60_000L
        private const val SPEED_WRITE_MS = 30_000L
        // No accepted fix for this long → warn in the notification + log the gap (screen-off signal loss).
        // internal: RecordingNotifier reads it to decide whether to show the gap warning.
        internal const val GPS_LOSS_NOTICE_MS = 20_000L

        fun startIntent(context: Context, activityType: String, visibility: String, autoDetected: Boolean = false, shareLive: Boolean = false) =
            Intent(context, GpsRecordingService::class.java).apply {
                putExtra(EXTRA_ACTIVITY_TYPE, activityType)
                putExtra(EXTRA_VISIBILITY, visibility)
                putExtra(EXTRA_AUTO_DETECTED, autoDetected)
                putExtra(EXTRA_SHARE_LIVE, shareLive)
            }

        fun pauseIntent(context: Context) =
            Intent(context, GpsRecordingService::class.java).apply { action = ACTION_PAUSE }

        fun resumeIntent(context: Context) =
            Intent(context, GpsRecordingService::class.java).apply { action = ACTION_RESUME }

        fun stopIntent(context: Context) =
            Intent(context, GpsRecordingService::class.java).apply { action = ACTION_STOP }

        fun autoPauseIntent(context: Context) =
            Intent(context, GpsRecordingService::class.java).apply { action = ACTION_AUTO_PAUSE }
    }
}
