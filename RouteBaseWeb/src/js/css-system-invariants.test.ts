// @vitest-environment node
import { describe, expect, it } from 'vitest';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';

// Text-level guard over the hand-written stylesheet tree (Vite does not touch CSS —
// no imports, no @layer, no dead-code elimination), pinning the few consolidation
// invariants from the CSS-system pass so a legitimate future change to one
// reads as deliberate rather than as drift. No DOM/browser — pure string checks.
const cssDir = path.resolve(fileURLToPath(import.meta.url), '../../../wwwroot/css');
const cssFiles = fs
	.readdirSync(cssDir)
	.filter((f) => f.endsWith('.css'))
	.sort();

function read(file: string): string {
	return fs.readFileSync(path.join(cssDir, file), 'utf8');
}

// The canonical breakpoint ladder + value+1 min companions + the one kept
// single-use exception. Mirrors the comment block in cockpit-tokens.css — keep
// the two in sync. This set is a RATCHET, not a snapshot: the tail (520/640/720/
// 992/1100) regrew silently once because nothing watched it, so the CSS-system
// cleanup collapsed it onto the ladder and TIGHTENED this list to lock it in. Only
// shrink or hold — a new value here must be a deliberate ladder addition (added
// to cockpit-tokens.css too), never an ad-hoc breakpoint slipped back in.
const ALLOWED_BREAKPOINTS = new Set<number>([
	// canonical max-width ladder
	480, 600, 768, 900, 1024, 1600,
	// value+1 min-width companions (the no-overlap idiom, e.g. 601 pairs 600, 901 pairs 900)
	481, 601, 769, 901, 1025, 1601,
	// the one kept single-use exception — replay-controls.css ≤320px transport bar
	400,
]);

