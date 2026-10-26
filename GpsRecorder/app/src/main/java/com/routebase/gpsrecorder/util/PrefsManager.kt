package com.routebase.gpsrecorder.util

import android.content.Context
import android.content.SharedPreferences
import androidx.core.content.edit

class PrefsManager(context: Context) : RecordingPrefs {

    private val prefs: SharedPreferences =
        context.getSharedPreferences("gps_recorder_prefs", Context.MODE_PRIVATE)

    /** Force-https (item 22): a base URL stored by an older build may be http://. Upgrade same-host to
     * https:// on read (the server is served over TLS) and persist the rewrite, so the API client never
     * speaks cleartext and the user keeps their saved key instead of being forced through re-setup. */
    var serverUrl: String?
        get() {
            val stored = prefs.getString(KEY_SERVER_URL, null) ?: return null
            if (!stored.startsWith("http://")) return stored
            val upgraded = "https://" + stored.removePrefix("http://")
            prefs.edit { putString(KEY_SERVER_URL, upgraded) }
            return upgraded
        }
        set(value) = prefs.edit { putString(KEY_SERVER_URL, value) }

    /** Stored encrypted (Keystore AES/GCM). Legacy plaintext from older builds is migrated on first read. */
    var apiKey: String?
        get() {
            prefs.getString(KEY_API_KEY_ENC, null)?.let { return SecureStore.decrypt(it) }
            // Migrate a pre-encryption plaintext key, then drop it. The migration must never make a READ
            // throw: SecureStore.encrypt can fail on a flaky Keystore, and an unguarded setter call here
            // crashed every launch until data was cleared. Keep the plaintext on failure — the app stays
            // configured and a later read retries the migration.
            val legacy = prefs.getString(KEY_API_KEY, null) ?: return null
            try {
                apiKey = legacy
                prefs.edit { remove(KEY_API_KEY) }
            } catch (_: Exception) {
                // Keystore refused; plaintext stays until a retry succeeds.
            }
            return legacy
        }
        set(value) = prefs.edit {
            if (value == null) remove(KEY_API_KEY_ENC)
            else putString(KEY_API_KEY_ENC, SecureStore.encrypt(value))
            remove(KEY_API_KEY)
        }

    /**
     * Local id of the recording currently capturing (0 = none). Epoch-ms at start, so it is unique and
     * sorts chronologically. Stamped on every queued point ([com.routebase.gpsrecorder.data.GpsPointEntity])
     * and carried into the upload queue when the recording is parked.
     *
     * This — not [activeSessionId] — is what "a recording is in progress" means: a recording started
     * offline captures points for hours before its server session exists.
     */
    override var currentRecordingId: Long
        get() = prefs.getLong(KEY_CURRENT_RECORDING_ID, 0L)
        set(value) = prefs.edit { putLong(KEY_CURRENT_RECORDING_ID, value) }

    /**
     * Start params of the recording currently capturing. Set at EVERY start (not only a failed one), so
     * a recording that never got a session — offline start to finish — can still have one created from
     * the queue with its original type/visibility/timezone.
     */
    override var currentRecordingParams: RecordingParams?
        get() = RecordingParamsCodec.decode(prefs.getString(KEY_CURRENT_PARAMS, null))
        set(value) = prefs.edit {
            val encoded = value?.let { RecordingParamsCodec.encode(it) }
            if (encoded == null) remove(KEY_CURRENT_PARAMS) else putString(KEY_CURRENT_PARAMS, encoded)
        }

    override var activeSessionId: String?
        get() = prefs.getString(KEY_ACTIVE_SESSION_ID, null)
        set(value) = prefs.edit { putString(KEY_ACTIVE_SESSION_ID, value) }

    /** AND-24: the public "/s/{token}" live-share path for the current recording, once minted. Null until
     * the phone has minted it (only when the user chose Share live and the session exists). Cleared with
     * the session. */
    override var activeShareUrl: String?
        get() = prefs.getString(KEY_ACTIVE_SHARE_URL, null)
        set(value) = prefs.edit { putString(KEY_ACTIVE_SHARE_URL, value) }

