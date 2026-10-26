package com.routebase.gpsrecorder.util

/**
 * The recording/upload-queue state [com.routebase.gpsrecorder.data.RecordingRepository] reads and writes.
 *
 * Extracted so the data layer depends on THIS abstraction rather than on [PrefsManager] — the concrete
 * SharedPreferences wrapper, which drags in an Android [android.content.Context] and can't be built in a
 * JVM unit test. With the storage hidden behind an interface (persistence ignorance), the queue drain and
 * the 409 conflict path can be driven by an in-memory fake. [PrefsManager] is the only production
 * implementation.
 *
 * Only the members the repository actually touches live here; everything else on [PrefsManager] (setup,
 * auto-detect, grace-window, listeners) stays off the recording contract.
 */
interface RecordingPrefs {
    var currentRecordingId: Long
    var currentRecordingParams: RecordingParams?
    var activeSessionId: String?
    var activeShareUrl: String?
    var clientSequence: Int
    var recordingStartTimeMs: Long
    var sessionPaused: Boolean
    var lastMovedMs: Long

    /** Read-only for the repository — only the capture/service side ever writes these. */
    val currentActivityType: String?
    val currentAutoDetected: Boolean
    val isCapturing: Boolean
    val isRecording: Boolean

    fun clearSession()
}
