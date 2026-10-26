package com.routebase.gpsrecorder.util

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import androidx.core.content.ContextCompat

/**
 * True while the app holds either location permission — the precondition for starting the location-type
 * FGS (API 34+ throws SecurityException in startForeground without it) and for FusedLocation updates.
 * ACTIVITY_RECOGNITION can outlive location ("Ask every time" downgrade, Settings revoke), so every
 * auto-start path must check this before touching the service (AND-20).
 */
fun hasLocationPermission(context: Context): Boolean =
    ContextCompat.checkSelfPermission(context, Manifest.permission.ACCESS_FINE_LOCATION) ==
        PackageManager.PERMISSION_GRANTED ||
        ContextCompat.checkSelfPermission(context, Manifest.permission.ACCESS_COARSE_LOCATION) ==
        PackageManager.PERMISSION_GRANTED
