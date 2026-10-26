package com.routebase.gpsrecorder

import android.Manifest
import android.annotation.SuppressLint
import android.content.Intent
import android.content.SharedPreferences
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.PowerManager
import android.provider.Settings
import android.view.View
import android.widget.ArrayAdapter
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.lifecycle.lifecycleScope
import com.routebase.gpsrecorder.autodetect.AutoDetectController
import com.routebase.gpsrecorder.data.CredentialVerdict
import com.routebase.gpsrecorder.data.CredentialVerifier
import com.routebase.gpsrecorder.databinding.ActivityMainBinding
import com.routebase.gpsrecorder.service.GpsRecordingService
import com.routebase.gpsrecorder.util.EventLog
import com.routebase.gpsrecorder.util.PrefsManager
import com.routebase.gpsrecorder.util.Visibility
import com.routebase.gpsrecorder.util.formatElapsed
import com.routebase.gpsrecorder.work.UploadWorker
import kotlinx.coroutines.Dispatchers
import android.widget.Toast
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

class MainActivity : AppCompatActivity() {

    private lateinit var binding: ActivityMainBinding

    private val app: GpsRecorderApp get() = application as GpsRecorderApp
    private val statusHandler = Handler(Looper.getMainLooper())
    private val statusTick = object : Runnable {
        override fun run() {
            updateRecordingStatus()
            statusHandler.postDelayed(this, 1000L)
        }
    }

    private val prefsListener = SharedPreferences.OnSharedPreferenceChangeListener { _, key ->
        // Skip high-frequency writes that don't change the visible UI — points/elapsed are driven
        // by statusTick — so refresh() doesn't run (and reset the timer) on every upload cycle.
        if (key == PrefsManager.KEY_CLIENT_SEQUENCE || key == PrefsManager.KEY_RECORDING_START_MS ||
            key == PrefsManager.KEY_LAST_MOVED_MS || key == PrefsManager.KEY_RECENT_SPEED_KMH
        ) {
            return@OnSharedPreferenceChangeListener
        }
        refresh()
    }

    // True while the permission chain is being walked to *enable auto-detect* (vs start a manual
    // recording) — decides the terminal action after battery-optimization is handled.
    private var enablingAutoDetect = false
    // Guards the auto-detect switch's listener while refresh() sets its checked state programmatically.
    // The share-live switch needs no such guard: onShareLiveToggled's early-returns absorb any
    // programmatic fire (already-shared → no-op; OFF → no-op).
    private var updatingAutoSwitch = false

    // ── Permission launchers ───────────────────────────────────────────────

    private val fineLocationLauncher = registerForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { granted ->
        when {
            granted -> driveChain(after = PermissionStep.REQUEST_FINE_LOCATION)
            // On Android 12+ the user can grant "Approximate" only — COARSE lands but FINE doesn't,
            // silently crippling PRIORITY_HIGH_ACCURACY. Point them at the Precise-location toggle.
            hasCoarseLocation() -> showPreciseLocationRequiredDialog()
            else -> showPermissionDeniedDialog("Fine location")
        }
    }

    private val backgroundLocationLauncher = registerForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { granted ->
        if (!granted) showPermissionDeniedDialog("Background location")
        else driveChain(after = PermissionStep.REQUEST_BACKGROUND_LOCATION)
    }

    // Notifications is "prompt once, then proceed" — advance regardless of the grant result (unchanged).
    private val notificationLauncher = registerForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { _ -> driveChain(after = PermissionStep.REQUEST_NOTIFICATIONS) }

