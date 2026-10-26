package com.routebase.gpsrecorder.util

/**
 * Formats an elapsed duration as `m:ss`, switching to `h:mm:ss` once it reaches an hour so a 2 h
 * session reads `2:00:34`, not `120:34` (AQ-11 notification / AU-10.6 in-app status). Pure — no Android
 * deps — so it is shared by both display sites and JVM-unit-testable. Semantics unchanged: this is
 * session age (wall-clock since start), not moving time.
 */
fun formatElapsed(ms: Long): String {
    val totalSeconds = (ms / 1000).coerceAtLeast(0)
    val h = totalSeconds / 3600
    val m = (totalSeconds % 3600) / 60
    val s = totalSeconds % 60
    return if (h > 0) "%d:%02d:%02d".format(h, m, s) else "%d:%02d".format(m, s)
}
