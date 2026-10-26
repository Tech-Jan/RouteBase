package com.routebase.gpsrecorder.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Intent
import com.routebase.gpsrecorder.MainActivity
import com.routebase.gpsrecorder.R
import com.routebase.gpsrecorder.util.PrefsManager
import com.routebase.gpsrecorder.util.formatDistance
import com.routebase.gpsrecorder.util.formatElapsed
import com.routebase.gpsrecorder.util.formatPace
import com.routebase.gpsrecorder.util.formatSpeed
import com.routebase.gpsrecorder.util.isFootActivity
import com.routebase.gpsrecorder.util.joinStatus

/**
 * Builds and posts the ongoing-recording notification. Split out of [GpsRecordingService] (file cap) along
 * the seam that was already there: the service owns capture, this owns what the user sees.
 *
 * GR-07: the four PendingIntents are built ONCE per service instance. Each getActivity/getService is a
 * binder round-trip into ActivityManagerService, and the notification is rebuilt ~every 5 s for the whole
 * ride (once per coalesced fix batch AND once per upload tick) — that was 3 needless IPCs × ~24 rebuilds/min,
 * for hours, on the foreground service's main thread. They are constant per instance; only the text changes.
 */
internal class RecordingNotifier(private val service: Service, private val prefs: PrefsManager) {

    private val manager = service.getSystemService(NotificationManager::class.java)

    private val openIntent: PendingIntent = PendingIntent.getActivity(
        service, REQ_OPEN,
        Intent(service, MainActivity::class.java),
        PendingIntent.FLAG_IMMUTABLE
    )
    private val stopIntent: PendingIntent = serviceAction(REQ_STOP, GpsRecordingService.ACTION_STOP)
    private val pauseIntent: PendingIntent = serviceAction(REQ_PAUSE, GpsRecordingService.ACTION_PAUSE)
    private val resumeIntent: PendingIntent = serviceAction(REQ_RESUME, GpsRecordingService.ACTION_RESUME)

    private fun serviceAction(requestCode: Int, action: String): PendingIntent =
        PendingIntent.getService(
            service, requestCode,
            Intent(service, GpsRecordingService::class.java).apply { this.action = action },
            PendingIntent.FLAG_IMMUTABLE
        )

    fun createChannel() {
        val channel = NotificationChannel(
            CHANNEL_ID,
            service.getString(R.string.notification_channel_name),
            NotificationManager.IMPORTANCE_LOW
        ).apply {
            description = service.getString(R.string.notification_channel_desc)
            setShowBadge(false)
        }
        manager.createNotificationChannel(channel)
    }

    /** Re-post the ongoing notification with the current stats. [uploadDead] is AND-05's repeated-404
     * notice — non-null once the upload loop has given up on this session. */
    fun post(distanceMeters: Double, lastFixMs: Long, uploadDead: Boolean = false) =
        manager.notify(NOTIFICATION_ID, build(distanceMeters, lastFixMs, uploadDead))

    fun build(distanceMeters: Double, lastFixMs: Long, uploadDead: Boolean = false): Notification {
        val paused = prefs.sessionPaused
        val elapsed = if (prefs.recordingStartTimeMs > 0) {
            formatElapsed(System.currentTimeMillis() - prefs.recordingStartTimeMs)
        } else "0:00"

        val statusText = when {
            // Grace window: distinguish from a manual pause — this one self-finalizes.
            paused && prefs.gracePause -> service.getString(R.string.status_paused_auto)
            paused -> service.getString(R.string.status_paused)
            else -> {
                val elapsedMs = if (prefs.recordingStartTimeMs > 0)
                    System.currentTimeMillis() - prefs.recordingStartTimeMs else 0L
                val paceOrSpeed = if (isFootActivity(prefs.currentActivityType))
                    formatPace(distanceMeters, elapsedMs)
                else formatSpeed(distanceMeters, elapsedMs)
                val stats = joinStatus(formatDistance(distanceMeters), paceOrSpeed, "$elapsed elapsed")
                // The dead-session notice outranks the GPS-loss one — both explain a stalled track, but
                // only one tells the user what to actually do about it.
                val gapMs = if (lastFixMs > 0) System.currentTimeMillis() - lastFixMs else 0L
                when {
                    uploadDead -> joinStatus(service.getString(R.string.session_expired_notice), stats)
                    gapMs > GpsRecordingService.GPS_LOSS_NOTICE_MS ->
                        joinStatus(service.getString(R.string.gps_lost_fmt, (gapMs / 1000).toInt()), stats)
                    else -> stats
                }
            }
        }

        return Notification.Builder(service, CHANNEL_ID)
            .setContentTitle(
                if (paused) service.getString(R.string.notification_title_paused)
                else service.getString(R.string.notification_title)
            )
            .setContentText(statusText)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentIntent(openIntent)
            .setOngoing(true)
            .addAction(
                Notification.Action.Builder(
                    null,
                    if (paused) service.getString(R.string.resume) else service.getString(R.string.pause),
                    if (paused) resumeIntent else pauseIntent
                ).build()
            )
            .addAction(
                Notification.Action.Builder(null, service.getString(R.string.stop), stopIntent).build()
            )
            .build()
    }

    companion object {
        const val NOTIFICATION_ID = 1001
        private const val CHANNEL_ID = "gps_recording"
        private const val REQ_OPEN = 0
        private const val REQ_STOP = 1
        // Pause and Resume get DISTINCT request codes. The old code reused code 2 with FLAG_UPDATE_CURRENT
        // and flipped the action — PendingIntent identity already keys on (requestCode, action), so those
        // were two system slots anyway. Distinct codes let both be cached instead of rebuilt every refresh.
        private const val REQ_PAUSE = 2
        private const val REQ_RESUME = 3
    }
}