    private val activityRecognitionLauncher = registerForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { granted ->
        if (granted) finishEnablingAutoDetect()
        else {
            resetAutoDetectSwitch()
            showPermissionDeniedDialog("Physical activity")
        }
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // AU-04: restore the mid-permission-chain flag across Activity recreation (rotation, split-screen,
        // process death while a system permission dialog is up) — otherwise the re-delivered ActivityResult
        // lands on a fresh instance with enablingAutoDetect=false and the chain ends in doStartRecording(),
        // starting a recording the user never asked for.
        enablingAutoDetect = savedInstanceState?.getBoolean(STATE_ENABLING_AUTO_DETECT) ?: false
        binding = ActivityMainBinding.inflate(layoutInflater)
        setContentView(binding.root)
        setupActivityTypeSpinner()
        setupClickListeners()
        refresh()
    }

    override fun onSaveInstanceState(outState: Bundle) {
        super.onSaveInstanceState(outState)
        outState.putBoolean(STATE_ENABLING_AUTO_DETECT, enablingAutoDetect)
    }

    override fun onResume() {
        super.onResume()
        app.prefs.registerListener(prefsListener)
        refresh()
        // Opening the app is a good moment to deliver the queue (the flush no-ops while capturing).
        lifecycleScope.launch {
            withContext(Dispatchers.IO) {
                if (app.prefs.isConfigured) {
                    // CE rethrown (AND-27 class): a swallowed cancellation would fall through to the
                    // worker enqueue below inside an already-cancelled scope.
                    try { app.repository.flushQueueAsync() }
                    catch (e: kotlinx.coroutines.CancellationException) { throw e }
                    catch (_: Exception) {}
                    // Still queued (offline)? Hand off to the network-constrained worker so it uploads in
                    // the background when connectivity returns, rather than only on the next app open.
                    if (app.repository.hasQueuedWork()) UploadWorker.enqueue(this@MainActivity)
                }
            }
            refreshQueueCard()
        }
    }

    override fun onPause() {
        super.onPause()
        statusHandler.removeCallbacks(statusTick)
        app.prefs.unregisterListener(prefsListener)
    }

    // ── UI state ───────────────────────────────────────────────────────────

