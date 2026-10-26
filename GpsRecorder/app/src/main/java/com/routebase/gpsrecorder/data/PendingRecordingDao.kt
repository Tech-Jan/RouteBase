package com.routebase.gpsrecorder.data

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query

@Dao
interface PendingRecordingDao {

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsert(recording: PendingRecordingEntity)

    /**
     * Parked recordings that already own a server session. Drained FIRST: stopping them frees the
     * server's one-active-session-per-user slot, so the sessionless rows below can then be created
     * without a 409.
     */
    @Query("SELECT * FROM pending_recordings WHERE sessionId IS NOT NULL ORDER BY recordingId ASC")
    suspend fun getWithSession(): List<PendingRecordingEntity>

    /** Parked recordings still needing a server session (ran entirely offline). Created one at a time. */
    @Query("SELECT * FROM pending_recordings WHERE sessionId IS NULL ORDER BY recordingId ASC")
    suspend fun getWithoutSession(): List<PendingRecordingEntity>

    /** Does a queued recording own this server session? Guards the 409 path from adopting one. */
    @Query("SELECT * FROM pending_recordings WHERE sessionId = :sessionId LIMIT 1")
    suspend fun findBySessionId(sessionId: String): PendingRecordingEntity?

    @Query("UPDATE pending_recordings SET sessionId = :sessionId WHERE recordingId = :recordingId")
    suspend fun setSessionId(recordingId: Long, sessionId: String)

    /**
     * Forgets a session that will no longer take this recording's points (stopped or swept away), so the
     * next flush pass creates a fresh one for what's left. The sequence resets with it — a new session has
     * accepted nothing, and reusing the old count would have every batch dedup-eaten and then deleted.
     */
    @Query("UPDATE pending_recordings SET sessionId = NULL, clientSequence = 0 WHERE recordingId = :recordingId")
    suspend fun detachSession(recordingId: Long)

    /** Persisted per accepted batch so a crash mid-drain resumes at the right sequence (server dedups <=). */
    @Query("UPDATE pending_recordings SET clientSequence = :seq WHERE recordingId = :recordingId")
    suspend fun setClientSequence(recordingId: Long, seq: Int)

    @Query("DELETE FROM pending_recordings WHERE recordingId = :recordingId")
    suspend fun delete(recordingId: Long)

    @Query("SELECT COUNT(*) FROM pending_recordings")
    suspend fun count(): Int

    /** Highest queued id — a new recording must sort above it even if the device clock jumped backwards. */
    @Query("SELECT MAX(recordingId) FROM pending_recordings")
    suspend fun maxRecordingId(): Long?

    @Query("DELETE FROM pending_recordings")
    suspend fun deleteAll()
}
