package com.routebase.gpsrecorder.util

/**
 * Everything needed to create this recording's session on the server. Persisted at start (not only when
 * the start fails), because a recording that ends before its session exists — offline the whole way —
 * still has to be created later, from the queue, with the ORIGINAL type/visibility/timezone rather than
 * whatever the phone happens to be set to when the network returns.
 */
data class RecordingParams(
    val activityType: String,
    val visibility: String,
    val clientVersion: String,
    val tzOffset: Int,
    val autoDetected: Boolean,
    // AND-24: the user asked to publish a public live-share for this ride. Carried through the offline
    // session-creation queue so a ride started offline still mints its /s/{token} once online. Defaults
    // false so a legacy (pre-shareLive) encoded row decodes as "not shared".
    val shareLive: Boolean = false
)

/**
 * Pipe-joined codec for storing [RecordingParams] in SharedPreferences.
 *
 * Fields are fixed wire values (activity type, visibility, dotted version), so none can realistically
 * contain the separator — but a `|` slipping into one used to shift every later field silently (a
 * mis-parsed tz offset produces wrong local timestamps on the whole track). [encode] rejects the
 * character outright and [decode] returns null on any malformed input; the caller then treats the
 * recording as unrestorable rather than uploading it with corrupted metadata.
 */
object RecordingParamsCodec {

    private const val SEP = "|"
    private const val FIELDS = 6
    // Pre-shareLive rows had 5 fields. Still decoded (as shareLive=false) so an app upgrade mid-recording,
    // or an already-queued offline row, restores rather than being dropped as unrestorable.
    private const val LEGACY_FIELDS = 5

    fun encode(p: RecordingParams): String? {
        val parts = listOf(p.activityType, p.visibility, p.clientVersion)
        if (parts.any { it.contains(SEP) }) return null
        return "${p.activityType}$SEP${p.visibility}$SEP${p.clientVersion}$SEP${p.tzOffset}$SEP${p.autoDetected}$SEP${p.shareLive}"
    }

    fun decode(raw: String?): RecordingParams? {
        if (raw.isNullOrEmpty()) return null
        val parts = raw.split(SEP)
        if (parts.size != FIELDS && parts.size != LEGACY_FIELDS) return null
        val tz = parts[3].toIntOrNull() ?: return null
        val auto = parts[4].toBooleanStrictOrNull() ?: return null
        val shareLive = if (parts.size == FIELDS) parts[5].toBooleanStrictOrNull() ?: return null else false
        if (parts[0].isEmpty() || parts[1].isEmpty()) return null
        return RecordingParams(parts[0], parts[1], parts[2], tz, auto, shareLive)
    }
}
