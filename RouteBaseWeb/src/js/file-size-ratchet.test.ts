// @vitest-environment node
import { describe, expect, it } from 'vitest';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';

/**
 * File-size ratchet — a RUNAWAY detector plus per-file growth pins. Pure filesystem walk, no DOM
 * (mirrors css-system-invariants.test.ts).
 *
 * ⚠ SCOPE CHANGED 2026-07-31 (deliberate). This used to hard-gate the 700-line cap, and
 * that was a mistake worth recording so it is not re-introduced:
 *
 *   Line count is a PROXY for "too many responsibilities in one file". It is not the thing itself, and
 *   this table was the evidence — `core/grid/grid-view.ts` is pinned at 844 with a note saying it is
 *   "the correct kind of large", and `maplibre/maplibre-helpers.ts` is pinned UNDER the cap because its
 *   real defect (a grab-bag of ~7 unrelated jobs) is invisible to a line count. Two of five entries
 *   said, in prose, that the number was not the point. A rule needing an essay per exemption is being
 *   carried by the essays.
 *
 *   It also mis-scored real work. A 2026-07-31 refactor cut GpsRecordingService.kt 964 → 946 while adding the first
 *   tests to the upload path — big improvement, metric barely moved — and one job correctly reported
 *   "726, I will not game it" rather than shoving DOM into a module labelled pure. The gate punished
 *   the honest outcome and would have scored a bad split (two 400-line halves sharing mutable state)
 *   as a win.
 *
 * So: **splitting is a judgement call, not a gate.** The 700 line is now a REVIEW TRIGGER, enforced as
 * an eslint `max-lines` WARNING, and the architectural rules that do have crisp definitions were
 * promoted to hard gates instead (eslint `no-restricted-imports` for map-engine layering,
 * LayeringTest.kt, LayeringTests.cs). What survives here:
 *
 *   • RUNAWAY_CAP (1000) — fails on any unlisted logic file past it. A file this size is not a
 *     judgement call any more; nobody argues 1200 lines is fine.
 *   • Per-file growth pins — a listed file may not GROW past its recorded number. This is the part
 *     that was always doing the work: it stops silent drift in the specific files known to be big,
 *     without demanding anyone split them.
 *   • Pins are monotonic BY CONVENTION: when a pinned file shrinks, lower its number in the same
 *     commit. Not machine-enforced — a stale pin is a tidiness issue, and failing a green build over
 *     one is exactly the over-enforcement this rewrite removes.
 *
 * Why these are pinned rather than split (decided 2026-07-30; ARCH-F2 2026-07-31):
 *   • core/grid/grid-view.ts (844; +1 in G3: the data-attrs import line) — the "correct kind of large": ONE abstraction, 17 consumers across
 *     6 feature directories, fully tested. The convention's own framing applies — "large size there =
 *     coupling smell, not a split target". Splitting it would invent seams nobody wants.
 *   • segment-stats/segment-stats-replay-ui.ts (813) — was 806 → 763 → 726 (extracted
 *     the per-frame live-data math) → 728 (slice L12: +1 must import, +1 speed/gap/bar
 *     rebind line for the noUncheckedIndexedAccess `!` retirement). Still inside the convention's "~50-line
 *     overage OK" band. **Decided 2026-07-31: leave it — do not SPLIT it.** The one
 *     remaining pure unit (the per-effort row-HTML builder in `_populateEffortList`) would carry it
 *     under 700, but it is interleaved with createElement/addEventListener, so it is a real refactor for
 *     ~26 lines. Not worth it. Do not re-propose the split.
 *     ⚠ "Do not split" is NOT "may never grow by a line". The 2026-07-31 ctx conversion needed ~+4
 *     lines to wrap the module in `initSegmentStatsReplayUi(ctx)` — the last step of removing the
 *     ambient `ctx` singleton from the replay cluster — read this pin as an absolute ceiling, reverted
 *     a working conversion, and escalated. That was a reasonable reading of a badly worded note, so:
 *     **a raise of a handful of lines to complete that ctx conversion is PRE-APPROVED** — make it, and
 *     say so in the commit message. A pin is a growth guard, not a target.
 *     777 → 790 (2026-08-03, replay speed readout): one `SpeedReadout` per effort row, built beside the
 *     existing `_speedEls`/`_gapEls`/`_barEls` arrays, plus a reset on seek. The smoothing itself is in
 *     `core/speed-readout.ts` and the row math stayed in `segment-stats-replay-live-data.ts` — what
 *     landed here is the per-effort lifecycle, which is exactly what this file owns.
 *     ⚠ The prose above said "(728)" while the pin said 809 — the header drifted behind the pin, which
 *     is exactly the drift these pins exist to catch. Corrected 2026-08-03; the PIN is the authoritative count.
 *     809 → 813 (2026-08-03, centred speed window): `core/speed-readout.ts` is gone, replaced by
 *     `core/speed-window.ts` (the measurement) + `core/speed-label.ts` (when it may change). Net here
 *     is +4 after giving back the readout import and the seek-reset loop: the playback rate the window
 *     width follows, and a last-written-string guard so a steady rider costs no DOM write. Same
 *     justification as the raise above — what lives here is the per-effort lifecycle and the DOM write.
 *   • race-sim/race-replay-view.ts — a replay refactor cut it 792 → 755; it has grown since, each raise
 *     argued at its entry in the table below. No count here on purpose: this line drifted behind the
 *     pin once already (755 vs 841), which is the failure the ⚠ two entries above describes. The PIN
 *     is the authoritative count.
 *   • maplibre/maplibre-helpers.ts (651) — pinned UNDER the 700 cap on purpose (ARCH-F2). It is the
 *     most-imported file in the map layer AND a `helpers = {}` grab-bag of ~7 unrelated jobs (style
 *     build, geometry math, network fetch, control construction, instance teardown). Its risk is
 *     GROWTH, not its current size — under the cap it is invisible to the hard-700 rule, so it could
 *     creep to 699 unnoticed. Pinned at today's count so any growth forces the split first. The
 *     eventual split is by job: style / geometry / controls / lifecycle. Not split now — that touches
 *     the map layer's most-imported file and ARCH-F1 already churned this surface.
 * Generated files (src/js/generated/**) and *.d.ts are exempt and skipped by the walk.
 * Test files (*.test.ts, ≤1000) are not logic files and are skipped too.
 */

