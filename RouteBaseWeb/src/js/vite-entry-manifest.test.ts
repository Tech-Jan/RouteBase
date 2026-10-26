// @vitest-environment node
import { describe, expect, it } from 'vitest';
import * as fs from 'node:fs';
import { esmEntries } from '../../vite.config.esm';

/**
 * Vite entry-manifest pin — the one automated guard on the "silent dropped entry" trap.
 *
 * `entry(name, srcRel)` in vite.config.esm.ts returns `{}` when `srcRel` does not exist
 * (`existsSync(abs) ? {…} : {}`). Move or rename a source file without updating its `srcRel` and
 * that entry VANISHES from the build with no error: the page's `<script src>` 404s at runtime while
 * `npm run build`, `tsc`, and `eslint` all stay green. Nothing else catches it.
 *
 * This test pins the full set of page entries. A dropped entry fails as a missing key; an added or
 * renamed one fails as an unexpected/missing key — both are deliberate one-line updates HERE. This is
 * the manifest the old SCRIPT-LOADING.md tried to be, made un-driftable: it is the entries themselves.
 *
 * When you legitimately add/rename/remove an entry, update EXPECTED_ENTRIES in the same commit.
 */
const EXPECTED_ENTRIES: readonly string[] = [
	'core/core',
	'core/op-toast',
	'pages/profile-settings',
	'live-recording/live-recording',
	'live-recording/share-live-view',
	'live-recording/record-review',
	'dashboard/dashboard',
	'pages/statistics',
	'dashboard/dashboard-nav',
	'pages/segments-index',
	'pages/sharing-inventory',
	'pages/keys-and-data',
	'segment-create/segment-create',
	'pages/friends',
	'pages/multiday',
	'import/import',
	'photos/photos',
	'trash/trash',
	'race-sim/races',
	'race-sim/races-grid',
	'race-sim/race-replay',
	'race-sim/share-race-view',
	'activity/activity-actions',
	'segment-stats/segment-stats',
	'segment-stats/segment-stats-efforts-table',
	'activity/activity-edit',
	'activity/activity-details-page',
	'activity/version-history',
	'overview/activities-page-init',
	'overview/activities-grid',
	'overview/segments-grid',
	'overview/shape-filter',
	'track-edit/entry-picker',
];

describe('vite entry manifest', () => {
	const actual = Object.keys(esmEntries);

	it('holds exactly the expected page entries — no silent drops, no stray additions', () => {
		const missing = EXPECTED_ENTRIES.filter(k => !actual.includes(k));
		const unexpected = actual.filter(k => !EXPECTED_ENTRIES.includes(k));
		// `missing` = a source file moved/renamed and its entry vanished (the trap), OR an entry was
		// intentionally removed. `unexpected` = an entry was added/renamed. Update EXPECTED_ENTRIES.
		expect({ missing, unexpected }).toEqual({ missing: [], unexpected: [] });
	});

	it('has 33 entries (the pinned count the C# build globs against)', () => {
		expect(actual.length).toBe(33);
	});

	it('every declared entry points at a source file that exists on disk', () => {
		// entry() already filters non-existent files out, so anything that survived into esmEntries
		// resolves — this asserts the invariant explicitly and fails loudly if it ever regresses.
		const dangling = Object.entries(esmEntries).filter(([, abs]) => !fs.existsSync(abs));
		expect(dangling).toEqual([]);
	});
});
