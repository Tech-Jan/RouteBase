// @vitest-environment node
import { must } from './invariant';
import { describe, expect, it } from 'vitest';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';

/**
 * The core/ layering gate — core/ is the BOTTOM layer and never imports upward.
 *
 * Two rules, both vitest parity gates for eslint.config.mjs (mirroring LayeringTests.cs /
 * LayeringTest.kt) and independent of eslint, so they run in gate 3:
 *   1. core/ imports nothing from maplibre/          (AR-02)
 *   2. core/ imports nothing from a feature directory (AR-01)
 *
 * Rule 2 derives the feature list from the FILESYSTEM rather than a hand-written list. The
 * eslint rule needs literal names (static config), but this test does not — so a feature
 * directory added next month is guarded the moment it exists, with nobody remembering to
 * update anything. That inverts the maintenance burden: a 4-entry allowlist of lower layers
 * that does not grow, instead of a 13-entry denylist of features that does.
 *
 * ---
 *
 * core/ imports NOTHING from maplibre/ (AR-02).
 *
 * core/ is the bottom layer. After ResetFraming + CameraControlsInstance moved down into
 * core-types.ts, no core module points at the maplibre/ directory. This pins that: a fresh
 * `from '../maplibre/…'` (or dynamic `import('../maplibre/…')`) in any non-test core module
 * fails here — the vitest parity gate for the eslint `no-restricted-imports` maplibre rule
 * (mirrors LayeringTests.cs / LayeringTest.kt), and independent of eslint (runs in gate 3).
 *
 * Test files are exempt by design: replay-follow-camera.test.ts imports the runtime
 * CameraControls class from maplibre/ to drive the follow — test-only wiring, the same
 * exemption eslint.config.mjs grants (`**\/*.test.ts`). So the walk skips *.test.ts.
 *
 * NOTE: the maplibre-gl NPM PACKAGE (`import("maplibre-gl")`, `from 'maplibre-gl'`) is the
 * global map type, not the local maplibre/ dir — its specifier has no `maplibre/` slash, so the
 * regex below never flags it. core/ is a MapLibre-typed kernel on purpose (ambient types in ~12
 * modules); this gate is about the local directory boundary only.
 */

// import.meta.url → .../src/js/core/core-layering.test.ts; one `..` pops the filename → src/js/core.
const coreRoot = path.resolve(fileURLToPath(import.meta.url), '..');
const jsRoot = path.resolve(coreRoot, '..'); // src/js

/**
 * Everything under src/js/ that is NOT a lower layer is a feature directory, discovered at
 * run time so a newly-added feature needs no edit here.
 *
 * `maplibre/` is excluded on purpose — it is the map ENGINE, not a feature, and it has its own
 * rule above (which allows type-only imports; this one allows none). Keeping it out avoids two
 * gates disagreeing about the same edge.
 */
const LOWER_LAYERS = new Set(['core', 'generated', 'generated-helpers', 'types']);
function collectFeatureDirs(): string[] {
	return fs
		.readdirSync(jsRoot, { withFileTypes: true })
		.filter((e) => e.isDirectory() && !LOWER_LAYERS.has(e.name) && e.name !== 'maplibre')
		.map((e) => e.name)
		.sort();
}

/** Non-test .ts files under core/ (recurses into grid/), as absolute paths. */
function collectCoreSourceFiles(dir: string): string[] {
	const out: string[] = [];
	for (const ent of fs.readdirSync(dir, { withFileTypes: true })) {
		const abs = path.join(dir, ent.name);
		if (ent.isDirectory()) {
			out.push(...collectCoreSourceFiles(abs));
		} else if (ent.name.endsWith('.ts') && !ent.name.endsWith('.test.ts') && !ent.name.endsWith('.d.ts')) {
			out.push(abs);
		}
	}
	return out;
}

/**
 * Every module specifier in `src`: `import … from '…'` / `export … from '…'`, side-effect
 * `import '…'`, and dynamic `import('…')`. No comment stripping — a false positive would only
 * make the gate STRICTER, never let a real maplibre/ edge through.
 */
function extractSpecifiers(src: string): string[] {
	const specs: string[] = [];
	const patterns = [
		/\bfrom\s*['"]([^'"]+)['"]/g,             // import … from '…' / export … from '…'
		/\bimport\s*\(\s*['"]([^'"]+)['"]\s*\)/g, // dynamic import('…')
		/\bimport\s+['"]([^'"]+)['"]/g,           // side-effect import '…'
	];
	for (const re of patterns) {
		let m: RegExpExecArray | null;
		while ((m = re.exec(src)) !== null) specs.push(must(m[1], 'capture group 1'));
	}
	return specs;
}

describe('core/ layering', () => {
	it('core/ imports nothing from maplibre/', () => {
		const files = collectCoreSourceFiles(coreRoot);
		// Vacuity guard: a broken walk (wrong root, empty read) would let every assertion below pass
		// hollowly. core/ has ~94 non-test modules today (76 root + 18 grid/).
		expect(files.length, 'core source-file walk found suspiciously few files — the walk is broken').toBeGreaterThan(50);

		const offenders: string[] = [];
		for (const abs of files) {
			const src = fs.readFileSync(abs, 'utf8');
			for (const spec of extractSpecifiers(src)) {
				if (/maplibre\//.test(spec)) offenders.push(`${path.relative(coreRoot, abs)} → '${spec}'`);
			}
		}

		expect(
			offenders,
			`core/ is the bottom layer — it must not import from maplibre/. Move the shared type DOWN into core (see AR-02). Offending imports:\n${offenders.join('\n')}`,
		).toEqual([]);
	});

	it('core/ imports nothing from a feature directory', () => {
		const features = collectFeatureDirs();
		// Vacuity guard: a broken readdir would yield an empty feature list and pass hollowly.
		// 13 feature dirs today (18 under src/js, minus the 4 lower layers and maplibre/).
		expect(
			features.length,
			'feature-directory scan found suspiciously few dirs — the scan is broken, so this gate is vacuous',
		).toBeGreaterThan(8);

		const files = collectCoreSourceFiles(coreRoot);
		expect(files.length, 'core source-file walk found suspiciously few files — the walk is broken').toBeGreaterThan(50);

		// A specifier points at a feature when the dir name is a whole path segment:
		// '../activity/x' and '../../overview/y' (from core/grid/) match; the npm package
		// 'chart.js' and core's own './core-types' do not.
		const featureRe = new RegExp(`(^|/)(${features.join('|')})/`);

		const offenders: string[] = [];
		for (const abs of files) {
			const src = fs.readFileSync(abs, 'utf8');
			for (const spec of extractSpecifiers(src)) {
				if (featureRe.test(spec)) offenders.push(`${path.relative(coreRoot, abs)} → '${spec}'`);
			}
		}

		expect(
			offenders,
			`core/ is the bottom layer — it must not import UP into a feature directory (AR-01). This is the ` +
				`core/ → pages/ inversion that once had to be fixed; it can silently recur. If a feature needs to share code ` +
				`with core, move that code DOWN into core/ or generated/. Offending imports:\n${offenders.join('\n')}`,
		).toEqual([]);
	});
});