const RUNAWAY_CAP = 1000;   // review trigger is eslint max-lines (700, a WARNING); this is the runaway gate

// path (relative to src/js) → recorded line count: a GROWTH pin, not a target. These files may stay
// this size indefinitely; they just may not get bigger without a written reason in the same commit.
// Lower on shrink (convention, not machine-enforced). Seeded 2026-07-30 at each file's `wc -l`.
const EXEMPTIONS: ReadonlyArray<{ path: string; maxLines: number }> = [
	// 845 → 844 (settings step 11): lowered on shrink, per this table's convention. A cap left above the
	// real count is a cap that permits growth nobody argued for.
	{ path: 'core/grid/grid-view.ts', maxLines: 844 }, // was +1 in K5: the async-listener import line
	// segment-stats-map-maplibre.ts retired 2026-07-31: 777 → 680, now under the cap on its own.
	// +1 (settings step 10): `speedPrefKey` on the ReplayTransportIds record, which is what lets the
	// transport remember this replay KIND's playback speed. A required field, one line per call site,
	// and the alternative — deriving the kind from an element id — is the string-fragment identifier
	// the conventions forbid. Same reason on race-replay-view below.
	// 729 → 777 (blocking-dialog pass, 2026-08-02): the three alert()s this file owned are gone. Clicking
	// Replay mid-load used to alert(), and a modal BLOCKS the page's chunked effort load until dismissed —
	// the impatient click delayed the very data it waited for. The wait/cancel state machine went to its
	// own module (core/pending-action-gate.ts); what stays here is the readiness predicate, the
	// deferred-start entry point (`_beginReplay`, split out of `startReplay`), the button's spinner
	// painting and `_syncReplayAvailability` (0 efforts ticked = a disabled button with the reason in its
	// tooltip, instead of a click answered by a modal). All of it is this file's own DOM work. Not a split
	// candidate — see header.
	// 790 → 809 (click-to-hold, 2026-08-03): the effort sidebar joins the race sidebar in latching open on
	// a click instead of always timing out. The RULE is shared — core/panel-hold-open owns the latch, the
	// two exempt-row selectors and the reasoning for both, and effort-list-expand gained one `isHeld`
	// predicate. What lands here is only the wiring this file already owns: the panel lookup, the
	// create/destroy pair beside the existing _effortListExpand ones, and the header-collapse release.
	{ path: 'segment-stats/segment-stats-replay-ui.ts', maxLines: 813 },
	// 757 → 790 (races Phase 4, staggered starts): the gun/net time-basis toggle. Raised deliberately,
	// with the reason, per the header's "a pin is a growth guard, not a target".
	//
	// The +33 is a `timeBasis` variable, `wireTimeBasisToggle()` and the basis passed to buildRace at its
	// two call sites. It stays HERE rather than moving to a module because all three touch view-private
	// state — the basis is a build INPUT, and the change handler must go through `rebuildField()`, which
	// is what carries follow and paint across the rebuild by identity. A module would have to be handed
	// a rebuild callback and a payload reader, i.e. the same lines with an indirection on top.
	// 790 → 833 (rider emphasis + the Names toggle, 2026-08-03): the second presentation channel — a real
	// rider's size, drawn in front, and named — wired exactly like the paint beside it: an `onEmphasised`
	// handler, the write-back onto the participant the payload owns (the RF-02 seam), the full-state push
	// in `attachRaceLayers`, and one `emphasisSet()` derivation the charts read.
	//
	// It stays HERE for the same reason the paint wiring does: every line touches view-private state that
	// a module would have to be handed back (the race, the markers, the charts, the rebuild-by-identity
	// path). Two things kept the number down rather than up: emphasis is DERIVED from the participants
	// instead of mirrored in a second `painted`-style map, and the label policy moved OUT to
	// `race-name-labels.ts` — which also deleted the two copies of the "who is named" computation this
	// file used to carry, so `applyLabels()` is now the single caller.
	// 833 → 841 (race speed panel, 2026-08-03): the zoomable speed-vs-completion chart below the map. The
	// +8 is deliberately the whole cost: an import, a `speedPanel` field, one construction line, one
	// `speedPanel?.update(...)` inside the EXISTING `updateSpeedProfile()`, one call in `onEmphasised`
	// (emphasis decides who is drawn) and one destroy.
	//
	// It is this small because the panel is its own slice — `race-speed-panel.ts` owns the DOM lookup, the
	// rider set, the settings read and the empty state, and `race-rider-speed.ts`/`race-speed-chart.ts`
	// own the series and the chart. Folding the update into `updateSpeedProfile()` rather than adding
	// calls at its two call sites is what kept the two rebuild paths from each growing a line.
	// 841 → 858 (rename-in-place, 2026-08-05): a `reload()` on the returned view — re-read the payload,
	// then `rebuildField()`. The +17 is one four-line method and its comment.
	//
	// It buys the removal of `window.location.reload()` from the riders panel, which had become a data
	// loss: that panel is now a column of editable name boxes, and reloading the document to save one
	// rider discarded whatever the user had typed into the others. The method is this short because
	// `rebuildField()` already does the work and already carries follow and paint across by identity —
	// this only re-seeds the payload it reads, which is view-private state and cannot live elsewhere.
	// 858 → 859 (per-rider call-out, 2026-08-11): ONE argument — the hidden-rider set handed to
	// labelledIndices, so a real rider the owner quieted loses his map label the same way he loses his
	// speed-chart line. The policy and the set-builder both live in race-name-labels.ts (calloutOffIndices);
	// this file only passes what it already owns, which is why the switch costs it a single line.
	// 859 → 900 (speed chart becomes the primary surface, 2026-08-12): the dock's elevation strip is
	// fullscreen-only now and the speed panel under the map carries the course instead — terrain, section
	// rules, the field aggregate, the playhead, and click/drag-to-place a section boundary. The +41 here is
	// only the FAN-OUT: one `setCheckpointsEverywhere` helper (both surfaces draw the same course, so both
	// hear every edit), a shared `placement` literal spread into both constructors, the panel's new course
	// arguments, one `setFieldProfile` line, one `setPlayhead` line in render(), and a `charts.tick(true)`
	// in onResize because a strip revealed by fullscreen must repaint rather than wait for a clock tick.
	//
	// Everything with substance went into the slice, which is why the raise is this size against the size
	// of the change: `race-speed-backdrop.ts` (265) owns the terrain/section/rule painting,
	// `race-speed-checkpoints.ts` (218) owns the whole place/drag/select gesture, and the panel + chart own
	// their own state. This file gained no branch and no new concept — it hands the same callbacks to a
	// second surface.
	// 900 → 903 (chart→map hover, 2026-08-12): three lines — an `onHover` on the speed panel that calls
	// `charts.hoverAt(metres)`. The strip owned the map dot and still does (its axis has always been
	// chainage); it went fullscreen-only last round, which took the dot off the inline view with it. The
	// door back in is one delegation in race-charts, so this file only passes a callback.
	// 903 → 912 (staged paint, 2026-08-20): the owner's paint/emphasis stopped writing on every action —
	// it stages in the browser and a Save button commits it. The unsaved-changes flag has to live ABOVE
	// the results table, because rebuildField() destroys and re-mounts that table and a flag owned inside
	// it would come back clean over unsaved work. Nine lines: the `paintDirty` declaration with the
	// paragraph explaining why it is here, and the two option lines that pass it in and take it back. The
	// staging, the save request and the unload guard are all in race-results-table.ts.
	{ path: 'race-sim/race-replay-view.ts', maxLines: 912 }, // +1 in L9: the fitToCourse rebind line,
	                                                         // +1 in step 10: speedPrefKey (see above)
	// Pinned UNDER the 700 cap on purpose (ARCH-F2): a grab-bag whose growth is the risk. See header.
	// 651 → 659 (2026-08-01): +8 lines of trap-WHY comments migrated from the deleted
	// MAP-CONTRACTS.md to their code sites — the setTerrain-guard hang and the buildInlineTerrainStyle
	// deep-clone. Documentation moving INTO code is exactly this pass's goal; the file was not otherwise
	// grown and the split is still out of scope (see header).
	// 659 → 662 (settings-defaults, 2026-08-01): +1 import of the generated PROFILE_SETTINGS_DEFAULTS,
	// +2 from wrapping the two hillshade reads. This file held the worst copy of the drift — it defaulted
	// hillshadeExaggeration to 0.0, which does not just differ from the C# 0.2, it SKIPS the hillshade
	// layer. Three lines to make that value un-driftable is the trade the pin exists to permit.
	// 662 → 655 (settings step 11): the three lines above went, and four more with them. Gate (f) made
	// core/account-settings.ts the only reader of the defaults, so this file no longer imports them or
	// wraps its hillshade reads at all — it takes a complete, non-nullable payload from the seam. The
	// pin that was raised to permit an anti-drift fix is lower than before the drift existed.
	// 655 → 658 (TerrainExaggeration setting, 2026-08-03): the `TERRAIN_EXAGGERATION` const became
	// `terrainExaggeration()`, reading the new account setting. +3 is the WHY comment, not the code: the
	// const had a module-scope capture in maplibre-terrain.ts that a settings-backed value turns into a
	// silent bug (the map would pin to the default forever), and that trap has to be readable at the seam.
	{ path: 'maplibre/maplibre-helpers.ts', maxLines: 658 },
];

