package com.routebase.gpsrecorder.data

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.Query

/**
 * Every read and delete is scoped by `recordingId` — the queue is shared across recordings, and an
 * unscoped drain uploads the previous recording's tail into the current session (see [GpsPointEntity]).
 * There is deliberately no "get the oldest N rows" query.
 */
@Dao
interface GpsPointDao {

    @Insert
    suspend fun insertAll(points: List<GpsPointEntity>)

    @Query("SELECT * FROM pending_points WHERE recordingId = :recordingId ORDER BY id ASC LIMIT :limit")
    suspend fun getPendingFor(recordingId: Long, limit: Int = 100): List<GpsPointEntity>

    /** Reloads an in-flight batch by its exact row ids (AND-02: frozen retry composition). */
    @Query("SELECT * FROM pending_points WHERE id IN (:ids) ORDER BY id ASC")
    suspend fun getByIds(ids: List<Long>): List<GpsPointEntity>

    @Query("SELECT COUNT(*) FROM pending_points WHERE recordingId = :recordingId")
    suspend fun countFor(recordingId: Long): Int

    @Query("DELETE FROM pending_points WHERE id IN (:ids)")
    suspend fun deleteByIds(ids: List<Long>)

    @Query("DELETE FROM pending_points WHERE recordingId = :recordingId")
    suspend fun deleteFor(recordingId: Long)

    /**
     * Gives the pre-v13 rows (owner 0 by migration default) a real owner id, so that 0 means exactly one
     * thing everywhere afterwards: "no recording owns this". Without it, `currentRecordingId == 0` ("not
     * recording") and a legacy queue row would name the same rows.
     */
    @Query("UPDATE pending_points SET recordingId = :toId WHERE recordingId = :fromId")
    suspend fun reassignOwner(fromId: Long, toId: Long)

    /** Points belonging to parked (finished, undelivered) recordings — the UI's queue size. */
    @Query("""
        SELECT COUNT(*) FROM pending_points
        WHERE recordingId IN (SELECT recordingId FROM pending_recordings)
    """)
    suspend fun countQueued(): Int

    /** Drops the whole backlog's points, leaving a live recording's rows untouched (user "discard queue"). */
    @Query("""
        DELETE FROM pending_points
        WHERE recordingId IN (SELECT recordingId FROM pending_recordings)
    """)
    suspend fun deleteQueuedPoints()

    /**
     * Reaps points that belong to neither the live recording ([currentRecordingId]) nor any queued one:
     * rows a straggling enqueuePoints wrote right after parkCurrentRecordingLocked counted zero and cleared
     * prefs, so no queue row ever names them. countQueued/deleteQueuedPoints filter on pending_recordings,
     * so nothing else can see or delete them. Returns the number reaped.
     */
    @Query("""
        DELETE FROM pending_points
        WHERE recordingId != :currentRecordingId
          AND recordingId NOT IN (SELECT recordingId FROM pending_recordings)
    """)
    suspend fun deleteOrphans(currentRecordingId: Long): Int

    /**
     * AND-42: how many orphan rows [deleteOrphans] would reap. A read (no write lock) so the startup sweep
     * can skip the DELETE's transaction when there is nothing to reap — the common case, since the sweep is
     * armed on every recording-pointer drop but real orphans are rare. Same predicate as [deleteOrphans].
     */
    @Query("""
        SELECT COUNT(*) FROM pending_points
        WHERE recordingId != :currentRecordingId
          AND recordingId NOT IN (SELECT recordingId FROM pending_recordings)
    """)
    suspend fun countOrphans(currentRecordingId: Long): Int
}