    override var clientSequence: Int
        get() = prefs.getInt(KEY_CLIENT_SEQUENCE, 0)
        set(value) = prefs.edit { putInt(KEY_CLIENT_SEQUENCE, value) }

    override var recordingStartTimeMs: Long
        get() = prefs.getLong(KEY_RECORDING_START_MS, 0L)
        set(value) = prefs.edit { putLong(KEY_RECORDING_START_MS, value) }

    override var sessionPaused: Boolean
        get() = prefs.getBoolean(KEY_SESSION_PAUSED, false)
        set(value) = prefs.edit { putBoolean(KEY_SESSION_PAUSED, value) }

    /**
     * AND-41: this pause was MIRRORED from a web-side pause (the upload loop saw the session paused via
     * classifyActiveSession), not a local user tap or auto grace-pause. Only a web-mirrored pause may be
     * auto-cleared when the session goes ACTIVE again — a local pause is the user's and stays until they
     * resume. A modifier on [sessionPaused]: only read while it is true, and cleared with it. Persisted
     * like [sessionPaused] so the two never disagree across a process restart.
     */
    var pausedByWeb: Boolean
        get() = prefs.getBoolean(KEY_PAUSED_BY_WEB, false)
        set(value) = prefs.edit { putBoolean(KEY_PAUSED_BY_WEB, value) }

    /** Activity type of the in-progress recording. Read by the notification to pick pace vs speed. */
    override var currentActivityType: String?
        get() = prefs.getString(KEY_CURRENT_ACTIVITY_TYPE, null)
        set(value) = prefs.edit { putString(KEY_CURRENT_ACTIVITY_TYPE, value) }

    /** Whether the in-progress recording was auto-detected. Gates the no-fix abort (a manually started
     * recording must never self-abort). Persisted so it survives a START_STICKY process restart. */
    override var currentAutoDetected: Boolean
        get() = prefs.getBoolean(KEY_CURRENT_AUTO_DETECTED, false)
        set(value) = prefs.edit { putBoolean(KEY_CURRENT_AUTO_DETECTED, value) }

    /** Whether the in-progress recording has ever captured a usable GPS fix. Persisted (not the in-memory
     * point count, which resets to 0 on a START_STICKY reconnect) so the no-fix abort can't wrongly fire
     * on a long recording resumed after process death. */
    var currentSessionGotFix: Boolean
        get() = prefs.getBoolean(KEY_CURRENT_GOT_FIX, false)
        set(value) = prefs.edit { putBoolean(KEY_CURRENT_GOT_FIX, value) }

    /** Wall-clock (ms) of the last significant GPS displacement. Gates the idle auto-stop (no movement for
     * IDLE_STOP_MS → finalize). Reset to now on session start and reconnect so a mid-recording process
     * restart can't false-stop before the anchor re-arms. */
    override var lastMovedMs: Long
        get() = prefs.getLong(KEY_LAST_MOVED_MS, 0L)
        set(value) = prefs.edit { putLong(KEY_LAST_MOVED_MS, value) }

    /**
     * Whether the in-progress recording has EVER displaced (the movement anchor advanced at least once).
     * Gates the false-start abort, which must tell "never was a ride" apart from "was a ride, now idle" —
     * [lastMovedMs] alone cannot, because start/reconnect/resume all re-arm it to now. Persisted, like
     * [currentSessionGotFix], so a START_STICKY restart of a real ride can't read as a false start.
     */
    var currentSessionMoved: Boolean
        get() = prefs.getBoolean(KEY_CURRENT_MOVED, false)
        set(value) = prefs.edit { putBoolean(KEY_CURRENT_MOVED, value) }

    /** Rolling ~2-min average speed (km/h) persisted by the service so the cooldown alarm receiver can
     * read it in a fresh process. Written throttled (~1 write/30 s while fixes flow). */
    var recentSpeedKmh: Float
        get() = prefs.getFloat(KEY_RECENT_SPEED_KMH, 0f)
        set(value) = prefs.edit { putFloat(KEY_RECENT_SPEED_KMH, value) }

    // ── Auto-detect (always-on workout detection) ──────────────────────────

