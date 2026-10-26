package com.routebase.gpsrecorder.data

import androidx.room.Entity
import androidx.room.PrimaryKey

/**
 * A server session the user discarded, whose abandon call failed (offline) at Discard time.
 *
 * [RecordingRepository.discardQueueAsync] drops the local queue rows unconditionally — that is the user's
 * intent, and it must not wait for a network. But if `api.abandonSession` itself fails, the session stays
 * 'recording'/'paused' server-side with nothing local pointing at it any more. Without this row, the next
 * ride's [SessionConflictPolicy] CLOSE_STALE path would find it via getActiveSessions and *stop* it — which
 * finalizes it into a real activity, resurrecting exactly what the user discarded. This row lets
 * [RecordingRepository.retryPendingAbandonsAsync] finish the abandon once a network returns.
 */
@Entity(tableName = "pending_abandons")
data class PendingAbandonEntity(
    @PrimaryKey val sessionId: String,
    val requestedAtMs: Long
)