// import.meta.url → .../src/js/file-size-ratchet.test.ts; one `..` pops the filename → src/js.
const srcRoot = path.resolve(fileURLToPath(import.meta.url), '..');

/** Line count as `wc -l` (newline count) — the metric the recorded caps above were seeded with. */
function countLines(abs: string): number {
	return (fs.readFileSync(abs, 'utf8').match(/\n/g) ?? []).length;
}

/** Non-test, non-generated, non-.d.ts .ts files under src/js, as paths relative to src/js. */
function collectSourceFiles(dir: string): string[] {
	const out: string[] = [];
	for (const ent of fs.readdirSync(dir, { withFileTypes: true })) {
		const abs = path.join(dir, ent.name);
		if (ent.isDirectory()) {
			if (ent.name === 'generated') continue; // codegen output — exempt
			out.push(...collectSourceFiles(abs));
		} else if (ent.name.endsWith('.ts') && !ent.name.endsWith('.test.ts') && !ent.name.endsWith('.d.ts')) {
			out.push(path.relative(srcRoot, abs));
		}
	}
	return out;
}

describe('frontend file-size ratchet', () => {
	it('no logic file runs away past 1000 lines, and no pinned file grows', () => {
		const files = collectSourceFiles(srcRoot);
		// Vacuity guard: a broken walk (wrong root, empty read) would make every check below pass
		// silently. The tree has ~314 non-test source files today.
		expect(files.length, 'source-file walk found suspiciously few files — the walk is broken').toBeGreaterThan(200);

		// Every exemption must still point at a real file — a rename/delete must not silently drop a cap.
		const missing = EXEMPTIONS.filter((e) => !fs.existsSync(path.join(srcRoot, e.path)));
		expect(missing.map((e) => e.path), 'exempt file(s) renamed or deleted — update the exemption path:').toEqual([]);

		const capByPath = new Map(EXEMPTIONS.map((e) => [e.path, e.maxLines]));
		const grown: string[] = [];
		const newViolations: string[] = [];
		let maxObserved = 0;
		for (const rel of files) {
			const lines = countLines(path.join(srcRoot, rel));
			if (lines > maxObserved) maxObserved = lines;
			const cap = capByPath.get(rel);
			if (cap !== undefined) {
				if (lines > cap) grown.push(`${rel}: ${lines} > recorded ${cap}`);
			} else if (lines > RUNAWAY_CAP) {
				newViolations.push(`${rel}: ${lines} > ${RUNAWAY_CAP}`);
			}
		}

		// Vacuity guard: if countLines silently returned small values, the two checks below would pass
		// hollowly. The largest exempt file (grid-view.ts, 843) proves the counter reads real content.
		expect(maxObserved, 'line counter appears broken — no source file measured over 700 lines').toBeGreaterThan(700);

		expect(
			grown,
			`exempt file grew past its recorded cap — split it, or (with a written reason) raise the number in file-size-ratchet.test.ts:\n${grown.join('\n')}`,
		).toEqual([]);
		expect(
			newViolations,
			`file crossed the ${RUNAWAY_CAP}-line RUNAWAY cap — this is no longer a judgement call. Split it, or add a documented pin in file-size-ratchet.test.ts:\n${newViolations.join('\n')}`,
		).toEqual([]);
	});
});