    private fun refresh() {
        val configured = app.prefs.isConfigured
        binding.setupSection.visibility = if (configured) View.GONE else View.VISIBLE
        binding.recordingSection.visibility = if (configured) View.VISIBLE else View.GONE

        if (configured) {
            // Sync the auto-detect switch to persisted state without firing its listener.
            updatingAutoSwitch = true
            binding.switchAutoDetect.isChecked = app.prefs.autoDetectEnabled
            updatingAutoSwitch = false

            // "active" = a recording is capturing, whether or not its server session exists yet.
            // The upload queue deliberately does NOT gate any of this: a backlog of undelivered
            // recordings must never stop the user from recording the next ride.
            val active = app.prefs.isCapturing
            val offlineRecording = app.prefs.needsSessionCreation
            val paused = app.prefs.sessionPaused
            val visLabel = binding.spinnerVisibility.selectedItem.toString()

            if (active) {
                binding.btnStartStop.text = getString(R.string.stop)
                binding.btnStartStop.setBackgroundColor(ContextCompat.getColor(this, R.color.stop_red))
            } else {
                binding.btnStartStop.text = "${getString(R.string.start)} ($visLabel)"
                binding.btnStartStop.setBackgroundColor(ContextCompat.getColor(this, R.color.start_green))
            }
            binding.spinnerActivityType.isEnabled = !active
            binding.spinnerVisibility.isEnabled = !active
            // Enable-only: free before Start; can be flipped ON mid-ride; locks once chosen (no revoke API).
            val shareLiveChosen = active && app.prefs.currentRecordingParams?.shareLive == true
            binding.switchShareLive.isEnabled = !shareLiveChosen
            // Sync the CHECKED state for a shared ride too: a fresh activity (task killed, process death,
            // open mid-ride) otherwise rendered the switch off+disabled while the ride was publicly live —
            // the privacy indicator showing the safe value while the unsafe one was true. One-directional
            // (never unchecks): pre-start the switch is the user's pending choice, not ride state. The
            // programmatic fire is absorbed by onShareLiveToggled's already-shared early-return.
            if (shareLiveChosen) binding.switchShareLive.isChecked = true

            if (active) {
                binding.btnPauseResume.visibility = View.VISIBLE
                binding.btnPauseResume.text = if (paused) getString(R.string.resume) else getString(R.string.pause)
                binding.tvRecordingStatus.visibility = View.VISIBLE
                updateRecordingStatus()
                statusHandler.removeCallbacks(statusTick)
                if (!paused) statusHandler.postDelayed(statusTick, 1000L)
            } else {
                binding.btnPauseResume.visibility = View.GONE
                binding.tvRecordingStatus.visibility = View.GONE
                statusHandler.removeCallbacks(statusTick)
            }

            val sessionId = app.prefs.activeSessionId
            val host = app.prefs.serverUrl?.trimEnd('/')
            // AND-24: the chosen-at-start Share-live flag rides with the recording's params, so it survives
            // a process restart and an offline start (where activeShareUrl arrives later, once minted).
            val shareLive = app.prefs.currentRecordingParams?.shareLive == true
            val shareUrl = app.prefs.activeShareUrl
            when {
                active && shareLive && shareUrl != null -> {
                    binding.tvShareLink.text = "$host$shareUrl"
                    binding.tvShareCaption.text = getString(R.string.share_caption_public)
                    binding.shareSection.visibility = View.VISIBLE
                }
                active && shareLive -> {
                    // Chose to share, but the link isn't minted yet (just started, or still offline).
                    binding.tvShareLink.text = getString(R.string.waiting_for_link)
                    binding.tvShareCaption.text = getString(R.string.share_caption_public)
                    binding.shareSection.visibility = View.VISIBLE
                }
                active && sessionId != null -> {
                    // Not shared publicly: the internal LiveView link only the signed-in owner can open.
                    binding.tvShareLink.text = "$host/LiveView/$sessionId"
                    binding.tvShareCaption.text = getString(R.string.share_caption_private)
                    binding.shareSection.visibility = View.VISIBLE
                }
                offlineRecording -> {
                    binding.tvShareLink.text = getString(R.string.waiting_for_connection)
                    binding.tvShareCaption.text = getString(R.string.share_caption_private)
                    binding.shareSection.visibility = View.VISIBLE
                }
                else -> binding.shareSection.visibility = View.GONE
            }

            refreshQueueCard()

        } else {
            binding.etSetupCode.setText("")
            binding.manualFields.visibility = View.GONE
            binding.tvEnterManually.visibility = View.VISIBLE
        }
        binding.tvVersion.text = getString(R.string.version_fmt, BuildConfig.VERSION_NAME)
    }

    private fun updateRecordingStatus() {
        val startMs = app.prefs.recordingStartTimeMs
        val elapsed = if (startMs > 0) {
            formatElapsed(System.currentTimeMillis() - startMs)
        } else "0:00"
        val points = GpsRecordingService.currentPointCount
        binding.tvRecordingStatus.text = "$points points — $elapsed elapsed"
    }

    // ── Click handlers ─────────────────────────────────────────────────────

