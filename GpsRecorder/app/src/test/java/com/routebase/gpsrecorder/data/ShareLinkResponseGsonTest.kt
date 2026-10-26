package com.routebase.gpsrecorder.data

import com.google.gson.GsonBuilder
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * Pins [ShareLinkResponse] deserialization through the app's actual Gson config. All parameters
 * defaulted for the same no-arg-constructor rule as [SessionResponseGsonTest]: an absent `url`
 * lands on blank (which the mint caller rejects and retries), never Unsafe-instantiated null.
 */
class ShareLinkResponseGsonTest {

    private val gson = GsonBuilder().create()

    @Test
    fun `empty object yields blank url, not null`() {
        val r = gson.fromJson("""{}""", ShareLinkResponse::class.java)
        assertEquals("", r.url)   // blank = failed mint at the caller; "" is never stored
        assertNull(r.expiresAt)
    }

    @Test
    fun `full payload maps both fields`() {
        val r = gson.fromJson(
            """{"url":"/s/abc123","expiresAt":"2026-08-02T10:00:00Z"}""",
            ShareLinkResponse::class.java,
        )
        assertEquals("/s/abc123", r.url)
        assertEquals("2026-08-02T10:00:00Z", r.expiresAt)
    }
}
