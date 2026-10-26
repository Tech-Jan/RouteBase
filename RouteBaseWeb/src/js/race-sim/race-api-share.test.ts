// @vitest-environment node
import { describe, expect, it, vi, afterEach } from 'vitest';
import { must } from '../core/invariant';
import { fetchShareRaceReplay } from './race-api';

/**
 * The anonymous read path: `GET /s/{token}/api/race/replay`.
 *
 * Two things differ from the owner's fetchRaceReplay and both are deliberate:
 *   - the base comes from the caller (the seed island), because the token is in the URL;
 *   - a failure says one thing for every cause, because a share miss MUST be uniform — revoked, expired,
 *     wrong URL and rate-limited are all 404 by design, and a message that distinguished them
 *     would hand a prober the oracle the whole slice avoids.
 */
describe('fetchShareRaceReplay', () => {
	function respondWith(body: string, init: ResponseInit = { status: 200 }): void {
		vi.stubGlobal('fetch', vi.fn(async () =>
			new Response(body, { headers: { 'Content-Type': 'application/json' }, ...init })));
	}

	function sharedPayload(participants: unknown[] = []): string {
		return JSON.stringify({
			raceId: 7,
			raceName: 'Ski to Sea',
			course: { lat: [], lng: [], elevation: [], gradePercent: [], stepMeters: 10, totalMeters: 100 },
			checkpoints: [],
			costCurve: null,
			costCurveMinBin: 0,
			participants,
		});
	}

	afterEach(() => vi.unstubAllGlobals());

	it('reads from the token-scoped base it is given', async () => {
		respondWith(sharedPayload());

		await fetchShareRaceReplay('/s/abc/api/race');

		expect(fetch).toHaveBeenCalledWith('/s/abc/api/race/replay', expect.anything());
	});

	it('adapts the share payload into the shape every race-sim module speaks', async () => {
		respondWith(sharedPayload([
			{
				participantId: 5, kind: 'real', seed: 1, color: '#ff0000',
				name: 'Ada Lovelace', bib: '4711', category: 'W40', gender: 'F', club: 'Bletchley Wheelers',
				officialFinishSeconds: 3600, splits: [{ checkpointId: 1, elapsedSeconds: 60 }],
				track: { lat: [1], lng: [2], elapsedSeconds: [0], chainageMeters: [0] },
			},
		]));

		const payload = await fetchShareRaceReplay('/s/abc/api/race');
		const rider = must(payload.participants[0], 'one rider');

		expect(payload.raceId).toBe(7);
		// The results columns come through as they are — the server publishes the event's own start list,
		// so the share table filters, sorts and paints on exactly what the owner's does.
		expect(rider.name).toBe('Ada Lovelace');
		expect(rider.bib).toBe('4711');
		expect(rider.category).toBe('W40');
		expect(rider.gender).toBe('F');
		expect(rider.club).toBe('Bletchley Wheelers');
		// Everything the replay actually runs on survives the adaptation.
		expect(rider.track?.lat).toEqual([1]);
		expect(rider.splits).toEqual([{ checkpointId: 1, elapsedSeconds: 60 }]);
		expect(rider.color).toBe('#ff0000');
	});

	it('an unnamed entrant stays unnamed — null is what the results table filters on', async () => {
		respondWith(sharedPayload([
			{
				participantId: 6, kind: 'simulated', seed: 2, color: null,
				name: null, bib: null, category: null, gender: null, club: null,
				officialFinishSeconds: null, splits: [], track: null,
			},
		]));

		const rider = must((await fetchShareRaceReplay('/s/abc/api/race')).participants[0], 'one rider');

		expect(rider.name).toBeNull();
		expect(rider.bib).toBeNull();
	});

	it('reports any failure as one indistinguishable "link unavailable"', async () => {
		respondWith('', { status: 404 });

		// Deliberately NOT the owner path's `Race 7 replay failed: 404`: on this surface the status IS the
		// design (revoked, expired, unknown and throttled are all 404), so echoing it would say nothing
		// true and reveal the shape.
		await expect(fetchShareRaceReplay('/s/abc/api/race')).rejects.toThrow(/no longer available/i);
	});

	it('still names a truncated body as a server fault', async () => {
		// The non-finite-double failure mode is not owner-only — the redacted payload carries the same
		// split times, and on THIS page there is no signed-in fallback to fall back to.
		respondWith('{"raceId":7,"participants":[{"splits":[{"elapsedSeconds":');

		await expect(fetchShareRaceReplay('/s/abc/api/race')).rejects.toThrow(/incomplete/i);
	});
});