    private fun setupClickListeners() {
        binding.btnSave.setOnClickListener { onSaveTapped() }
        binding.btnStartStop.setOnClickListener { onStartStopTapped() }
        binding.btnPauseResume.setOnClickListener { onPauseResumeTapped() }
        binding.btnQueueUpload.setOnClickListener { onQueueUploadTapped() }
        binding.btnQueueDiscard.setOnClickListener { onQueueDiscardTapped() }
        binding.tvEnterManually.setOnClickListener {
            binding.manualFields.visibility = View.VISIBLE
            binding.tvEnterManually.visibility = View.GONE
        }
        binding.btnCopyLink.setOnClickListener {
            val link = binding.tvShareLink.text.toString()
            val clipboard = getSystemService(CLIPBOARD_SERVICE) as android.content.ClipboardManager
            clipboard.setPrimaryClip(android.content.ClipData.newPlainText("share link", link))
        }
        binding.btnResetConfig.setOnClickListener {
            // AU-05: resetting creds while a recording is live strands it — isConfigured flips false, the
            // UI shows setup (no Stop button), and the service keeps uploading against the stale Retrofit
            // until process death. Block it. A queued recording also needs the creds to be delivered, so
            // block that too (the queue card's Discard is the way out).
            lifecycleScope.launch {
                val queued = withContext(Dispatchers.IO) { app.repository.hasQueuedWork() }
                when {
                    app.prefs.isCapturing || GpsRecordingService.isServiceActive ->
                        Toast.makeText(this@MainActivity, R.string.stop_recording_first, Toast.LENGTH_LONG).show()
                    queued ->
                        Toast.makeText(this@MainActivity, R.string.reset_pending_first, Toast.LENGTH_LONG).show()
                    else -> {
                        app.prefs.serverUrl = null
                        app.prefs.clearCredentials()
                        refresh()
                    }
                }
            }
        }
        binding.switchAutoDetect.setOnCheckedChangeListener { _, isChecked ->
            if (updatingAutoSwitch) return@setOnCheckedChangeListener
            onAutoDetectToggled(isChecked)
        }
        // No guard needed — onShareLiveToggled's early-returns no-op every spurious/programmatic fire.
        binding.switchShareLive.setOnCheckedChangeListener { _, isChecked -> onShareLiveToggled(isChecked) }
        // Hidden diagnostics: long-press the version to view/share/clear the on-device event log.
        binding.tvVersion.setOnLongClickListener { showEventLog(); true }
    }

    private fun showEventLog() {
        val log = EventLog.dump().ifBlank { getString(R.string.log_empty) }
        AlertDialog.Builder(this)
            .setTitle(R.string.event_log_title)
            .setMessage(log)
            .setPositiveButton(R.string.share) { _, _ ->
                startActivity(
                    Intent.createChooser(
                        Intent(Intent.ACTION_SEND).apply {
                            type = "text/plain"
                            putExtra(Intent.EXTRA_TEXT, log)
                        },
                        getString(R.string.share)
                    )
                )
            }
            .setNeutralButton(R.string.clear_log) { _, _ -> EventLog.clear() }
            .setNegativeButton(android.R.string.ok, null)
            .show()
    }

    private fun onSaveTapped() {
        val combined = binding.etSetupCode.text.toString().trim()
        val url: String
        val key: String

        if (combined.isNotBlank()) {
            val parsed = parseSetupCode(combined)
            if (parsed == null) {
                showSetupError(getString(R.string.error_invalid_setup_code))
                return
            }
            url = parsed.first
            key = parsed.second
        } else {
            // Manual fallback
            url = binding.etServerUrl.text.toString().trimEnd('/')
            key = binding.etApiKey.text.toString().trim()
            if (url.isBlank() || key.isBlank()) {
                showSetupError(getString(R.string.error_fields_required))
                return
            }
        }

        // Force-https (item 22): reject cleartext. Server is behind TLS; http is silently blocked on
        // API 28+ anyway and leaks the API key on 26/27. Covers both the setup-code and manual paths.
        if (!url.startsWith("https://")) {
            showSetupError(getString(R.string.error_https_required))
            return
        }

        binding.tvSetupError.visibility = View.GONE
        binding.btnSave.isEnabled = false

        // B2/AND-07: verify BEFORE persisting. The old order (save, rebuild the app-wide client, then
        // check) stored a typo'd key regardless of the check's outcome — and with queued rides behind
        // it, the only way back to setup was discarding the queue. A failed verify now leaves whatever
        // credentials were stored before untouched, and never involves the repository or the queue.
        lifecycleScope.launch {
            val verdict = CredentialVerifier.verify(url, key)
            binding.btnSave.isEnabled = true
            when (verdict) {
                CredentialVerdict.OK -> saveVerifiedCredentials(url, key)
                CredentialVerdict.BAD_KEY -> showSetupError(getString(R.string.error_invalid_api_key))
                CredentialVerdict.UNREACHABLE -> {
                    showSetupError(getString(R.string.error_connection_failed))
                    // The escape hatch for offline setup. Only here — the server never judged this key.
                    // A BAD_KEY verdict gets no hatch: saving a key the server just refused cannot help.
                    AlertDialog.Builder(this@MainActivity)
                        .setTitle(R.string.save_anyway_title)
                        .setMessage(R.string.save_anyway_message)
                        .setNegativeButton(R.string.cancel, null)
                        .setPositiveButton(R.string.save_anyway) { _, _ -> saveVerifiedCredentials(url, key) }
                        .show()
                }
            }
        }
    }

