package com.routebase.gpsrecorder.service

import com.routebase.gpsrecorder.util.BatterySnapshot
import java.util.Locale

/**
 * Per-ride power, thermal and cadence accumulator — the instrument behind "why does the phone get warm
 * on a ride". Pure: every clock and every reading is an injected parameter, so the whole thing is
 * table-testable without Android, like [WatchdogPolicy] / [UploadCadencePolicy].
 *
 * It exists because none of the candidate causes left a trace. On the arithmetic the upload cadence
 * cannot be the answer — it is worth ~40 mW against a 200-400 mW GNSS + never-sleeping-AP baseline —
 * but charging while riding, a screen left on in the sun, and a live-view tab left open (which pins the
 * 5 s cadence for the whole ride) all can be, and none of them was observable. This records the few
 * numbers that tell them apart, so the next change targets a measurement instead of a guess.
 *
 * Two rules keep the instrument from corrupting its own measurement: it adds NO wakeups (sampling rides
 * the watchdog loop's existing tick and a sticky broadcast) and it logs on edges only — a checkpoint per
 * [CHECKPOINT_MS] and one summary at the end, never per tick, because [com.routebase.gpsrecorder.util.EventLog]
 * is a 64 KB rotating file that a per-tick line would churn through in under an hour.
 *
 * Every entry point is [Synchronized] because the three callers are three different threads: the watchdog
 * loop ticks it, the upload loop reports to it, and the main looper asks for the summary from stopRecording
 * and onDestroy. That last overlap is not theoretical — stopRecording summarises BEFORE cancelRecordingLoops,
 * so a live upload tick can be inside onUpload while summaryOnce walks [cadenceMs]. Unguarded that is a
 * ConcurrentModificationException thrown out of a teardown path. The lock is uncontended and taken at most
 * once every few seconds, so it costs nothing that matters here.
 */
class RideDiagnostics {

    private var startMs = 0L
    private var lastTickMs = 0L
    private var lastUploadMs = 0L
    private var lastCheckpointMs = 0L
    private var summaryEmitted = false
    /** True when the clocks were seeded by a tick rather than by [reset] — i.e. the process was killed
     *  mid-ride and this instance only ever saw the tail of it. Surfaced in the summary, because a
     *  drain rate over an unknown fraction of a ride is exactly the kind of number that misleads. */
    private var partial = false

    private var firstLevel = LEVEL_UNSET
    private var lastLevel = LEVEL_UNSET
    private var firstTempC = Float.NaN
    private var lastTempC = Float.NaN
    private var peakTempC = Float.NaN
    private var everCharged = false
    private var charging = false
    private var screenOn = false

    private var chargingMs = 0L
    private var screenOnMs = 0L
    /** Wall-clock spent at each upload interval, keyed by the interval itself. Insertion-ordered for a
     *  stable summary regardless of which cadence the ride happened to start on. */
    private val cadenceMs = LinkedHashMap<Long, Long>()
    private var lastCadenceMs = 0L

    private var uploadsOk = 0
    private var uploadsFailed = 0
    private var pointsUploaded = 0

    /** A fresh ride. Called only from the genuine-start path — a reconnect or resume continues the same
     *  ride, and must keep accumulating, exactly like the static `trackStats` it sits beside. */
    @Synchronized fun reset(nowMs: Long) {
        startMs = nowMs
        lastTickMs = nowMs
        lastUploadMs = nowMs
        lastCheckpointMs = nowMs
        summaryEmitted = false
        partial = false
        firstLevel = LEVEL_UNSET
        lastLevel = LEVEL_UNSET
        firstTempC = Float.NaN
        lastTempC = Float.NaN
        peakTempC = Float.NaN
        everCharged = false
        charging = false
        screenOn = false
        chargingMs = 0L
        screenOnMs = 0L
        cadenceMs.clear()
        lastCadenceMs = 0L
        uploadsOk = 0
        uploadsFailed = 0
        pointsUploaded = 0
    }

    /**
     * One watchdog tick. Folds in the power reading and returns a checkpoint line when one is due,
     * else null — so a ride that never stops cleanly still leaves a trail.
     *
     * Time is attributed to the state observed at the START of the interval (the state we just spent it
     * in), and clamped to [MAX_GAP_MS] so a suspended process cannot bank an hour of "screen on" from a
     * single late tick.
     */
    @Synchronized fun onTick(sample: BatterySnapshot?, nowMs: Long): String? {
        ensureStarted(nowMs)
        val elapsed = (nowMs - lastTickMs).coerceIn(0L, MAX_GAP_MS)
        if (charging) chargingMs += elapsed
        if (screenOn) screenOnMs += elapsed
        lastTickMs = nowMs

        if (sample != null) {
            if (firstLevel == LEVEL_UNSET) firstLevel = sample.levelPercent
            lastLevel = sample.levelPercent
            if (firstTempC.isNaN()) firstTempC = sample.temperatureC
            lastTempC = sample.temperatureC
            if (peakTempC.isNaN() || sample.temperatureC > peakTempC) peakTempC = sample.temperatureC
            charging = sample.charging
            screenOn = sample.screenOn
            if (sample.charging) everCharged = true
        }

        if (nowMs - lastCheckpointMs < CHECKPOINT_MS) return null
        lastCheckpointMs = nowMs
        return "+${formatDuration(nowMs - startMs)} batt ${levelText(lastLevel)} temp ${tempText(lastTempC)}" +
            " cadence ${formatInterval(lastCadenceMs)} chg ${if (charging) "yes" else "no"}" +
            " scr ${if (screenOn) "on" else "off"}"
    }

