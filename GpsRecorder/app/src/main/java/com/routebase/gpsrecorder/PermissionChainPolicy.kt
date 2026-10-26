package com.routebase.gpsrecorder

/**
 * The stages of the pre-recording / pre-auto-detect permission chain, in the fixed order they are asked.
 * [SATISFIED] is the terminal — every prerequisite is granted (or predates the running API level, or was a
 * prompt-once stage already shown) — so the caller may proceed to its action.
 */
enum class PermissionStep {
    REQUEST_FINE_LOCATION,
    REQUEST_BACKGROUND_LOCATION,
    REQUEST_NOTIFICATIONS,
    PROMPT_BATTERY_OPTIMIZATION,
    REQUEST_ACTIVITY_RECOGNITION,
    SATISFIED,
}

/** Why the chain is being walked — decides whether ACTIVITY_RECOGNITION is part of it, and the terminal. */
enum class PermissionIntent {
    /** Manual "Start recording" tap: the chain ends at battery-opt (no activity recognition). */
    START_RECORDING,

    /** "Enable auto-detect": the same chain PLUS activity recognition, which auto-detect needs. */
    ENABLE_AUTO_DETECT,
}

/** A snapshot of the permission / battery-optimization grant state, read fresh from the OS per call. */
data class PermissionGrants(
    val fineLocationGranted: Boolean,
    val backgroundLocationGranted: Boolean,
    val notificationsGranted: Boolean,
    val activityRecognitionGranted: Boolean,
    val ignoringBatteryOptimizations: Boolean,
)

/**
 * The pure "which permission do we ask for next" decision, extracted verbatim from MainActivity's
 * permission chain (ARCH-AND-D, 2026-07-31). MainActivity keeps every ActivityResultLauncher,
 * checkSelfPermission and dialog; this object owns only the branching, so the SDK gates — otherwise
 * unverifiable on an emulator-free machine — become a unit-test table.
 *
 * The chain is walked FORWARD: [nextStep] takes the stage just completed ([after]; null = chain start) and
 * returns the first LATER stage that still needs doing, or [PermissionStep.SATISFIED]. Forward-only is
 * load-bearing: REQUEST_NOTIFICATIONS and PROMPT_BATTERY_OPTIMIZATION are "prompt once, then proceed
 * regardless of the outcome" stages — their MainActivity callbacks advance no matter what the user chose. A
 * grant-state-only function would re-ask them forever when the user declines; carrying [after] makes each
 * stage strictly one-shot. The function itself holds no state, so after Activity recreation the
 * re-delivered result calls back with the same [after] (a compile-time literal at each launcher) and the
 * same freshly-read grants, and gets the same answer (AU-04).
 *
 * SDK gates (encoded here, tested at each boundary): ACCESS_BACKGROUND_LOCATION and ACTIVITY_RECOGNITION
 * exist from API 29 (Q); POST_NOTIFICATIONS from API 33 (TIRAMISU). Below those levels the stage is skipped
 * — verbatim from the `Build.VERSION.SDK_INT >= …` guards MainActivity used inline.
 *
 * Pure — unit-tested without Android deps, like [com.routebase.gpsrecorder.service.RestorePolicy].
 */
object PermissionChainPolicy {

    const val API_BACKGROUND_LOCATION = 29    // Build.VERSION_CODES.Q — ACCESS_BACKGROUND_LOCATION
    const val API_ACTIVITY_RECOGNITION = 29   // Build.VERSION_CODES.Q — ACTIVITY_RECOGNITION
    const val API_NOTIFICATIONS = 33          // Build.VERSION_CODES.TIRAMISU — POST_NOTIFICATIONS

    /** The fixed asking order. SATISFIED is deliberately absent — it is the "nothing left to ask" answer. */
    private val ORDER = listOf(
        PermissionStep.REQUEST_FINE_LOCATION,
        PermissionStep.REQUEST_BACKGROUND_LOCATION,
        PermissionStep.REQUEST_NOTIFICATIONS,
        PermissionStep.PROMPT_BATTERY_OPTIMIZATION,
        PermissionStep.REQUEST_ACTIVITY_RECOGNITION,
    )

    fun nextStep(
        after: PermissionStep?,
        grants: PermissionGrants,
        sdkInt: Int,
        intent: PermissionIntent,
    ): PermissionStep {
        if (after == PermissionStep.SATISFIED) return PermissionStep.SATISFIED
        val startIndex = if (after == null) 0 else ORDER.indexOf(after) + 1
        for (i in startIndex until ORDER.size) {
            if (isNeeded(ORDER[i], grants, sdkInt, intent)) return ORDER[i]
        }
        return PermissionStep.SATISFIED
    }

    private fun isNeeded(
        step: PermissionStep,
        grants: PermissionGrants,
        sdkInt: Int,
        intent: PermissionIntent,
    ): Boolean = when (step) {
        PermissionStep.REQUEST_FINE_LOCATION ->
            !grants.fineLocationGranted
        PermissionStep.REQUEST_BACKGROUND_LOCATION ->
            sdkInt >= API_BACKGROUND_LOCATION && !grants.backgroundLocationGranted
        PermissionStep.REQUEST_NOTIFICATIONS ->
            sdkInt >= API_NOTIFICATIONS && !grants.notificationsGranted
        PermissionStep.PROMPT_BATTERY_OPTIMIZATION ->
            !grants.ignoringBatteryOptimizations
        PermissionStep.REQUEST_ACTIVITY_RECOGNITION ->
            intent == PermissionIntent.ENABLE_AUTO_DETECT &&
                sdkInt >= API_ACTIVITY_RECOGNITION && !grants.activityRecognitionGranted
        PermissionStep.SATISFIED -> false
    }
}
