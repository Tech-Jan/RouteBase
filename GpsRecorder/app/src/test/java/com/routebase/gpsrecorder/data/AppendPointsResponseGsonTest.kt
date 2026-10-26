package com.routebase.gpsrecorder.data

import com.google.gson.GsonBuilder
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins the wire-absent defaults of [AppendPointsResponse] through the app's actual Gson config
 * (plain GsonBuilder, no Kotlin adapter — GpsRecorderApp). Gson only runs Kotlin parameter
 * defaults when the class has a no-arg constructor, i.e. when EVERY parameter is defaulted;
 * otherwise it instantiates via Unsafe and absent fields silently land on JVM defaults — which
 * inverted `watched` to false, the exact "silently go idle" direction its fail-safe default
 * exists to prevent (a wrong `true` costs battery; a wrong `false` breaks the live view).
 */
class AppendPointsResponseGsonTest {

    private val gson = GsonBuilder().create()

    @Test
    fun `older server omitting watched keeps the fail-safe true, not JVM false`() {
        val r = gson.fromJson("""{"inserted":3}""", AppendPointsResponse::class.java)
        assertEquals(3, r.inserted)
        assertEquals(0, r.lastSequence)
        assertTrue("watched must default TRUE (fail-safe: keep the fast cadence)", r.watched)
    }

    @Test
    fun `present fields override the defaults`() {
        val r = gson.fromJson(
            """{"inserted":0,"lastSequence":7,"watched":false}""",
            AppendPointsResponse::class.java,
        )
        assertEquals(0, r.inserted)
        assertEquals(7, r.lastSequence)
        assertEquals(false, r.watched)
    }
}