    /** The only place setup writes credentials: after an OK verify, or the user's explicit offline save. */
    private fun saveVerifiedCredentials(url: String, key: String) {
        app.prefs.serverUrl = url
        try {
            app.prefs.apiKey = key
        } catch (e: Exception) {
            // Real-world Keystore failures (corrupt store, vendor HAL glitch, key invalidated by a
            // security-state change) surface through SecureStore.encrypt — a setup error, not a crash.
            EventLog.log("Saving the API key failed: ${e.message}")
            showSetupError("Could not store the API key securely (device keystore error). Try again or reboot.")
            return
        }
        app.rebuildApi()
        binding.tvSetupError.visibility = View.GONE
        refresh()
    }

    private fun parseSetupCode(text: String): Pair<String, String>? {
        val idx = text.indexOf('|')
        if (idx <= 0) return null
        val url = text.substring(0, idx).trim().trimEnd('/')
        val key = text.substring(idx + 1).trim()
        if (!url.startsWith("http") || key.isBlank()) return null
        return Pair(url, key)
    }

    private fun showSetupError(message: String) {
        binding.tvSetupError.text = message
        binding.tvSetupError.visibility = View.VISIBLE
    }

    private fun onStartStopTapped() {
        // A pending upload queue never blocks a new recording — it is delivered in the background.
        if (app.prefs.isCapturing) {
            startService(GpsRecordingService.stopIntent(this))
        } else {
            driveChain(after = null)   // walk the permission chain, then start recording
        }
    }

    // ── Upload queue card ──────────────────────────────────────────────────

    /** Shows what is still waiting to reach the server. Hidden when the queue is empty. */
    private fun refreshQueueCard() {
        lifecycleScope.launch {
            val status = withContext(Dispatchers.IO) {
                runCatching { app.repository.queueStatusAsync() }.getOrNull()
            } ?: return@launch
            binding.queueSection.visibility = if (status.isEmpty) View.GONE else View.VISIBLE
            if (!status.isEmpty) {
                binding.tvQueueStatus.text =
                    getString(R.string.queue_status_fmt, status.recordings, status.points)
            }
        }
    }

    private fun onQueueUploadTapped() {
        lifecycleScope.launch {
            binding.btnQueueUpload.isEnabled = false
            binding.btnQueueUpload.text = getString(R.string.queue_uploading)
            val delivered = withContext(Dispatchers.IO) {
                runCatching { app.repository.flushQueueAsync() }
                !app.repository.hasQueuedWork()
            }
            binding.btnQueueUpload.isEnabled = true
            binding.btnQueueUpload.text = getString(R.string.queue_upload_now)
            if (!delivered) {
                // Couldn't finish (offline, or a recording is capturing) — the worker keeps trying.
                UploadWorker.enqueue(this@MainActivity)
                Toast.makeText(this@MainActivity, R.string.queue_upload_failed, Toast.LENGTH_LONG).show()
            }
            refreshQueueCard()
        }
    }

