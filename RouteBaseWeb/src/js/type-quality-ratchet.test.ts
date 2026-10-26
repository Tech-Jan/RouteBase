// @vitest-environment node
import { describe, expect, it } from 'vitest';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';

/**
 * Type-quality ratchets (AR-02 recipe, TS tier — sibling of file-size-ratchet.test.ts). Six pins,
 * measured 2026-08-01 (f: 2026-08-02), each PARITY: count/set must equal the pin — grew means fix the
 * new site, shrank means lower the pin in the same commit.
 *
 * (a) MIRROR DETECTOR — locally declared interface/type names that collide with a generated
 *     contracts.ts alias. The sanctioned shape is import + Omit/refine (race-api.ts,
 *     overview-types.ts:58); an independent redeclaration drifts silently (7 TODO(openapi-review)
 *     markers track known drift). G1 retires the redeclarations; the FLOOR that remains is the
 *     deliberately frozen edit-ops.ts chain (invariant 8: the op/script/stamp mirror is an atomic,
 *     hand-frozen wire contract) plus refinements that carry more truth than the generated twin
 *     (gapSeconds absent-vs-null).
 * (b) `.json() as` casts — an unverified assertion on wire data. The typed-path helper
 *     (generated-helpers/endpoint-types.ts ResponseOf) binds the type to endpoint+verb instead.
 *     G2 takes this to zero.
 * (c) postfix `!` (non-null assertion) — noUncheckedIndexedAccess is ON and this is the honest
 *     floor: every surviving `!` carries a `// !-ok -- <reason>` line directly above it, and the
 *     test enforces that pairing, so the floor audits itself.
 * (d) raw `.dataset.` reads — a hidden Razor↔TS contract with inline coercion and no NaN guards.
 *     G3's core/data-attrs.ts helper becomes the single reader; only the helper may touch dataset.
 * (e) `as unknown as` double casts — the unchecked escape hatch. K4 centralized chart/map
 *     internals into core/chart-internals.ts + core/map-internals.ts; what remains is the pinned
 *     floor (test harness, style-spec coercions, factory-boundary option literals).
 * (f) blocking dialogs (alert/confirm/prompt) — they freeze the page, including its own in-flight
 *     loads. The pinned remainder is failure terminals + destructive confirms; everything else has a
 *     non-blocking answer (see the test's comment for the five substitutions).
 */

const SRC = path.resolve(path.dirname(fileURLToPath(import.meta.url)));

function walk(dir: string, out: string[] = []): string[] {
	for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
		const p = path.join(dir, e.name);
		if (e.isDirectory()) {
			if (e.name === 'generated') continue;
			walk(p, out);
		} else if (
			e.name.endsWith('.ts') &&
			!e.name.endsWith('.test.ts') &&
			!e.name.endsWith('.d.ts')
		) {
			out.push(p);
		}
	}
	return out;
}

function rel(p: string): string {
	return path.relative(SRC, p).replaceAll('\\', '/');
}

const STRINGS = /'(?:[^'\\]|\\.)*'|"(?:[^"\\]|\\.)*"|`(?:[^`\\]|\\.)*`/g;
const COMMENT = /^\s*(\/\/|\*|\/\*)/;