    /** Master switch for always-on auto-detection. */
    var autoDetectEnabled: Boolean
        get() = prefs.getBoolean(KEY_AUTO_DETECT_ENABLED, false)
        set(value) = prefs.edit { putBoolean(KEY_AUTO_DETECT_ENABLED, value) }

    /** Persisted state-machine state: survives process death between transitions. Stored by name;
     * an unrecognized stored value (downgrade across a rename) reads as IDLE, never throws. */
    var autoState: AutoState
        get() = AutoState.entries.firstOrNull { it.name == prefs.getString(KEY_AUTO_STATE, null) }
            ?: AutoState.IDLE
        set(value) = prefs.edit { putString(KEY_AUTO_STATE, value.name) }

    /** Visibility applied to auto-detected activities (private until reviewed on the web). */
    var autoDetectVisibility: String
        get() = prefs.getString(KEY_AUTO_VISIBILITY, Visibility.PRIVATE.wire) ?: Visibility.PRIVATE.wire
        set(value) = prefs.edit { putString(KEY_AUTO_VISIBILITY, value) }

    /** Stop signal (DetectedActivity STILL/IN_VEHICLE name) that armed the running cooldown. Persisted so
     * the alarm receiver can apply the vehicle-speed exception in a fresh process. */
    var cooldownSignal: String?
        get() = prefs.getString(KEY_COOLDOWN_SIGNAL, null)
        set(value) = prefs.edit { putString(KEY_COOLDOWN_SIGNAL, value) }

    /** True while an auto session sits in the post-stop grace window: paused server-side, kept open so a
     * Run/Ride within the window resumes the SAME session instead of splitting the activity. */
    var gracePause: Boolean
        get() = prefs.getBoolean(KEY_GRACE_PAUSE, false)
        set(value) = prefs.edit { putBoolean(KEY_GRACE_PAUSE, value) }

    /**
     * AND-07: consecutive [UploadWorker][com.routebase.gpsrecorder.work.UploadWorker] ticks that found
     * [isCapturing] true but [com.routebase.gpsrecorder.service.GpsRecordingService.isServiceActive] false
     * — a capture whose service died for good and will never restart on its own. Reset whenever the service
     * is confirmed alive or nothing is capturing; once it crosses the park threshold the worker parks the
     * capture into the upload queue instead of retrying forever.
     */
    var deadServiceTicks: Int
        get() = prefs.getInt(KEY_DEAD_SERVICE_TICKS, 0)
        set(value) = prefs.edit { putInt(KEY_DEAD_SERVICE_TICKS, value) }

    /** Wall-clock stamp of the last counted dead-service observation — the worker only counts ticks
     * spaced far enough apart (AND-04: Result.retry() re-runs on ~30 s backoff, not the 30-min cadence
     * the park threshold was sized for). */
    var lastDeadServiceTickMs: Long
        get() = prefs.getLong(KEY_LAST_DEAD_TICK_MS, 0L)
        set(value) = prefs.edit { putLong(KEY_LAST_DEAD_TICK_MS, value) }

    /** Wall-clock end of the grace window (diagnostic only — the finalize worker's delay is the timer). */
    var graceUntilMs: Long
        get() = prefs.getLong(KEY_GRACE_UNTIL_MS, 0L)
        set(value) = prefs.edit { putLong(KEY_GRACE_UNTIL_MS, value) }

    /**
     * AND-25: [gracePause] is flipped from two threads — the service's resume/stop path ([endGrace]) and
     * the finalize worker ([claimGraceStop]). Guarding both with one monitor makes "resume wins" vs
     * "finalize wins" a single atomic decision, so the finalize can never stop a ride a resume just revived.
     */
    private val graceLock = Any()

    /** Resume or a real stop ends the grace window. Atomic against [claimGraceStop]. */
    fun endGrace() = synchronized(graceLock) {
        gracePause = false
        graceUntilMs = 0L
    }

    /**
     * The finalize worker's atomic claim on the grace stop. Returns true only if the window was still open
     * (this call closes it — proceed to stop); false if a resume/stop already ended it (abort). Re-checking
     * a plain [gracePause] read here would let a finalize that raced a just-landed resume stop a live ride.
     */
    fun claimGraceStop(): Boolean = synchronized(graceLock) {
        if (gracePause) {
            gracePause = false
            graceUntilMs = 0L
            true
        } else {
            false
        }
    }

