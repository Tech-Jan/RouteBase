package com.routebase.gpsrecorder

import com.routebase.gpsrecorder.PermissionIntent.ENABLE_AUTO_DETECT
import com.routebase.gpsrecorder.PermissionIntent.START_RECORDING
import com.routebase.gpsrecorder.PermissionStep.PROMPT_BATTERY_OPTIMIZATION
import com.routebase.gpsrecorder.PermissionStep.REQUEST_ACTIVITY_RECOGNITION
import com.routebase.gpsrecorder.PermissionStep.REQUEST_BACKGROUND_LOCATION
import com.routebase.gpsrecorder.PermissionStep.REQUEST_FINE_LOCATION
import com.routebase.gpsrecorder.PermissionStep.REQUEST_NOTIFICATIONS
import com.routebase.gpsrecorder.PermissionStep.SATISFIED
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Test

/**
 * Pins the permission-chain decision extracted from MainActivity (ARCH-AND-D). The SDK gates
 * (ACCESS_BACKGROUND_LOCATION + ACTIVITY_RECOGNITION at API 29, POST_NOTIFICATIONS at API 33) are
 * unverifiable on an emulator-free machine any other way; this is their test table. minSdk 26, compileSdk
 * 36, so the sweep runs 26..36. Every assertion mirrors a concrete MainActivity path.
 */
class PermissionChainPolicyTest {

    private fun next(
        after: PermissionStep? = null,
        fine: Boolean = false,
        bg: Boolean = false,
        notif: Boolean = false,
        actRec: Boolean = false,
        battery: Boolean = false,       // ignoringBatteryOptimizations
        sdk: Int = 36,
        intent: PermissionIntent = START_RECORDING,
    ) = PermissionChainPolicy.nextStep(
        after,
        PermissionGrants(
            fineLocationGranted = fine,
            backgroundLocationGranted = bg,
            notificationsGranted = notif,
            activityRecognitionGranted = actRec,
            ignoringBatteryOptimizations = battery,
        ),
        sdk,
        intent,
    )

    @Test fun `a fresh device walks fine, background, notifications, battery-opt, then satisfied (start)`() {
        // Nothing granted, battery not ignoring, newest API — every stage fires in order, ending at
        // SATISFIED: a manual recording start never asks for activity recognition.
        assertEquals(REQUEST_FINE_LOCATION, next())
        assertEquals(REQUEST_BACKGROUND_LOCATION, next(after = REQUEST_FINE_LOCATION, fine = true))
        assertEquals(REQUEST_NOTIFICATIONS, next(after = REQUEST_BACKGROUND_LOCATION, fine = true, bg = true))
        assertEquals(
            PROMPT_BATTERY_OPTIMIZATION,
            next(after = REQUEST_NOTIFICATIONS, fine = true, bg = true, notif = true),
        )
        assertEquals(
            SATISFIED,
            next(after = PROMPT_BATTERY_OPTIMIZATION, fine = true, bg = true, notif = true),
        )
    }

    @Test fun `enabling auto-detect walks the same chain plus activity recognition`() {
        assertEquals(REQUEST_FINE_LOCATION, next(intent = ENABLE_AUTO_DETECT))
        assertEquals(
            REQUEST_BACKGROUND_LOCATION,
            next(after = REQUEST_FINE_LOCATION, fine = true, intent = ENABLE_AUTO_DETECT),
        )
        assertEquals(
            REQUEST_NOTIFICATIONS,
            next(after = REQUEST_BACKGROUND_LOCATION, fine = true, bg = true, intent = ENABLE_AUTO_DETECT),
        )
        assertEquals(
            PROMPT_BATTERY_OPTIMIZATION,
            next(after = REQUEST_NOTIFICATIONS, fine = true, bg = true, notif = true, intent = ENABLE_AUTO_DETECT),
        )
        assertEquals(
            REQUEST_ACTIVITY_RECOGNITION,
            next(after = PROMPT_BATTERY_OPTIMIZATION, fine = true, bg = true, notif = true, intent = ENABLE_AUTO_DETECT),
        )
        assertEquals(
            SATISFIED,
            next(after = REQUEST_ACTIVITY_RECOGNITION, fine = true, bg = true, notif = true, actRec = true, intent = ENABLE_AUTO_DETECT),
        )
    }

    @Test fun `an already-fully-granted device goes straight to SATISFIED, both intents`() {
        // Must NOT re-ask anything that is already granted / ignored.
        for (intent in listOf(START_RECORDING, ENABLE_AUTO_DETECT)) {
            assertEquals(
                "intent $intent",
                SATISFIED,
                next(fine = true, bg = true, notif = true, actRec = true, battery = true, intent = intent),
            )
        }
    }