    private fun onQueueDiscardTapped() {
        lifecycleScope.launch {
            val status = withContext(Dispatchers.IO) {
                runCatching { app.repository.queueStatusAsync() }.getOrNull()
            } ?: return@launch
            if (status.isEmpty) { refreshQueueCard(); return@launch }
            // Irreversible and it destroys real GPS data — always confirm, and say exactly what is lost.
            AlertDialog.Builder(this@MainActivity)
                .setTitle(R.string.queue_discard_title)
                .setMessage(getString(R.string.queue_discard_message, status.recordings, status.points))
                .setNegativeButton(R.string.cancel, null)
                .setPositiveButton(R.string.queue_discard_confirm) { _, _ ->
                    lifecycleScope.launch {
                        // AND-12: an unhandled Room/IO failure here would crash the activity; the card
                        // refresh below shows what actually happened either way.
                        runCatching { withContext(Dispatchers.IO) { app.repository.discardQueueAsync() } }
                            .onFailure { android.util.Log.e("MainActivity", "discardQueueAsync failed", it) }
                        refreshQueueCard()
                    }
                }
                .show()
        }
    }

    private fun onPauseResumeTapped() {
        if (app.prefs.sessionPaused) {
            startService(GpsRecordingService.resumeIntent(this))
        } else {
            startService(GpsRecordingService.pauseIntent(this))
        }
    }

    private fun doStartRecording() {
        val activityType = binding.spinnerActivityType.selectedItem.toString()
        // AU-09: derive the wire value from the spinner label (which IS the server value, capitalized),
        // not the item position — a reorder or localization of the array must not silently remap it.
        // Validated fail-closed: a null selection or unrecognized label becomes PRIVATE — never a
        // "null"/localized string on the wire, and never accidentally wider exposure.
        val visibility = Visibility.fromOrPrivate(binding.spinnerVisibility.selectedItem?.toString()).wire
        val shareLive = binding.switchShareLive.isChecked
        startForegroundService(GpsRecordingService.startIntent(this, activityType, visibility, shareLive = shareLive))
    }

    // ── Permissions ────────────────────────────────────────────────────────
    // The "which permission comes next?" decision is [PermissionChainPolicy]; MainActivity keeps only the
    // plumbing (launchers, checkSelfPermission, dialogs). driveChain() performs the policy's next stage;
    // each launcher's grant callback calls back in to advance one stage.

    private val permissionIntent: PermissionIntent
        get() = if (enablingAutoDetect) PermissionIntent.ENABLE_AUTO_DETECT else PermissionIntent.START_RECORDING

    /** Grants read fresh from the OS — the same checks the inline chain made, in one snapshot. Each
     *  SDK-gated field short-circuits to `true` below its introducing API (the policy re-gates by SDK, so
     *  the value is only consulted at/above that level) — verbatim from the old inline `SDK < X ||` reads. */
    private fun permissionGrants() = PermissionGrants(
        fineLocationGranted = hasFineLocation(),
        backgroundLocationGranted = Build.VERSION.SDK_INT < Build.VERSION_CODES.Q ||
            isPermissionGranted(Manifest.permission.ACCESS_BACKGROUND_LOCATION),
        notificationsGranted = Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU ||
            isPermissionGranted(Manifest.permission.POST_NOTIFICATIONS),
        activityRecognitionGranted = Build.VERSION.SDK_INT < Build.VERSION_CODES.Q ||
            isPermissionGranted(Manifest.permission.ACTIVITY_RECOGNITION),
        ignoringBatteryOptimizations =
            (getSystemService(POWER_SERVICE) as PowerManager).isIgnoringBatteryOptimizations(packageName),
    )

    /** Ask the policy what comes after [after] (null = chain start) and perform that stage's plumbing. */
    @SuppressLint("InlinedApi") // SDK gate lives in PermissionChainPolicy — see permissionGrants().
    private fun driveChain(after: PermissionStep?) {
        when (PermissionChainPolicy.nextStep(after, permissionGrants(), Build.VERSION.SDK_INT, permissionIntent)) {
            PermissionStep.REQUEST_FINE_LOCATION ->
                fineLocationLauncher.launch(Manifest.permission.ACCESS_FINE_LOCATION)
            PermissionStep.REQUEST_BACKGROUND_LOCATION -> showBackgroundLocationRationale()
            PermissionStep.REQUEST_NOTIFICATIONS ->
                notificationLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
            PermissionStep.PROMPT_BATTERY_OPTIMIZATION -> promptBatteryOptimization()
            PermissionStep.REQUEST_ACTIVITY_RECOGNITION ->
                activityRecognitionLauncher.launch(Manifest.permission.ACTIVITY_RECOGNITION)
            // Terminal: every prerequisite is met — do the intent's action (reset lives in finishEnabling…).
            PermissionStep.SATISFIED ->
                if (enablingAutoDetect) finishEnablingAutoDetect() else doStartRecording()
        }
    }

