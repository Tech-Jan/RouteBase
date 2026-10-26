package com.routebase.gpsrecorder.autodetect

import com.routebase.gpsrecorder.util.AutoState
import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * Pins [AutoDetectController.stateOnEnable] — the boot / app-update re-registration path.
 *
 * enable() is called by BootReceiver one line after a still-capturing recording has been restarted, and it
 * used to force IDLE unconditionally. The IDLE branch ignores STILL/IN_VEHICLE, so the cooldown never armed
 * again and the AR stop was dead for the rest of the ride — and the idle watchdog could not cover it,
 * because it fires on no GPS *displacement* and a drive home keeps the anchor advancing. The ride ran for
 * the whole car journey.
 */
class AutoStateOnEnableTest {

    @Test fun `a live auto recording resumes RECORDING so stop signals still land`() {
        // The reboot/app-update-mid-ride case. Without this the next IN_VEHICLE is ignored and the car ride
        // is recorded onto the end of the workout.
        assertEquals(
            AutoState.RECORDING,
            AutoDetectController.stateOnEnable(capturing = true, autoDetected = true, gracePaused = false)
        )
    }

    @Test fun `a COOLDOWN is not resumed — it comes back as RECORDING`() {
        // stateOnEnable takes no stored state on purpose: a cooldown's AlarmManager alarm does not survive a
        // reboot, so resuming COOLDOWN would wait for a wake-up that never fires. RECORDING lets the next
        // stop signal arm a fresh cooldown. (Encoded by the signature — this test documents why.)
        assertEquals(
            AutoState.RECORDING,
            AutoDetectController.stateOnEnable(capturing = true, autoDetected = true, gracePaused = false)
        )
    }

    @Test fun `a MANUAL recording stays IDLE — auto-detect must never stop a hand-started ride`() {
        // RECORDING here would let a STILL transition arm a cooldown, whose elapse calls finishRecording and
        // stops a ride the user started by hand. Toggling auto-detect on mid-ride must be inert.
        assertEquals(
            AutoState.IDLE,
            AutoDetectController.stateOnEnable(capturing = true, autoDetected = false, gracePaused = false)
        )
    }

    @Test fun `a grace-paused auto session stays IDLE — that IS the grace state`() {
        // The IDLE branch is what resumes a grace-paused session on the next Run/Ride. RECORDING would route
        // that transition into the no-op RECORDING branch and strand the session until the finalize worker.
        assertEquals(
            AutoState.IDLE,
            AutoDetectController.stateOnEnable(capturing = true, autoDetected = true, gracePaused = true)
        )
    }

    @Test fun `nothing recording is IDLE — the plain user toggle`() {
        assertEquals(
            AutoState.IDLE,
            AutoDetectController.stateOnEnable(capturing = false, autoDetected = false, gracePaused = false)
        )
    }

    @Test fun `stale autoDetected flag with nothing capturing is still IDLE`() {
        // currentAutoDetected is only meaningful while a recording exists; capturing is the gate.
        assertEquals(
            AutoState.IDLE,
            AutoDetectController.stateOnEnable(capturing = false, autoDetected = true, gracePaused = false)
        )
    }
}
