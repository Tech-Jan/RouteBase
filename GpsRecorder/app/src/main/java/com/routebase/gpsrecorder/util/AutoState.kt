package com.routebase.gpsrecorder.util

/**
 * Auto-detection state-machine phases. Persisted (by name) in [PrefsManager.autoState] so they
 * survive process death. Lives in util/ so PrefsManager can expose the TYPED property without
 * importing up into autodetect/ (LayeringTest).
 */
enum class AutoState { IDLE, RECORDING, COOLDOWN }
