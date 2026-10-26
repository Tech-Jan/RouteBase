package com.routebase.gpsrecorder

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * Kotlin file-size ratchet — a RUNAWAY detector plus per-file growth pins. Pure filesystem walk, no
 * Android deps (mirrors the frontend's RouteBaseWeb/src/js/file-size-ratchet.test.ts — read that first,
 * it is the worked example, including the full rationale for the 2026-07-31 scope change).
 *
 * ⚠ SCOPE CHANGED 2026-07-31 (deliberate) — this is a RUNAWAY detector plus growth pins, NOT a
 * 700-line gate. Line count is a proxy for "too many responsibilities" and it mis-scored real work:
 * A 2026-07-31 refactor cut GpsRecordingService.kt 964 → 946 while adding the first tests to the upload path (big win,
 * metric barely moved). Splitting is a judgement call; the architectural rules with crisp definitions
 * were promoted to hard gates instead — see LayeringTest.kt. The ratchet:
 *   • FAILS if a listed file grows past its recorded cap — split it, or (only with a written reason) raise
 *     the number here in the same commit; the diff makes the decision visible to a reviewer.
 *   • FAILS if any UNLISTED .kt under app/src/main/ crosses 1000 — a runaway, not a judgement call.
 *   • when a listed file legitimately SHRINKS, lower its number here in the same commit — monotonic BY
 *     CONVENTION, deliberately not machine-enforced (a stale pin must not fail a green build).
 *
 * The three pins are NOT "the correct kind of large" (contrast grid-view.ts in the TS ratchet) — they are
 * known-oversized files whose growth we want stopped. Seeded and cut in 2026-07-31 refactors, and reconciled to
 * their true post-merge counts on 2026-07-31. Two of the three are now UNDER 700 and stay pinned anyway,
 * so they cannot creep back up.
 */
private const val RUNAWAY_CAP = 1000   // review trigger is a judgement call; this is the runaway gate

/** relative path under src/main/java → recorded line count (`wc -l`, an upper bound). LOWER on shrink. */
private data class SizePin(val path: String, val maxLines: Int)

private val EXEMPTIONS = listOf(
    // known-oversized, actively being split — seeded and cut 2026-07-31. Reconciled
    // post-merge, once both lanes' actual deltas were visible:
    //
    //   GpsRecordingService.kt  964 → 946  extracted UploadCadencePolicy (the WATCH_DECAY hysteresis,
    //     the failure backoff, the AND-05 404 threshold) — the first tests on the upload path. Still well
    //     over cap and that is expected: this was a coverage extraction, not a size one. The under-cap
    //     split (onStartCommand dispatch / startRecording recovery) is a later, larger pass.
    //   RecordingRepository.kt  773 → 651  extracted QueueDrainer (+9 tests driving the drain
    //     directly). Now UNDER the 700 cap — kept pinned at the new count so it cannot creep back before
    //     the remaining lifecycle split lands. 651 → 658 (type-quality C1+C2, 2026-08-01): two blank-
    //     sessionId adopt-seam guards + why-comments (net of deleted dead activeTrackId writes) and the
    //     SessionStatus/Visibility wire-enum imports. GpsRecordingService 946 → 947 and MainActivity
    //     696 → 699 in the same slice: the Visibility import + the fail-closed spinner comment.
    //     Both +1 again (type-quality K7, 2026-08-01): the ActivityType import.
    //
    // GpsRecordingService 948 → 965 (ride diagnostics, 2026-08-20). WRITTEN REASON, per the raise rule
    // above: the recorder could not answer "why does the phone get warm on a ride" because it recorded
    // nothing about power, charge state or which upload cadence it actually ran at. The accumulator and
    // the battery read are two NEW files (RideDiagnostics.kt, util/BatterySnapshot.kt) precisely so the
    // service keeps only call sites — what landed here is 5 of them plus their why-comments: a static
    // field, a reset on genuine start, a sample on the existing watchdog tick, an outcome on the upload
    // tick, and the latched summary at stop/destroy. No logic. Headroom to RUNAWAY_CAP is now 35 lines,
    // so the onStartCommand/startRecording split noted above is the next change to this file, not more
    // call sites.
    SizePin("com/routebase/gpsrecorder/service/GpsRecordingService.kt", 965),
    SizePin("com/routebase/gpsrecorder/data/RecordingRepository.kt", 659),
    // Now UNDER 700 after the permission-policy extraction (was 701 → 696). Pinned at the new count —
    // like maplibre-helpers.ts in the TS ratchet — so it cannot creep back toward the cap before the
    // remaining credential / queue / view split lands.
    SizePin("com/routebase/gpsrecorder/MainActivity.kt", 699),
)

