package com.routebase.gpsrecorder.util

import org.junit.Assert.assertEquals
import org.junit.Test

class ElapsedFormatTest {

    @Test fun underAnHourIsMinuteSecond() {
        assertEquals("0:00", formatElapsed(0))
        assertEquals("0:05", formatElapsed(5_000))
        assertEquals("1:09", formatElapsed(69_000))
        assertEquals("59:59", formatElapsed(59 * 60_000 + 59_000))
    }

    @Test fun oneHourAndOverIsHourMinuteSecond() {
        assertEquals("1:00:00", formatElapsed(3_600_000))
        assertEquals("2:00:34", formatElapsed(2 * 3_600_000 + 34_000))
    }

    @Test fun negativeClamped() {
        assertEquals("0:00", formatElapsed(-5_000))
    }
}
