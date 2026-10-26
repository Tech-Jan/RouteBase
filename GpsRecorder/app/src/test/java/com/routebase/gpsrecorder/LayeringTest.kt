package com.routebase.gpsrecorder

import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * Android layering gate — the lower layers must not depend UP on the Android-framework layers.
 *
 * This exists because the violation it forbids was a real, months-long defect, not a style opinion.
 * `RecordingRepository` (the data layer) used to read `GpsRecordingService.isServiceActive`, a
 * `@Volatile` companion static on the service — so a data-layer class reached up into the service layer
 * through a process-global. Its practical cost: the class could not be unit-tested at all, because
 * `busyCapturing()` was unfakeable (the only way to make it false was to have no Android service
 * running, which a JVM test cannot arrange). A 2026-07-31 refactor inverted it to an injected predicate
 * and then extracted the queue drain on top of that seam; those two changes are what made the first
 * 9 queue-drain tests possible.
 *
 * The rule was prose in the project conventions ("dependency inversion — depend on abstractions") and was enforced
 * only by whoever happened to read it. Prose did not stop it the first time. This does.
 *
 * The layers, bottom-up:
 *   util/       leaf utilities + prefs (depends on nothing intra-app but itself)
 *   data/       repository, DAOs, api client, delivery (depends DOWN on util/ only)
 *   ---- the line this test defends ----
 *   service/, work/, autodetect/, and the root classes (GpsRecorderApp, MainActivity):
 *               Android framework components. They depend DOWN on data/ and util/, never the reverse.
 *
 * If you need something from an upper layer down here, INJECT it (a `() -> Boolean`, an interface) and
 * wire it in `GpsRecorderApp.rebuildApi()`. Do not add an import and do not add an exemption list —
 * this gate deliberately has none, because the moment it gets one it stops meaning anything.
 */
class LayeringTest {

    /** Package segments that sit ABOVE data/ and util/ and must never be imported from them. */
    private val forbiddenPackages = listOf("service", "work", "autodetect")

    /** Root-level classes that are Android entry points — also above the line. */
    private val forbiddenRootClasses = listOf("GpsRecorderApp", "MainActivity")

    @Test
    fun `data and util never import the service, work, autodetect or root layers`() {
        val root = sourceRoot()
        val lowerLayers = listOf("data", "util").map { File(root, "com/routebase/gpsrecorder/$it") }

        // Vacuity guard: a wrong root would make the whole test pass without inspecting anything.
        val files = lowerLayers.flatMap { dir ->
            dir.walkTopDown().filter { it.isFile && it.extension == "kt" }.toList()
        }
        assertTrue(
            "layering walk found ${files.size} files under data/ + util/ — the source root is wrong " +
                "(looked in ${lowerLayers.joinToString()}). Fix the walk, do not weaken the test.",
            files.size >= 8,
        )

        val violations = mutableListOf<String>()
        for (file in files) {
            file.forEachLine { line ->
                val trimmed = line.trimStart()
                if (!trimmed.startsWith("import ")) return@forEachLine
                val imported = trimmed.removePrefix("import ").trim()
                if (!imported.startsWith("com.routebase.gpsrecorder.")) return@forEachLine

                val rest = imported.removePrefix("com.routebase.gpsrecorder.")
                val segment = rest.substringBefore('.')
                val simpleName = rest.substringBefore('.')

                if (segment in forbiddenPackages) {
                    violations += "${file.name}: imports $imported (${segment}/ is above data/util)"
                } else if (!rest.contains('.') && simpleName in forbiddenRootClasses) {
                    violations += "${file.name}: imports $imported (an Android entry point)"
                }
            }
        }

        assertTrue(
            "LAYERING VIOLATION — a lower layer imports an upper one. Inject the dependency instead " +
                "(see this file's header for the RecordingRepository/isServiceActive precedent):\n" +
                violations.joinToString("\n"),
            violations.isEmpty(),
        )
    }

    /** `app/src/main/java`, resolved from this test's own location so it survives a module move. */
    private fun sourceRoot(): File {
        var dir = File("").absoluteFile
        while (!File(dir, "app/src/main/java").isDirectory) {
            dir = dir.parentFile ?: error("could not locate app/src/main/java from ${File("").absolutePath}")
        }
        return File(dir, "app/src/main/java")
    }
}