class KotlinFileSizeRatchetTest {

    @Test
    fun `no logic file runs away past 1000 lines, and no pinned file grows`() {
        val root = sourceRoot()
        val files = root.walkTopDown()
            .filter { it.isFile && it.extension == "kt" }
            .map { it.relativeTo(root).invariantSeparatorsPath }
            .toList()

        // Vacuity guard: a broken walk (wrong root, empty read) would make every check below pass silently.
        // src/main/java holds ~37 .kt files today.
        assertTrue(
            "source-file walk found suspiciously few .kt files (${files.size}) — the walk is broken",
            files.size > 20,
        )

        // Every exemption must still point at a real file — a rename/delete must not silently drop a cap.
        val missing = EXEMPTIONS.filter { !File(root, it.path).exists() }.map { it.path }
        assertEquals("exempt file(s) renamed or deleted — update the exemption path:", emptyList<String>(), missing)

        val capByPath = EXEMPTIONS.associate { it.path to it.maxLines }
        val grown = mutableListOf<String>()
        val newViolations = mutableListOf<String>()
        var maxObserved = 0
        for (rel in files) {
            val lines = countLines(File(root, rel))
            if (lines > maxObserved) maxObserved = lines
            val cap = capByPath[rel]
            if (cap != null) {
                if (lines > cap) grown += "$rel: $lines > recorded $cap"
            } else if (lines > RUNAWAY_CAP) {
                newViolations += "$rel: $lines > $RUNAWAY_CAP"
            }
        }

        // Vacuity guard: if countLines silently returned small values, the two checks below would pass
        // hollowly. 700 (not RUNAWAY_CAP) on purpose — nothing is over 1000 any more, which is the point
        // of the rewrite; the largest pinned file (GpsRecordingService.kt, 946) proves the counter reads.
        assertTrue(
            "line counter appears broken — no source file measured over 700 lines (max=$maxObserved)",
            maxObserved > 700,
        )

        assertEquals(
            "exempt file grew past its recorded cap — split it, or (with a written reason) raise the number " +
                "in KotlinFileSizeRatchetTest.kt:\n${grown.joinToString("\n")}",
            emptyList<String>(), grown,
        )
        assertEquals(
            "file crossed the $RUNAWAY_CAP-line RUNAWAY cap — this is no longer a judgement call. Split it, or pin it " +
                "in KotlinFileSizeRatchetTest.kt:\n${newViolations.joinToString("\n")}",
            emptyList<String>(), newViolations,
        )
    }

    /** Line count as `wc -l` (newline count) — the metric the recorded caps were seeded with. */
    private fun countLines(f: File): Int = f.readText().count { it == '\n' }

    /**
     * Locate app/src/main/java from the test's own working directory — Gradle runs unit tests with the
     * working dir set to the module dir (GpsRecorder/app). Resolved relative to user.dir (never an absolute
     * path), so it works in any worktree checkout — the Kotlin equivalent of the TS ratchet's
     * fileURLToPath(import.meta.url).
     */
    private fun sourceRoot(): File {
        val userDir = System.getProperty("user.dir") ?: error("user.dir is not set")
        var dir: File? = File(userDir).absoluteFile
        while (dir != null) {
            val candidate = File(dir, "src/main/java")
            if (candidate.isDirectory) return candidate
            dir = dir.parentFile
        }
        error("could not locate src/main/java from ${System.getProperty("user.dir")}")
    }
}