    /**
     * B1/AND-05: a stop was requested for the current recording and has not completed yet. The stop path
     * (drain + stop retries) can run for tens of seconds, and a process death inside it used to resurrect
     * the manually-stopped ride as ACTIVE on the next restart. Persisted so the restart resumes the STOP
     * path instead — see RestorePolicy. Written with commit, not apply: the flag must be on disk before
     * the drain starts, because the death it exists to survive happens during that drain.
     * Cleared with the rest of the recording state in [clearSession], and at every fresh session start.
     */
    var stopRequested: Boolean
        get() = prefs.getBoolean(KEY_STOP_REQUESTED, false)
        set(value) = prefs.edit(commit = true) { putBoolean(KEY_STOP_REQUESTED, value) }

    val isConfigured: Boolean
        get() = !serverUrl.isNullOrBlank() && !apiKey.isNullOrBlank()

    /** A recording is capturing right now — online or offline, session created or not. */
    override val isCapturing: Boolean
        get() = currentRecordingId != 0L

    /** The current recording has a server session (points can be appended). */
    override val isRecording: Boolean
        get() = !activeSessionId.isNullOrBlank()

    /** Capturing, but the server session does not exist yet (started offline) → the upload loop creates it. */
    val needsSessionCreation: Boolean
        get() = isCapturing && !isRecording

    /**
     * Clears the CURRENT recording. Whatever still had to reach the server must already have been parked
     * into the upload queue (RecordingRepository.parkCurrentRecordingAsync) — this drops the phone's only
     * pointer to it.
     */
    override fun clearSession() {
        prefs.edit {
            remove(KEY_CURRENT_RECORDING_ID)
            remove(KEY_CURRENT_PARAMS)
            remove(KEY_ACTIVE_SESSION_ID)
            remove(KEY_ACTIVE_SHARE_URL)
            remove(KEY_CLIENT_SEQUENCE)
            remove(KEY_RECORDING_START_MS)
            remove(KEY_SESSION_PAUSED)
            remove(KEY_PAUSED_BY_WEB)
            remove(KEY_CURRENT_ACTIVITY_TYPE)
            remove(KEY_CURRENT_AUTO_DETECTED)
            remove(KEY_CURRENT_GOT_FIX)
            remove(KEY_CURRENT_MOVED)
            remove(KEY_LAST_MOVED_MS)
            remove(KEY_RECENT_SPEED_KMH)
            remove(KEY_GRACE_PAUSE)
            remove(KEY_GRACE_UNTIL_MS)
            remove(KEY_STOP_REQUESTED)
            // AND-03: the dead-service observations belong to THIS capture. Leaking them across captures
            // let a few ordinary kill-during-grace incidents accumulate until a future live ride was
            // parked on the first worker tick after a kill.
            remove(KEY_DEAD_SERVICE_TICKS)
            remove(KEY_LAST_DEAD_TICK_MS)
            // Dropping the pointer to a recording is the only moment an orphan row can be created
            // (a straggling enqueuePoints landing after park/forget cleared prefs). Arm the sweep.
            putBoolean(KEY_ORPHAN_SWEEP_PENDING, true)
        }
    }

    /**
     * A recording pointer was dropped ([clearSession]) — the next launch must reap points a straggling
     * enqueuePoints may have left behind. Set by [clearSession]; cleared by the sweep in GpsRecorderApp.
     * Without it the sweep opened the DB and ran a write txn on the main thread at EVERY process start.
     */
    var orphanSweepPending: Boolean
        get() = prefs.getBoolean(KEY_ORPHAN_SWEEP_PENDING, false)
        set(value) = prefs.edit { putBoolean(KEY_ORPHAN_SWEEP_PENDING, value) }

    // ── Legacy (pre-v13) queue state ───────────────────────────────────────
    // Read once by LegacyQueueMigration, which adopts the recording these
    // described into the pending_recordings table, then clears them. Nothing else may read these.

    var queueMigrated: Boolean
        get() = prefs.getBoolean(KEY_QUEUE_MIGRATED, false)
        set(value) = prefs.edit { putBoolean(KEY_QUEUE_MIGRATED, value) }

