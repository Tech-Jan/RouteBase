package com.routebase.gpsrecorder.data

/**
 * Runs a block as one atomic database transaction. A one-method seam over Room's
 * [androidx.room.withTransaction] so [RecordingRepository] no longer depends on the concrete
 * [AppDatabase] just to make the Discard mutation atomic (AND-23) — and so that mutation's caller can be
 * unit-tested without a real Room database. Production wires it to `db.withTransaction` in GpsRecorderApp.
 */
interface Transactor {
    suspend fun <R> transaction(block: suspend () -> R): R
}
