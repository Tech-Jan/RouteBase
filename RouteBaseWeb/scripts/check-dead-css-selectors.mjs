#!/usr/bin/env node
/**
 * check-dead-css-selectors.mjs — fail if a class selector defined in wwwroot/css/*.css
 * is referenced NOWHERE in the markup/code (.cshtml / .cs / .ts).
 *
 * Part of the CSS pipeline. Vite never touches CSS (no imports, no
 * tree-shaking), so dead selectors accreted silently — one pass found 143 dead lines.
 * This is the ratchet that stops that: run it alongside `npm run lint`.
 *
 * It searches the STEM, not the full class name. A class like `share-badge--past` is
 * assembled in code as `"share-badge--" + state`, so only the base `share-badge` ever
 * appears literally (this was a real false-positive delete candidate). Stem = the class
 * up to its first BEM separator (`--` or `__`). A stem found as a substring anywhere in
 * the corpus counts as alive: false-ALIVES are harmless, a false-DEAD would wrongly fail
 * the build, so the check deliberately biases to leniency.
 *
 * Legitimately-dynamic or third-party-runtime names whose stem still never appears in the
 * corpus go in ALLOWLIST below — each with a dated reason. To add one: append its stem.
 */
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, extname } from 'node:path';

const ROOT = process.cwd(); // RouteBaseWeb (npm script cwd)
const CSS_DIR = join(ROOT, 'wwwroot', 'css');
const CODE_EXTS = new Set(['.cshtml', '.cs', '.ts']);
const SKIP_DIRS = new Set(['node_modules', 'bin', 'obj', '.git', 'lib', 'dist']);

// Classes whose stem is a prefix of a third-party runtime's own class names: the
// library adds them to the DOM at runtime (it lives in node_modules, which the corpus
// skips) and our CSS only themes them, so they never appear as a literal in our code.
const THIRD_PARTY_PREFIXES = ['maplibregl-']; // MapLibre GL popup/ctrl DOM — themed in cockpit-components.css. 2026-07-30.

// Class stems referenced only dynamically / indirectly, or grandfathered pre-existing
// dead. Keep each justified + dated so the list stays meaningful.
const ALLOWLIST = new Set([
	// ── Design-system primitives + utilities (opt-in API; referenced by selector-grouping
	//    or reserved for new components, so not present in today's markup). 2026-07-30. ──
	'card-cockpit', // the canonical card primitive; .seg-card/.race-card/.activity-card group onto it
	'toolbar-cockpit', // the canonical map-toolbar primitive; .act/.det/.seg-map-toolbar group onto it
	'focus-ring', // accessibility utility, opt-in
	'hairline', // hairline-border utility, opt-in
	'hairline-t',

	// ── GRANDFATHERED: pre-existing dead selectors the check found on install
	//    (2026-07-30). Defined in wwwroot/css but in no .cshtml/.cs/.ts. Left as a ratchet
	//    baseline — NOT endorsed as correct. Each is a CLEANUP CANDIDATE: verify in a browser
	//    (the map/replay ones especially) and delete the rule, then drop it from this list.
	//    Do not add new entries here to silence a fresh finding —
	//    a new dead selector means delete it, not grandfather it. ──
	'act-col-picker-wrap', 'act-dd-end', 'act-summary-bar',
	'cum-chip-dot', 'cumulative-daterange', 'custom-marker',
	'det-map-btn-solo', 'det-panel', 'det-panel-date',
	'feed-filters', 'map-label', 'marker-finished', 'marker-highlighted',
	'replay-loading-overlay', // .replay-loading-overlay .spinner-border, segment-replay.css
	// (replay-loading-spinner / replay-loading-text deleted from replay-controls.css — the whole
	//  dead "Loading helpers" block, whose bare `.replay-loading` false-alived off `.replay-loading-status`)
	'seg-date', 'seg-date-label', 'seg-replay-marker', 'seg-select',
	'seg-table-header-actions', 'segment-trend', 'sidebar-spacer',
	'track-edit-banner', 'track-edit-banner-actions', 'version-row-error',
	'widget-big-num', 'widget-meta',
]);

function walk(dir, out) {
	for (const name of readdirSync(dir)) {
		if (SKIP_DIRS.has(name)) continue;
		const p = join(dir, name);
		if (statSync(p).isDirectory()) walk(p, out);
		else if (CODE_EXTS.has(extname(name))) out.push(p);
	}
}

const codeFiles = [];
walk(ROOT, codeFiles);
const corpus = codeFiles.map((f) => readFileSync(f, 'utf8')).join('\n');

const cssFiles = readdirSync(CSS_DIR).filter((f) => f.endsWith('.css'));
const classes = new Set();
for (const f of cssFiles) {
	const css = readFileSync(join(CSS_DIR, f), 'utf8').replace(/\/\*[\s\S]*?\*\//g, '');
	// Selector text = everything before each `{` (values live inside braces, so this
	// never mistakes a property value's dot for a class).
	for (const m of css.matchAll(/([^{}]+)\{/g)) {
		for (const c of m[1].matchAll(/\.(-?[A-Za-z_][\w-]*)/g)) classes.add(c[1]);
	}
}

function stem(cls) {
	const dashDash = cls.indexOf('--');
	const underUnder = cls.indexOf('__');
	const cut = Math.min(dashDash < 0 ? Infinity : dashDash, underUnder < 0 ? Infinity : underUnder);
	return cut === Infinity ? cls : cls.slice(0, cut);
}

const dead = [];
for (const cls of [...classes].sort()) {
	const s = stem(cls);
	if (corpus.includes(s)) continue;
	if (THIRD_PARTY_PREFIXES.some((p) => cls.startsWith(p))) continue;
	if (ALLOWLIST.has(cls) || ALLOWLIST.has(s)) continue;
	dead.push(cls);
}

if (dead.length) {
	console.error('Dead CSS class selectors (defined in wwwroot/css, referenced in no .cshtml/.cs/.ts):');
	for (const d of dead) console.error(`  .${d}   (searched stem "${stem(d)}")`);
	console.error('\nRemove the rule, or if the name is assembled dynamically add its stem to');
	console.error('ALLOWLIST in scripts/check-dead-css-selectors.mjs (with a dated reason).');
	process.exit(1);
}
console.log(`check-dead-css-selectors: ${classes.size} class selectors, all referenced. OK`);