describe('type-quality ratchets', () => {
	const files = walk(SRC);
	it('walk sees the real tree (vacuity)', () => {
		expect(files.length).toBeGreaterThan(250);
	});

	it('(a) generated-contract name collisions match the pin', () => {
		const contracts = fs.readFileSync(path.join(SRC, 'generated/contracts.ts'), 'utf8');
		const aliases = new Set([...contracts.matchAll(/^export type (\w+) =/gm)].map((m) => m[1]));

		// G1 retired every independent redeclaration; this pin is now the documented FLOOR.
		const pin = new Set([
			// Same-name aliases/refinements of the generated twin (compiler-verified, cannot drift):
			'core/grid/grid-types.ts:GridSort',                                   // refine: direction → SortDirection union
			'statistics/statistics-filter-schema.ts:OwnerOption',                 // alias to generated
			'track-edit/edit-ops.ts:TimeMode',                                    // alias to generated (D1 stamp)
			'segment-stats/segment-stats-effort-types.ts:EffortPerformanceData',  // hydrated refine over the wire twin
			// Sanctioned import+refine shapes (compiler-verified against the generated twin):
			'overview/activities-map-display.ts:ActivityGridResponse',
			'overview/overview-types.ts:ActivitiesGridBootstrap',
			'segment-stats/segment-stats-effort-types.ts:EffortPointsWire',
			'track-edit/edit-session-api.ts:SaveRequest',
			// The deliberately frozen edit-ops chain (invariant 8), do not "dedup":
			'track-edit/edit-ops.ts:EditOp',
			'track-edit/edit-ops.ts:EditScript',
			'track-edit/edit-ops.ts:TrackVersionStamp',
			'track-edit/edit-session-api.ts:AppendOpsRequest',
			'track-edit/edit-session-api.ts:SessionStateResponse',
		]);

		const found = new Set<string>();
		for (const f of files) {
			const text = fs.readFileSync(f, 'utf8');
			for (const m of text.matchAll(/^(?:export\s+)?(?:interface|type)\s+(\w+)\b/gm)) {
				if (aliases.has(m[1])) found.add(`${rel(f)}:${m[1]}`);
			}
		}

		const added = [...found].filter((x) => !pin.has(x)).sort();
		const stale = [...pin].filter((x) => !found.has(x)).sort();
		expect(added, 'NEW local declaration shadowing a generated contract — import it from generated/contracts and refine (Omit/&) instead').toEqual([]);
		expect(stale, 'pinned collision no longer exists — delete the pin entry in this commit').toEqual([]);
	});

	it('(b) .json() as cast count matches the pin', () => {
		const PIN = 0; // G2: permanent — wire bodies are BOUND (annotated const / ResponseOf), never asserted
		const sites: string[] = [];
		for (const f of files) {
			const text = fs.readFileSync(f, 'utf8');
			for (const [i, line] of text.split('\n').entries()) {
				if (/\.json\(\)\s*\)?\s*as\s/.test(line)) sites.push(`${rel(f)}:${i + 1}`);
			}
		}
		expect(
			sites.length,
			`(pin ${PIN}) .json() as sites — new fetches use ResponseOf (generated-helpers/endpoint-types.ts); sites:\n${sites.join('\n')}`,
		).toBe(PIN);
	});

	it('(c) postfix non-null-assertion count matches the pin, every survivor annotated', () => {
		// G3 swept 267 → 190; L swept 190 → 1 while flipping noUncheckedIndexedAccess ON.
		// This IS the honest floor: segment-statistics' ctx `null!` (retyping ctx `| null` is the
		// forbidden singleton restructure). A surviving `!` must carry a `// !-ok -- <reason>`
		// line directly above it — an unannotated one fails here even at the right count, so the
		// floor audits itself. New code gets a guard, a must() invariant, or narrowing — never `!`.
		const PIN = 1;
		let count = 0;
		const unannotated: string[] = [];
		for (const f of files) {
			const lines = fs.readFileSync(f, 'utf8').split('\n');
			for (const [i, line] of lines.entries()) {
				if (COMMENT.test(line)) continue;
				const stripped = line.replace(STRINGS, "''");
				const hits = (stripped.match(/[\w)\]]!(?![=.]?=)/g) ?? []).length;
				if (hits === 0) continue;
				count += hits;
				if (!/^\s*\/\/ !-ok -- /.test(lines[i - 1] ?? '')) unannotated.push(`${rel(f)}:${i + 1}`);
			}
		}
		expect(
			count,
			`(pin ${PIN}) postfix ! count — new assertions need a guard/must()/narrowing instead; lowered counts lower the pin in the same commit`,
		).toBe(PIN);
		expect(
			unannotated,
			'every pinned ! needs a `// !-ok -- <reason>` line DIRECTLY above it (the floor is self-auditing)',
		).toEqual([]);
	});

	it('(d) raw .dataset. access count matches the pin', () => {
		const PIN = 0; // G3: permanent — core/data-attrs.ts is the single reader/writer
		const perFile = new Map<string, number>();
		for (const f of files) {
			if (rel(f) === 'core/data-attrs.ts') continue; // the sanctioned single reader (G3)
			const n = (fs.readFileSync(f, 'utf8').match(/\.dataset\./g) ?? []).length;
			if (n > 0) perFile.set(rel(f), n);
		}
		const total = [...perFile.values()].reduce((a, b) => a + b, 0);
		expect(
			total,
			`(pin ${PIN}) raw .dataset. reads — go through core/data-attrs.ts (typed, NaN-guarded); top: ${[...perFile.entries()].sort((a, b) => b[1] - a[1]).slice(0, 5).map(([k, v]) => `${k}=${v}`).join(', ')}`,
		).toBe(PIN);
	});

	it('(e) `as unknown as` cast count matches the pin', () => {
		// K4 end-state: chart/map internals go through the two sanctioned seams
		// (core/chart-internals.ts 2, core/map-internals.ts 3 — each cast documented in place);
		// fake-map-harness IS the test seam (5); maplibre-helpers style-spec coercions (2); the
		// remaining 9 are factory-boundary Chart.js option-literal casts (statistics-chart 5,
		// statistics-bars 2, chart-series 1, segment-stats-charts-progress 1). A NEW double cast
		// belongs in a seam accessor, not at a call site.
		const PIN = 21;
		const perFile = new Map<string, number>();
		for (const f of files) {
			let n = 0;
			for (const line of fs.readFileSync(f, 'utf8').split('\n')) {
				if (COMMENT.test(line)) continue;
				const stripped = line.replace(STRINGS, "''");
				n += (stripped.match(/as unknown as/g) ?? []).length;
			}
			if (n > 0) perFile.set(rel(f), n);
		}
		const total = [...perFile.values()].reduce((a, b) => a + b, 0);
		expect(
			total,
			`(pin ${PIN}) as-unknown-as count — chart/map internals go through core/chart-internals.ts / core/map-internals.ts; top: ${[...perFile.entries()].sort((a, b) => b[1] - a[1]).slice(0, 5).map(([k, v]) => `${k}=${v}`).join(', ')}`,
		).toBe(PIN);
	});
	it('(f) blocking-dialog count matches the pin', () => {
		// alert/confirm/prompt freeze the page — including its own in-flight loads — until dismissed.
		// That is right for a destructive confirm and for a terminal the user must see before a reload
		// or navigation; it is wrong for everything else, and the wrongest case is a refusal fired at a
		// page that is still loading ("Map and performance data must be loaded first" blocked the very
		// chunked load it was waiting for). The 2026-08-02 pass took 101 → 79 by asking, per site:
		//   • still loading?      → defer the action, spin the control (core/pending-action-gate.ts)
		//   • already running?    → adopt the run; the operation toast is already saying it
		//   • form validation?    → core/inline-error.ts, next to the field, clears on edit
		//   • impossible state?   → disable the control and put the reason in its tooltip
		//   • can't happen?       → console.error; a user cannot act on it anyway
		// What is pinned here is the REMAINDER: failure terminals and destructive confirms. A new dialog
		// must argue for itself — if it is genuinely one of those two, raise the pin in the same commit
		// and say why. Lower it on removal, like every other pin in this file.
		// 79 → 80 (2026-08-11): overview/activities-export-controller.ts, the one terminal for "the export
		// could not be STARTED" (network down, queue full). It is a failure terminal by the rule above —
		// the click produced nothing, so there is no operation toast to adopt and no field to annotate.
		// The adoptable case is handled without a dialog: a 409 (an export is already building) starts
		// polling instead of reporting.
		const PIN = 80;
		const perFile = new Map<string, number>();
		for (const f of files) {
			let n = 0;
			for (const line of fs.readFileSync(f, 'utf8').split('\n')) {
				if (COMMENT.test(line)) continue;
				const stripped = line.replace(STRINGS, "''");
				n += (stripped.match(/(?<![\w.])(?:alert|confirm|prompt)\s*\(/g) ?? []).length;
				n += (stripped.match(/\bwindow\.(?:alert|confirm|prompt)\s*\(/g) ?? []).length;
			}
			if (n > 0) perFile.set(rel(f), n);
		}
		const total = [...perFile.values()].reduce((a, b) => a + b, 0);
		expect(
			total,
			`(pin ${PIN}) alert/confirm/prompt count — a blocking dialog needs a reason (see this test's comment); top: ${[...perFile.entries()].sort((a, b) => b[1] - a[1]).slice(0, 5).map(([k, v]) => `${k}=${v}`).join(', ')}`,
		).toBe(PIN);
	});

});
