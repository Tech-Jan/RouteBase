package com.routebase.gpsrecorder.data

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query

@Dao
interface PendingAbandonDao {

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsert(entity: PendingAbandonEntity)

    @Query("SELECT * FROM pending_abandons ORDER BY requestedAtMs ASC")
    suspend fun getAll(): List<PendingAbandonEntity>

    @Query("DELETE FROM pending_abandons WHERE sessionId = :sessionId")
    suspend fun delete(sessionId: String)
}
