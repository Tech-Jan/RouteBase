package com.routebase.gpsrecorder.util

/**
 * Client-side names for the server's closed wire vocabularies. Wire DTO fields deliberately STAY
 * `String` — a Gson enum field deserializes an unknown value to NULL in a non-null slot, which is
 * exactly the Unsafe-instantiation bug SessionResponse just fixed (C1). Convert at the point of
 * use via the lenient factories; an unknown value never throws, so a newer server can add states
 * without breaking older phones (the AU-09 open-set discipline).
 *
 * Lives in util/ (the bottom layer), not data/: PrefsManager needs [Visibility] for its default
 * and util must not import data (LayeringTest). Mirrors the server const classes
 * RecordingSessionStatus.cs and Visibilities.cs — RecorderWireParityTests (server test suite)
 * scrapes this file and fails on any drift, both directions.
 */
enum class SessionStatus(val wire: String) {
    RECORDING("recording"),
    PAUSED("paused"),
    STOPPED("stopped"),
    FINALIZED("finalized"),
    FINALIZE_FAILED("finalize_failed"),
    ABANDONED("abandoned"),

    /** Anything this build does not know — treated as "present but not paused" by consumers. */
    UNKNOWN("");

    companion object {
        fun from(wire: String?): SessionStatus =
            entries.firstOrNull { it != UNKNOWN && it.wire == wire } ?: UNKNOWN
    }
}

enum class Visibility(val wire: String) {
    PRIVATE("private"),
    FRIENDS("friends"),
    PUBLIC("public");

    companion object {
        /**
         * Fail-closed: anything unrecognized (null spinner item, localized label, future value)
         * becomes PRIVATE — the app must never invent a wire value or widen exposure by accident.
         * Case-insensitive because the spinner labels are the capitalized server values (AU-09).
         */
        fun fromOrPrivate(wire: String?): Visibility =
            entries.firstOrNull { it.wire.equals(wire, ignoreCase = true) } ?: PRIVATE
    }
}
