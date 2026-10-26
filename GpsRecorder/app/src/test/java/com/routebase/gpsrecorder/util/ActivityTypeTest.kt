package com.routebase.gpsrecorder.util

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import javax.xml.parsers.DocumentBuilderFactory

/**
 * Pins the phone edge of the activity-type triangle: C# ActivityTypeVocabulary ↔ strings.xml is
 * pinned by the server suite's ActivityTypeVocabularyTests; strings.xml ↔ [ActivityType.KNOWN] is
 * pinned here, IN ORDER (AU-09: the spinner sends the label, not the position). Plus the open-set
 * contract: an unknown wire value rides through verbatim — [ActivityType] must never fold it.
 */
class ActivityTypeTest {

    @Test
    fun `KNOWN matches strings-xml activity_types in order`() {
        val doc = DocumentBuilderFactory.newInstance().newDocumentBuilder().parse(stringsXml())
        val arrays = doc.getElementsByTagName("string-array")
        var items: List<String>? = null
        for (i in 0 until arrays.length) {
            val el = arrays.item(i) as org.w3c.dom.Element
            if (el.getAttribute("name") == "activity_types") {
                val nodes = el.getElementsByTagName("item")
                items = (0 until nodes.length).map { nodes.item(it).textContent }
            }
        }

        // Vacuity guard: a broken parse (renamed array, moved file) must not pass as parity.
        assertTrue(
            "activity_types string-array not found or too small (${items?.size ?: 0} items) — " +
                "fix the parse, do not weaken the test",
            items != null && items.size >= 7,
        )
        assertEquals("strings.xml activity_types vs ActivityType.KNOWN — do not reorder either " +
            "side independently (AU-09)", items, ActivityType.KNOWN.map { it.wire })
    }

    @Test
    fun `wire spellings are pinned`() {
        // A rename here is a wire change for every recording the phone starts.
        assertEquals(
            listOf("Run", "Ride", "Walk", "Hike", "AlpineSki", "NordicSki", "Other"),
            ActivityType.KNOWN.map { it.wire },
        )
    }

    @Test
    fun `isFoot classification table including unknown types`() {
        val cases = mapOf(
            ActivityType.RUN to true,
            ActivityType.WALK to true,
            ActivityType.HIKE to true,
            ActivityType.RIDE to false,
            ActivityType.ALPINE_SKI to false,
            ActivityType.NORDIC_SKI to false,
            ActivityType.OTHER to false,
            ActivityType("Skateboarding") to false,   // unknown reads as speed, never throws
        )
        for ((type, expected) in cases) {
            assertEquals("isFoot(${type.wire})", expected, type.isFoot)
        }
    }

    @Test
    fun `unknown wire value rides through verbatim`() {
        // The open-set contract (AU-09): no from() folding, no normalization, value semantics hold.
        assertEquals("Skateboarding", ActivityType("Skateboarding").wire)
        assertEquals(ActivityType.RUN, ActivityType("Run"))
        assertFalse(ActivityType("run") == ActivityType.RUN)   // case is part of the wire value
    }

    /** res/values/strings.xml resolved from the working dir upward (LayeringTest's pattern). */
    private fun stringsXml(): File {
        var dir = File("").absoluteFile
        while (!File(dir, "app/src/main/res/values/strings.xml").isFile) {
            dir = dir.parentFile ?: error("could not locate strings.xml from ${File("").absolutePath}")
        }
        return File(dir, "app/src/main/res/values/strings.xml")
    }
}
