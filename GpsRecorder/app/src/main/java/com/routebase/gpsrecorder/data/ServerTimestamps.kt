package com.routebase.gpsrecorder.data

import java.time.OffsetDateTime

/**
 * Parsing for the server's instant timestamps (e.g. [SessionResponse.startedAt]).
 *
 * NOT `Instant.parse`: that accepts only the trailing-`Z` form on pre-OpenJDK-12 Android runtimes,
 * and the server's format is not this phone's to control — since AR-02 the server standardised on
 * explicit-offset ISO (`+00:00`) and keeps `Z` on the recording DTO only as a courtesy converter.
 * [OffsetDateTime.parse] accepts `Z` and any `±hh:mm` on every runtime, so this app stays correct
 * even if that converter is ever lost.
 *
 * Null on anything unparseable — the caller decides what lenient means (see
 * `RecordingRepository.startedBefore`, where null deliberately keeps the old adopt behaviour).
 */
object ServerTimestamps {

    /** Epoch millis of an ISO-8601 instant with `Z` or explicit offset; null if unparseable. */
    fun parseEpochMs(iso: String): Long? =
        runCatching { OffsetDateTime.parse(iso).toInstant().toEpochMilli() }.getOrNull()
}
