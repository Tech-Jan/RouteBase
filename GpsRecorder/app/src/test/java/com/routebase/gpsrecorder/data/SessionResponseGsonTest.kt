package com.routebase.gpsrecorder.data

import com.google.gson.GsonBuilder
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * Pins [SessionResponse] deserialization through the app's actual Gson config (plain GsonBuilder,
 * no Kotlin adapter — GpsRecorderApp). Every parameter is defaulted so Gson uses the no-arg
 * constructor: an absent key lands on the Kotlin default instead of Unsafe-instantiated NULL in a
 * non-null field — the 409-adopt NPE path this class used to have. A malformed payload now yields
 * a blank sessionId, which the adopt seams reject explicitly.
 */
class SessionResponseGsonTest {

    private val gson = GsonBuilder().create()

    @Test
    fun `empty object lands on safe defaults, not nulls in non-null fields`() {
        val r = gson.fromJson("""{}""", SessionResponse::class.java)
        assertEquals("", r.sessionId)   // blank, NOT null — .isBlank() is the adopt-seam contract
        assertEquals(0L, r.trackId)
        assertEquals("", r.status)
        assertEquals("", r.activityType)
        assertEquals("", r.visibility)
        assertEquals("", r.startedAt)
        assertNull(r.stoppedAt)
        assertNull(r.lastPointAt)
        assertEquals(0, r.pointCount)
        assertEquals(0, r.lastSequence)
    }

    @Test
    fun `full server payload maps field-for-field`() {
        val r = gson.fromJson(
            """{"sessionId":"s1","trackId":42,"status":"paused","activityType":"Ride",
               "visibility":"private","startedAt":"2026-08-01T10:00:00Z",
               "stoppedAt":"2026-08-01T11:00:00Z","lastPointAt":"2026-08-01T10:59:00Z",
               "pointCount":123,"lastSequence":7}""",
            SessionResponse::class.java,
        )
        assertEquals("s1", r.sessionId)
        assertEquals(42L, r.trackId)
        assertEquals("paused", r.status)
        assertEquals("Ride", r.activityType)
        assertEquals("private", r.visibility)
        assertEquals("2026-08-01T10:00:00Z", r.startedAt)
        assertEquals("2026-08-01T11:00:00Z", r.stoppedAt)
        assertEquals("2026-08-01T10:59:00Z", r.lastPointAt)
        assertEquals(123, r.pointCount)
        assertEquals(7, r.lastSequence)
    }

    @Test
    fun `partial payload keeps AQ-04 sequence resync intact`() {
        val r = gson.fromJson(
            """{"sessionId":"s2","trackId":1,"lastSequence":9}""",
            SessionResponse::class.java,
        )
        assertEquals(9, r.lastSequence)
        assertEquals("", r.status) // not "paused" → AQ-08 consumers treat it as recording
    }
}