    @Test fun `SDK gates each stage at its introducing API level (26 to 36 sweep)`() {
        // ACCESS_BACKGROUND_LOCATION + ACTIVITY_RECOGNITION: API 29 (Q). POST_NOTIFICATIONS: API 33 (T).
        for (sdk in 26..36) {
            // Background: fine granted + battery ignoring, so bg is the only remaining candidate at 29+.
            val bg = next(after = REQUEST_FINE_LOCATION, fine = true, battery = true, sdk = sdk)
            if (sdk >= 29) assertEquals("bg at sdk $sdk", REQUEST_BACKGROUND_LOCATION, bg)
            else assertEquals("no bg at sdk $sdk", SATISFIED, bg)

            // Notifications: fine+bg granted, battery ignoring — notif is the only candidate at 33+.
            val notif = next(after = REQUEST_BACKGROUND_LOCATION, fine = true, bg = true, battery = true, sdk = sdk)
            if (sdk >= 33) assertEquals("notif at sdk $sdk", REQUEST_NOTIFICATIONS, notif)
            else assertEquals("no notif at sdk $sdk", SATISFIED, notif)

            // Activity recognition (auto-detect): everything else granted/ignored — AR is the candidate at 29+.
            val ar = next(
                after = PROMPT_BATTERY_OPTIMIZATION,
                fine = true, bg = true, notif = true, battery = true,
                intent = ENABLE_AUTO_DETECT, sdk = sdk,
            )
            if (sdk >= 29) assertEquals("ar at sdk $sdk", REQUEST_ACTIVITY_RECOGNITION, ar)
            else assertEquals("no ar at sdk $sdk", SATISFIED, ar)
        }
    }

    @Test fun `a required permission left ungranted keeps the chain on it — never SATISFIED`() {
        // MainActivity STOPS on a denial (dialog, no advance); the policy mirrors it by never skipping a
        // required, applicable, ungranted stage. Fine (all APIs), background + activity-recognition (29+).
        assertEquals(REQUEST_FINE_LOCATION, next(fine = false))
        assertEquals(REQUEST_FINE_LOCATION, next(fine = false, intent = ENABLE_AUTO_DETECT))
        assertEquals(REQUEST_BACKGROUND_LOCATION, next(fine = true, bg = false, sdk = 29))
        assertEquals(
            REQUEST_ACTIVITY_RECOGNITION,
            next(
                after = PROMPT_BATTERY_OPTIMIZATION,
                fine = true, bg = true, notif = true, battery = true, actRec = false,
                intent = ENABLE_AUTO_DETECT, sdk = 29,
            ),
        )
    }

    @Test fun `notifications and battery-opt are prompt-once — declining does not re-ask`() {
        // Their MainActivity callbacks advance regardless of the outcome, so once passed the policy must
        // never return them again — even with the permission still ungranted.
        assertNotEquals(
            REQUEST_NOTIFICATIONS,
            next(after = REQUEST_NOTIFICATIONS, fine = true, bg = true, notif = false),
        )
        assertNotEquals(
            PROMPT_BATTERY_OPTIMIZATION,
            next(after = PROMPT_BATTERY_OPTIMIZATION, fine = true, bg = true, notif = true, battery = false),
        )
        // Concretely: notifications declined → battery-opt next; battery-opt declined → manual chain done.
        assertEquals(
            PROMPT_BATTERY_OPTIMIZATION,
            next(after = REQUEST_NOTIFICATIONS, fine = true, bg = true, notif = false, battery = false),
        )
        assertEquals(
            SATISFIED,
            next(after = PROMPT_BATTERY_OPTIMIZATION, fine = true, bg = true, notif = false, battery = false),
        )
    }

    @Test fun `a manual recording start never asks for activity recognition, at any API level`() {
        val everyPosition = listOf<PermissionStep?>(
            null, REQUEST_FINE_LOCATION, REQUEST_BACKGROUND_LOCATION, REQUEST_NOTIFICATIONS, PROMPT_BATTERY_OPTIMIZATION,
        )
        for (sdk in 26..36) for (from in everyPosition) {
            assertNotEquals(
                "sdk $sdk from $from",
                REQUEST_ACTIVITY_RECOGNITION,
                next(after = from, sdk = sdk, intent = START_RECORDING),
            )
        }
    }

    @Test fun `nextStep is a pure function — recreation asks the same question and gets the same answer`() {
        // AU-04: after Activity recreation the re-delivered result calls back with the same `after` literal
        // and freshly-read grants. The policy holds no state, so two identical calls agree.
        val args = { next(after = REQUEST_BACKGROUND_LOCATION, fine = true, bg = true, sdk = 30, intent = ENABLE_AUTO_DETECT) }
        assertEquals(args(), args())
    }
}
