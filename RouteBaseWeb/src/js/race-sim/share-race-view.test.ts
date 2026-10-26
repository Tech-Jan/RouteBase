import { existsSync, readFileSync } from 'node:fs';
import { must } from '../core/invariant';
import { resolve } from 'node:path';
import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest';
import type { RaceReplayViewOptions } from './race-replay-view';
import type { ShareRaceReplayPayload } from '../generated/contracts';

/**
 * The anonymous race fork — pinned as a MOUNT LIST, which is the architectural investment race sharing was
 * built around.
 *
 * The rule: everything the visitor sees comes from race-sim/race-replay-view (the same module the owner's
 * page mounts), at editable:false, over the share payload. So there are exactly three things worth
 * asserting here, and they are the three that would silently rot:
 *
 *   1. it mounts the SHARED view, not a copy of it;
 *   2. it mounts it read-only, and reads from the token-scoped base in the seed island;
 *   3. it pulls in NO owner-edit module — the fork's isolation is "the code isn't there", not "a flag is
 *      false", and that is the property that keeps a future owner-side control from leaking onto a public
 *      link by accident.
 */

const SEED = { raceId: 42, apiBase: '/s/tok/api/race' };

/**
 * A repo path, resolved whether vitest was started from RouteBaseWeb (npm run test) or from the solution
 * root (a CI one-liner). NOT import.meta.url: vite rewrites it to a non-file URL under transform.
 */
function sourcePath(relativeToWeb: string): string {
	for (const base of ['.', 'RouteBaseWeb', '..']) {
		const candidate = resolve(process.cwd(), base, relativeToWeb);
		if (existsSync(candidate)) return candidate;
	}
	throw new Error(`source not found from ${process.cwd()}: ${relativeToWeb}`);
}

let viewOptions: RaceReplayViewOptions | null = null;
const destroy = vi.fn();

vi.mock('./race-replay-view', () => ({
	createRaceReplayView: (options: RaceReplayViewOptions) => {
		viewOptions = options;
		return { checkpoints: () => [], destroy };
	},
}));

const fetchShareRaceReplay = vi.fn(async (_apiBase: string) => ({ raceId: 42 }));
vi.mock('./race-api', () => ({ fetchShareRaceReplay: (base: string) => fetchShareRaceReplay(base) }));

function mountPage(seed: unknown = SEED): void {
	document.body.innerHTML =
		'<div id="shareRaceApp">'
		+ `<script type="application/json" id="share-race-data">${JSON.stringify(seed)}</script>`
		+ '</div>';
}

beforeEach(() => {
	viewOptions = null;
	destroy.mockClear();
	fetchShareRaceReplay.mockClear();
	vi.resetModules();
});

afterEach(() => {
	document.body.innerHTML = '';
});

describe('share-race-view — the anonymous fork', () => {
	it('mounts the shared replay view read-only, seeded from the island', async () => {
		mountPage();
		const { initShareRaceView } = await import('./share-race-view');
		initShareRaceView();

		expect(viewOptions).not.toBeNull();
		expect(viewOptions!.raceId).toBe(42);
		// THE flag. Editable would give an anonymous visitor the profile's click-to-place and a paint that
		// WRITES — both of which reach the DB. The paint controls themselves render either way; false makes
		// them client-only (race-results-table's PaintMode).
		expect(viewOptions!.editable).toBe(false);
		// No basemap option any more: the seed stopped carrying one and race-replay-view reads the
		// account-settings seam, which a share page fills with the global defaults. The "it opens on a
		// keyless basemap" pin moved to race-replay-view.test.ts, which is where the resolution now lives.
	});

	it('reads the replay from the token-scoped base, never the owner endpoint', async () => {
		mountPage();
		const { initShareRaceView } = await import('./share-race-view');
		initShareRaceView();

		await viewOptions!.loadPayload();

		expect(fetchShareRaceReplay).toHaveBeenCalledWith('/s/tok/api/race');
	});

	it('does nothing at all when its root is absent', async () => {
		// The entry is bundled per-page, but a shared bundle or a markup change must not have it grab the
		// owner page's elements — every id it mounts is deliberately the SAME as the owner page's.
		document.body.innerHTML = '<div id="raceReplay"></div>';
		const { initShareRaceView } = await import('./share-race-view');

		expect(initShareRaceView()).toBeUndefined();
		expect(viewOptions).toBeNull();
	});

	it('imports no owner-edit module', () => {
		// The isolation that makes the fork safe is structural: an owner control cannot render on a page
		// whose bundle does not contain it. This is the test that fails the day someone "just adds" the
		// riders panel here because it was convenient.
		const source = readFileSync(sourcePath('src/js/race-sim/share-race-view.ts'), 'utf8');

		for (const ownerOnly of [
			'race-import-panel', 'race-riders-panel', 'race-settings-panel', 'race-clear-field',
			'race-checkpoint-editor', 'share-links',
		]) {
			expect(source).not.toContain(ownerOnly);
		}

		// Anti-vacuity, and the positive half of the rule: it DOES mount the shared view.
		expect(source).toContain('race-replay-view');
		expect(source).toContain('fetchShareRaceReplay');
	});
});

describe('the share payload the fork consumes', () => {
	it('renders the event\'s own start list — entrants are not anonymized (2026-07-24)', async () => {
		// The adapter is the only place the two shapes meet, so this is where "what a visitor reads" is
		// observable on the TS side. Entrant identity travels; what the SERVER withholds is other people's
		// geometry, which is a rule about participants, not about columns (ShareRacePayloadRedactionTests).
		const { toReplayPayload } = await vi.importActual<typeof import('./race-api')>(
			'./race-api');

		const shared = {
			raceId: 1,
			raceName: 'Ski to Sea',
			course: { lat: [], lng: [], elevation: [], gradePercent: [], stepMeters: 10, totalMeters: 100 },
			checkpoints: [],
			costCurve: null,
			costCurveMinBin: 0,
			participants: [
				{
					participantId: 5, kind: 'simulated', seed: 0,
					name: 'Ada Lovelace', bib: '4711', category: 'W40', gender: 'F', club: 'Bletchley Wheelers',
					color: null, officialFinishSeconds: 3600, splits: [], track: null,
				},
			],
		} as unknown as ShareRaceReplayPayload;

		const payload = toReplayPayload(shared);
		const rider = must(payload.participants[0], 'one rider');

		expect(rider.name).toBe('Ada Lovelace');
		expect(rider.bib).toBe('4711');
		expect(rider.category).toBe('W40');
		expect(rider.gender).toBe('F');
		expect(rider.club).toBe('Bletchley Wheelers');
		// The raceable facts survive — an adapter that dropped them would pass every assertion above.
		expect(rider.officialFinishSeconds).toBe(3600);
		expect(payload.raceName).toBe('Ski to Sea');
	});
});