    val legacyOrphanedSessionId: String?
        get() = prefs.getString(KEY_ORPHANED_SESSION_ID, null)

    val legacyOrphanedClientSequence: Int
        get() = prefs.getInt(KEY_ORPHANED_CLIENT_SEQUENCE, 0)

    val legacyDeferredParams: RecordingParams?
        get() = RecordingParamsCodec.decode(prefs.getString(KEY_DEFERRED_SESSION, null))

    fun clearLegacyQueueState() {
        prefs.edit {
            remove(KEY_ORPHANED_SESSION_ID)
            remove(KEY_ORPHANED_CLIENT_SEQUENCE)
            remove(KEY_DEFERRED_SESSION)
        }
    }

    fun clearCredentials() {
        prefs.edit {
            remove(KEY_API_KEY)
            remove(KEY_API_KEY_ENC)
        }
    }

    fun registerListener(listener: SharedPreferences.OnSharedPreferenceChangeListener) {
        prefs.registerOnSharedPreferenceChangeListener(listener)
    }

    fun unregisterListener(listener: SharedPreferences.OnSharedPreferenceChangeListener) {
        prefs.unregisterOnSharedPreferenceChangeListener(listener)
    }

    companion object {
        private const val KEY_SERVER_URL = "server_url"
        private const val KEY_API_KEY = "api_key"            // legacy plaintext (migrated → _enc)
        private const val KEY_API_KEY_ENC = "api_key_enc"    // Keystore-encrypted (SecureStore)
        private const val KEY_CURRENT_RECORDING_ID = "current_recording_id"
        private const val KEY_CURRENT_PARAMS = "current_recording_params"
        private const val KEY_QUEUE_MIGRATED = "queue_migrated_v13"
        private const val KEY_ACTIVE_SESSION_ID = "active_session_id"
        private const val KEY_ACTIVE_SHARE_URL = "active_share_url"
        // Public: high-frequency writes the UI listener filters out (see MainActivity.prefsListener).
        const val KEY_CLIENT_SEQUENCE = "client_sequence"
        const val KEY_RECORDING_START_MS = "recording_start_ms"
        // AU-08: written on every >50 m fix batch (~5 s while moving) — filtered too, else refresh() churns.
        const val KEY_LAST_MOVED_MS = "last_moved_ms"
        // Written ~every 30 s while fixes flow — must be filtered by the UI listener like the above.
        const val KEY_RECENT_SPEED_KMH = "recent_speed_kmh"
        private const val KEY_SESSION_PAUSED = "session_paused"
        private const val KEY_PAUSED_BY_WEB = "paused_by_web"
        private const val KEY_CURRENT_ACTIVITY_TYPE = "current_activity_type"
        private const val KEY_CURRENT_AUTO_DETECTED = "current_auto_detected"
        private const val KEY_CURRENT_GOT_FIX = "current_got_fix"
        private const val KEY_CURRENT_MOVED = "current_moved"
        // Pre-v13 queue slots — kept only so the one-time migration can adopt what they pointed at.
        private const val KEY_ORPHANED_SESSION_ID = "orphaned_session_id"
        private const val KEY_ORPHANED_CLIENT_SEQUENCE = "orphaned_client_sequence"
        private const val KEY_DEFERRED_SESSION = "deferred_session"
        private const val KEY_AUTO_DETECT_ENABLED = "auto_detect_enabled"
        private const val KEY_AUTO_STATE = "auto_state"
        private const val KEY_AUTO_VISIBILITY = "auto_visibility"
        private const val KEY_COOLDOWN_SIGNAL = "cooldown_signal"
        private const val KEY_GRACE_PAUSE = "grace_pause"
        private const val KEY_GRACE_UNTIL_MS = "grace_until_ms"
        private const val KEY_STOP_REQUESTED = "stop_requested"
        private const val KEY_DEAD_SERVICE_TICKS = "dead_service_ticks"
        private const val KEY_LAST_DEAD_TICK_MS = "last_dead_tick_ms"
        private const val KEY_ORPHAN_SWEEP_PENDING = "orphan_sweep_pending"
    }
}
