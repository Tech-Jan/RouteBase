package com.routebase.gpsrecorder.util

/**
 * Typed name for the activity-type wire vocabulary. Open set, NOT an enum (AU-09): the server
 * accepts values outside the phone's picker (imports, future types), so an unknown wire string
 * rides through VERBATIM — deliberately no `from()` that folds unknowns into OTHER. Spinner,
 * prefs, intent extras, DB columns and DTO fields all stay `String`; construct at the point of use.
 *
 * [KNOWN]'s order IS the res/values `activity_types` contract (AU-09: the app sends the label,
 * not the position) — pinned by ActivityTypeTest. That closes the triangle's missing edge:
 * C# ActivityTypeVocabulary ↔ strings.xml is pinned by ActivityTypeVocabularyTests (server suite),
 * strings.xml ↔ these constants is pinned here.
 *
 * Separate file from WireEnums.kt on purpose: RecorderWireParityTests scrapes that file for
 * `enum class` blocks and must never see this value class.
 */
@JvmInline
value class ActivityType(val wire: String) {

    /** True where pace (min/km) reads better than speed (km/h). Unknown types read as speed. */
    val isFoot: Boolean
        get() = this == RUN || this == WALK || this == HIKE

    companion object {
        val RUN = ActivityType("Run")
        val RIDE = ActivityType("Ride")
        val WALK = ActivityType("Walk")
        val HIKE = ActivityType("Hike")
        val ALPINE_SKI = ActivityType("AlpineSki")
        val NORDIC_SKI = ActivityType("NordicSki")
        val OTHER = ActivityType("Other")

        /** The recordable picker set, ordered exactly as strings.xml `activity_types` (AU-09). */
        val KNOWN: List<ActivityType> = listOf(RUN, RIDE, WALK, HIKE, ALPINE_SKI, NORDIC_SKI, OTHER)
    }
}
