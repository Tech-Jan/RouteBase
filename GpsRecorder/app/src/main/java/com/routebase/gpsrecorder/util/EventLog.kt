package com.routebase.gpsrecorder.util

import android.content.Context
import android.util.Log
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.concurrent.Executors

/**
 * On-device diagnostic log for field debugging (service kills, GPS gaps, upload failures) without a
 * cable. Appends timestamped lines to a single file capped at [MAX_BYTES] — when it grows past the cap
 * the oldest half is dropped. All disk work runs on one background thread so callers (service main
 * thread, receivers, workers) never block. Init once from [com.routebase.gpsrecorder.GpsRecorderApp].
 */
object EventLog {

    private const val TAG = "GpsEventLog"
    private const val MAX_BYTES = 64 * 1024
    private const val FILE_NAME = "event_log.txt"

    private val io = Executors.newSingleThreadExecutor()
    private val stamp = SimpleDateFormat("MM-dd HH:mm:ss", Locale.US)

    @Volatile private var file: File? = null

    fun init(context: Context) {
        file = File(context.applicationContext.filesDir, FILE_NAME)
    }

    /** Fire-and-forget append. Safe from any thread; no-op if not yet initialised. */
    fun log(message: String) {
        val f = file ?: return
        // Capture the event time on the caller's thread, FORMAT on the log thread: SimpleDateFormat is not
        // thread-safe (it mutates an internal Calendar), and log() is called concurrently from the service
        // main thread, IO coroutines, the workers and the AR receiver. `io` is single-threaded, so all
        // formatting is serialized there — and a throw can no longer escape into onLocationResult.
        val at = Date()
        io.execute {
            try {
                f.appendText("${stamp.format(at)}  $message\n")
                if (f.length() > MAX_BYTES) trim(f)
            } catch (e: Exception) {
                Log.w(TAG, "log write failed: ${e.message}")
            }
        }
    }

    /** Returns the whole log for the in-app viewer (newest at the bottom). Runs on the caller thread. */
    fun dump(): String = try {
        file?.takeIf { it.exists() }?.readText().orEmpty()
    } catch (e: Exception) {
        "log read failed: ${e.message}"
    }

    fun clear() {
        val f = file ?: return
        io.execute { runCatching { f.writeText("") } }
    }

    /** Keeps the newest ~half when the cap is exceeded (line-aligned so we never cut mid-entry). */
    private fun trim(f: File) {
        val text = f.readText()
        val cut = text.indexOf('\n', text.length / 2)
        f.writeText(if (cut >= 0) text.substring(cut + 1) else "")
    }
}