describe('CSS system invariants', () => {
	it('every @media breakpoint uses a value from the canonical set', () => {
		const offenders: string[] = [];
		let total = 0;
		for (const file of cssFiles) {
			const css = read(file);
			// Only look inside @media preludes (up to the first `{`), so `max-width`/
			// `min-width` used as ordinary CSS properties are not mistaken for breakpoints.
			for (const media of css.matchAll(/@media[^{]*\{/g)) {
				for (const m of media[0].matchAll(/(?:max|min)-width:\s*(\d+)px/g)) {
					total++;
					const px = Number(m[1]);
					if (!ALLOWED_BREAKPOINTS.has(px)) {
						offenders.push(`${file}: ${px}px`);
					}
				}
			}
		}
		// Guard against an inert regex silently matching nothing (which would make the
		// off-list check vacuously pass). The tree has ~56 width breakpoints today.
		expect(total).toBeGreaterThan(40);
		expect(offenders, `off-list breakpoints (add to the canonical set in cockpit-tokens.css if deliberate):\n${offenders.join('\n')}`).toEqual([]);
	});

	it('cockpit-tokens.css is the only :root design-token block', () => {
		const offenders = cssFiles.filter((f) => f !== 'cockpit-tokens.css' && /:root\b/.test(read(f)));
		expect(offenders, `a second :root block forks the token system — extend cockpit-tokens.css instead:\n${offenders.join('\n')}`).toEqual([]);
	});

	it('the .btn-cockpit-* family is declared only in cockpit-components.css', () => {
		// A selector line that STARTS with .btn-cockpit is a family declaration; a
		// descendant usage like `.tour-card-footer > .btn-cockpit` does not, and is
		// a legitimate page-level override.
		const offenders: string[] = [];
		for (const file of cssFiles) {
			if (file === 'cockpit-components.css') continue;
			read(file)
				.split('\n')
				.forEach((line, i) => {
					if (/^\s*\.btn-cockpit\b/.test(line)) offenders.push(`${file}:${i + 1}`);
				});
		}
		expect(offenders, `.btn-cockpit-* must live with the family in cockpit-components.css:\n${offenders.join('\n')}`).toEqual([]);
	});

	/**
	 * The chart readout floats OUT of its panel by design, so it competes with whatever map chrome the
	 * page has — and it lost. It sat at 1200, a number picked against Leaflet's ~700–1000 panes and a
	 * "fullscreen map at 1500" that no longer exists, while the race replay's dock ladder starts at 9001.
	 * Neither the dock's host nor the readout's opens a stacking context, so 1200 and 9001 competed
	 * directly and the box was painted over.
	 *
	 * Pinned as a RANGE, not a value: it has to clear every page's map chrome and stay under a fullscreen
	 * map, and both of those move.
	 */
	it('the chart readout outranks map chrome but not a fullscreen map', () => {
		const components = read('cockpit-components.css');
		const block = /\.chart-readout\s*\{([^}]*)\}/.exec(components)?.[1] ?? '';
		const readout = Number(/z-index:\s*(\d+)/.exec(block)?.[1]);
		expect(readout, '.chart-readout must declare a z-index').not.toBeNaN();

		// Every z-index any stylesheet gives the fullscreen map overlay.
		const fullscreen: number[] = [];
		for (const file of cssFiles) {
			for (const m of read(file).matchAll(/\.map-fullscreen[^{]*\{[^}]*?z-index:\s*(\d+)/g)) {
				fullscreen.push(Number(m[1]));
			}
		}
		expect(fullscreen.length, 'no .map-fullscreen z-index found — has the overlay been renamed?')
			.toBeGreaterThan(0);

		// The tallest piece of in-page chrome the box has to clear (the race replay's exit button, 9003).
		expect(readout).toBeGreaterThan(9003);
		expect(readout).toBeLessThan(Math.min(...fullscreen));
	});

	/**
	 * The mobile tabbar is `position: fixed` and lives OUTSIDE `.cockpit-main`, which is
	 * `isolation: isolate` — so any overlay rendered inside the main column has its z-index trapped and
	 * cannot out-paint the bar no matter how large the number. The race replay's fullscreen stage sits at
	 * 8999 and still had the bar over its transport dock on mobile.
	 *
	 * Two answers are legitimate and both are in use: hide the bar (`replay-active`, the race stage) or
	 * raise `.cockpit-main` itself above it (`map-fullscreen-active`). The flags are derived from the
	 * sheets, so a new full-viewport mode is covered the moment its CSS lands.
	 */
	it('every full-viewport body flag either hides the mobile tabbar or lifts the column over it', () => {
		const shell = read('cockpit-shell.css');
		const tabbarZ = Number(/\.cockpit-tabbar\s*\{[^}]*?z-index:\s*(\d+)/.exec(shell)?.[1]);
		expect(tabbarZ, '.cockpit-tabbar must declare a z-index').not.toBeNaN();

		const flags = new Set<string>();
		for (const file of cssFiles) {
			for (const m of read(file).matchAll(/body\.([a-z0-9-]*(?:fullscreen|replay)[a-z0-9-]*)/g)) {
				const flag = m[1];
				if (flag) flags.add(flag);
			}
		}
		expect(flags.size, 'no full-viewport body flag found — have they been renamed?').toBeGreaterThan(0);

		const all = cssFiles.map(read).join('\n');
		const offenders: string[] = [];
		for (const flag of flags) {
			const hidden = new RegExp(
				`body\\.${flag}[^{]*\\.cockpit-tabbar[^{]*\\{[^}]*display:\\s*none`,
			).test(all);
			const liftedZ = new RegExp(
				`body\\.${flag}[^{]*\\.cockpit-main[^{]*\\{[^}]*z-index:\\s*(\\d+)`,
			).exec(all)?.[1];
			if (!hidden && Number(liftedZ ?? 0) <= tabbarZ) offenders.push(flag);
		}
		expect(
			offenders,
			`these body flags leave the mobile tabbar painted over the overlay:\n${offenders.join('\n')}`,
		).toEqual([]);
	});
});