    /**
     * One upload tick's outcome. [intervalMs] is the interval that was in effect for the wait just
     * ended — the caller passes it BEFORE computing the next one, so the time lands on the cadence that
     * actually spent it.
     */
    @Synchronized fun onUpload(success: Boolean, points: Int, intervalMs: Long, nowMs: Long) {
        ensureStarted(nowMs)
        val elapsed = (nowMs - lastUploadMs).coerceIn(0L, MAX_GAP_MS)
        cadenceMs[intervalMs] = (cadenceMs[intervalMs] ?: 0L) + elapsed
        lastUploadMs = nowMs
        lastCadenceMs = intervalMs
        if (success) {
            uploadsOk++
            pointsUploaded += points
        } else {
            uploadsFailed++
        }
    }

    /**
     * Seeds the clocks for a ride this instance did not see the start of. The OS kills the service
     * mid-ride and START_STICKY brings it back; the reconnect path deliberately does NOT call [reset]
     * (that would claim a fresh ride), so without this the zero clocks would make the very first tick
     * look 55 years overdue for a checkpoint and log `+29257917m`. Seeding here measures the tail and
     * labels it, which beats both the garbage line and silence.
     */
    private fun ensureStarted(nowMs: Long) {
        if (startMs != 0L) return
        startMs = nowMs
        lastTickMs = nowMs
        lastUploadMs = nowMs
        lastCheckpointMs = nowMs
        partial = true
    }

    /**
     * The one line that answers the question. Latched: a clean stop and the OS-kill teardown both call
     * it, and only the first wins, so a normal ride is not summarised twice.
     */
    @Synchronized fun summaryOnce(nowMs: Long): String? {
        if (summaryEmitted || startMs == 0L) return null
        summaryEmitted = true
        val rideMs = (nowMs - startMs).coerceAtLeast(0L)
        return "Ride${if (partial) " (partial, restarted mid-ride)" else ""} ${formatDuration(rideMs)} · batt ${levelText(firstLevel)}→${levelText(lastLevel)}" +
            " (${drainText(rideMs)}) · temp ${tempText(firstTempC)}→${tempText(lastTempC)}" +
            " peak ${tempText(peakTempC)} · chg ${sharePercent(chargingMs, rideMs)}" +
            " · scr ${sharePercent(screenOnMs, rideMs)} · cadence ${cadenceText(rideMs)}" +
            " · uploads ok $uploadsOk fail $uploadsFailed · pts $pointsUploaded"
    }

    /** Battery drop per hour — the number the whole exercise is for. Unusable if the ride was ever on
     *  charge (the level can rise), so say so rather than report a negative or flattered rate. */
    private fun drainText(rideMs: Long): String = when {
        everCharged -> "n/a, charged"
        firstLevel == LEVEL_UNSET || lastLevel == LEVEL_UNSET -> "n/a"
        rideMs < MIN_RATE_MS -> "n/a, too short"
        else -> String.format(Locale.US, "%.1f%%/hr", (firstLevel - lastLevel) * 3_600_000.0 / rideMs)
    }

    private fun cadenceText(rideMs: Long): String =
        if (cadenceMs.isEmpty()) "n/a"
        else cadenceMs.entries.joinToString(" / ") { (interval, ms) ->
            "${formatInterval(interval)} ${sharePercent(ms, rideMs)}"
        }

    private fun sharePercent(partMs: Long, totalMs: Long): String =
        if (totalMs <= 0L) "n/a" else "${(partMs * 100.0 / totalMs).toInt()}%"

    private fun levelText(level: Int): String = if (level == LEVEL_UNSET) "n/a" else "$level%"

    private fun tempText(c: Float): String =
        if (c.isNaN()) "n/a" else String.format(Locale.US, "%.1fC", c)

    private companion object {
        const val CHECKPOINT_MS = 10 * 60_000L
        // A tick or upload gap longer than this means the process was suspended, not that the phone
        // genuinely sat in that state — clamp rather than bank it.
        const val MAX_GAP_MS = 2 * 60_000L
        // Below this a 1% granularity level reading extrapolates to a wild rate (1% over 30 s reads as
        // 120%/hr), so report nothing instead of nonsense.
        const val MIN_RATE_MS = 5 * 60_000L
        const val LEVEL_UNSET = -1
    }
}

/**
 * `5000` → `5s`, `300000` → `5m`. Shared by the checkpoint and the summary so both read alike.
 * 60 s stays `60s` rather than collapsing to `1m`: that is the name UPLOAD_INTERVAL_IDLE_MS goes by
 * everywhere else, and a log a reader has to translate is a log that gets misread.
 */
internal fun formatInterval(ms: Long): String = when {
    ms <= 0L -> "n/a"
    ms > 60_000L && ms % 60_000L == 0L -> "${ms / 60_000L}m"
    else -> "${ms / 1000L}s"
}

/** `6420000` → `1h47m`; under an hour → `47m`. */
internal fun formatDuration(ms: Long): String {
    val totalMinutes = ms / 60_000L
    val hours = totalMinutes / 60
    val minutes = totalMinutes % 60
    return if (hours > 0) "${hours}h${minutes}m" else "${minutes}m"
}