    /** The background-location rationale dialog. Reached only when the policy decided it is needed. */
    @SuppressLint("InlinedApi") // SDK gate lives in PermissionChainPolicy — see permissionGrants().
    private fun showBackgroundLocationRationale() {
        AlertDialog.Builder(this)
            .setTitle(R.string.bg_location_title)
            .setMessage(R.string.bg_location_message)
            .setPositiveButton(R.string.grant) { _, _ ->
                backgroundLocationLauncher.launch(Manifest.permission.ACCESS_BACKGROUND_LOCATION)
            }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }

    /** Battery-opt is prompt-once: dismissing it (grant or not) advances the chain. Reached only when
     *  the policy decided it is needed (the app is not already exempt). */
    private fun promptBatteryOptimization() {
        AlertDialog.Builder(this)
            .setTitle(R.string.battery_opt_title)
            .setMessage(R.string.battery_opt_message)
            .setPositiveButton(R.string.open_settings) { _, _ ->
                startActivity(
                    Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS).apply {
                        data = Uri.parse("package:$packageName")
                    }
                )
            }
            .setNegativeButton(android.R.string.cancel, null)
            .setOnDismissListener { driveChain(after = PermissionStep.PROMPT_BATTERY_OPTIMIZATION) }
            .show()
    }

    // ── Auto-detect toggle ───────────────────────────────────────────────────

    private fun onAutoDetectToggled(enable: Boolean) {
        if (!enable) {
            AutoDetectController.disable(this)
            return
        }
        // Enabling needs the full background stack (location + notification + battery-opt) plus activity
        // recognition — the same permission chain, walked for the ENABLE_AUTO_DETECT intent (which the
        // policy uses to append the activity-recognition stage).
        enablingAutoDetect = true
        driveChain(after = null)
    }

    private fun finishEnablingAutoDetect() {
        // Terminal of the auto-detect chain — clear the intent flag here (was cleared inline mid-chain
        // before the policy extraction; it must stay set until now so the policy keeps asking for the
        // activity-recognition stage).
        enablingAutoDetect = false
        AutoDetectController.enable(this)
        Toast.makeText(this, R.string.auto_detect_enabled_toast, Toast.LENGTH_SHORT).show()
        refresh()
    }

    /** Flip the switch back off (without firing its listener) when enabling is cancelled/denied. */
    private fun resetAutoDetectSwitch() {
        enablingAutoDetect = false
        updatingAutoSwitch = true
        binding.switchAutoDetect.isChecked = false
        updatingAutoSwitch = false
    }

    // ── Share-live (AND-24: enable mid-ride) ───────────────────────────────
    //
    // Making a ride shareable mid-stream is outward-facing and — with no phone revoke — irreversible, so
    // confirm first. The three early-returns keep the handler idempotent against any spurious/programmatic
    // fire (rotation restore, the cancel-path re-toggle): it acts only on a genuine user ENABLE of a
    // not-yet-shared live ride, and does nothing before Start (pre-start toggling is read at Start, as today).
    private fun onShareLiveToggled(isChecked: Boolean) {
        if (!isChecked) return                          // enable-only: OFF taps (incl. the cancel re-toggle) no-op
        if (!app.prefs.isCapturing) return              // pre-start: unchanged — no dialog, choice read at Start
        val params = app.prefs.currentRecordingParams ?: return
        if (params.shareLive) return                    // already shared (also swallows a rotation-restore fire)
        AlertDialog.Builder(this)
            .setTitle(R.string.share_live_confirm_title)
            .setMessage(R.string.share_live_confirm_body)
            .setPositiveButton(R.string.share_live_confirm_yes) { _, _ -> enableShareLiveMidRide() }
            .setNegativeButton(android.R.string.cancel) { _, _ -> revertShareSwitch() }
            .setOnCancelListener { revertShareSwitch() }
            .show()
    }

    /** Flip the switch back off on cancel. Re-fires the listener with isChecked=false, which
     *  [onShareLiveToggled]'s first early-return no-ops — so no guard flag is required. */
    private fun revertShareSwitch() {
        binding.switchShareLive.isChecked = false
    }

    private fun enableShareLiveMidRide() {
        val params = app.prefs.currentRecordingParams ?: return
        app.prefs.currentRecordingParams = params.copy(shareLive = true)   // reuse existing setter (re-encodes)
        EventLog.log("Share-live enabled mid-recording")
        // Reuse the idempotent mint path so the link appears promptly instead of waiting for the next tick.
        lifecycleScope.launch { withContext(Dispatchers.IO) { runCatching { app.repository.ensureShareLinkAsync() } } }
        refresh()                                                          // lock switch + show "Preparing link…"
    }

    private fun isPermissionGranted(permission: String) =
        ContextCompat.checkSelfPermission(this, permission) == PackageManager.PERMISSION_GRANTED

    private fun hasFineLocation() = isPermissionGranted(Manifest.permission.ACCESS_FINE_LOCATION)

    private fun hasCoarseLocation() = isPermissionGranted(Manifest.permission.ACCESS_COARSE_LOCATION)

    /** Shown when the user granted Approximate-only location — GPS tracking needs Precise (FINE). */
    private fun showPreciseLocationRequiredDialog() {
        if (enablingAutoDetect) resetAutoDetectSwitch()
        AlertDialog.Builder(this)
            .setTitle(R.string.precise_location_title)
            .setMessage(R.string.precise_location_message)
            .setPositiveButton(R.string.open_settings) { _, _ ->
                startActivity(
                    Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS).apply {
                        data = Uri.parse("package:$packageName")
                    }
                )
            }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }

    private fun showPermissionDeniedDialog(permission: String) {
        // If we were mid-way through enabling auto-detect, undo the optimistic switch flip.
        if (enablingAutoDetect) resetAutoDetectSwitch()
        AlertDialog.Builder(this)
            .setTitle(getString(R.string.permission_denied_title))
            .setMessage(getString(R.string.permission_denied_message, permission))
            .setPositiveButton(android.R.string.ok, null)
            .show()
    }

    // ── Spinner setup ──────────────────────────────────────────────────────

    private fun setupActivityTypeSpinner() {
        val types = resources.getStringArray(R.array.activity_types)
        binding.spinnerActivityType.adapter =
            ArrayAdapter(this, android.R.layout.simple_spinner_dropdown_item, types)

        val visibilities = resources.getStringArray(R.array.visibility_options)
        binding.spinnerVisibility.adapter =
            ArrayAdapter(this, android.R.layout.simple_spinner_dropdown_item, visibilities)
        binding.spinnerVisibility.onItemSelectedListener = object : android.widget.AdapterView.OnItemSelectedListener {
            override fun onItemSelected(parent: android.widget.AdapterView<*>?, view: View?, pos: Int, id: Long) {
                if (!app.prefs.isCapturing) {
                    binding.btnStartStop.text = "${getString(R.string.start)} (${visibilities[pos]})"
                }
            }
            override fun onNothingSelected(parent: android.widget.AdapterView<*>?) {}
        }
    }

    companion object {
        // AU-04: survives Activity recreation mid-permission-chain (see onSaveInstanceState/onCreate).
        private const val STATE_ENABLING_AUTO_DETECT = "enabling_auto_detect"
    }
}
