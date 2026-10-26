// @vitest-environment node
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { must } from '../core/invariant';

/**
 * The signed-in and share routes differ in URL, HTTP verb AND response shape. This module is the only
 * place that knows that, so this is the only place it can be pinned.
 *
 * Written after a real miss: the module shipped with the signed-in transport only, so a shared segment
 * rendered its efforts table (server-side) but the map and charts failed with "Failed to fetch" — the
 * loaders were POSTing to endpoints that exist only on the authenticated route.
 */

const configRef: { current: Record<string, unknown> } = { current: {} };

vi.mock('./segment-statistics', () => ({
	get ctx() {
		return { config: configRef.current };
	},
}));

const SHARE_BASE = '/s/tok123/api/segment';

function jsonResponse(body: unknown, ok = true, status = 200): Response {
	return { ok, status, json: () => Promise.resolve(body) } as Response;
}

describe('segment-stats-api — route-dependent transport', () => {
	let fetchSpy: ReturnType<typeof vi.fn>;

	beforeEach(() => {
		fetchSpy = vi.fn();
		vi.stubGlobal('fetch', fetchSpy);
	});

	afterEach(() => {
		vi.unstubAllGlobals();
		vi.resetModules();
	});

	// ---- signed-in route ----

	it('signed-in: POSTs an id list to bulk-tracks', async () => {
		configRef.current = { segmentId: 7 };   // no apiBase → the authenticated default
		const api = await import('./segment-stats-api');
		fetchSpy.mockResolvedValue(jsonResponse({}));

		await api.fetchTrackChunk([1, 2], 0);

		const [url, init] = must(fetchSpy.mock.calls[0], 'one fetch call');
		expect(url).toBe('/api/SegmentsApi/activities/bulk-tracks');
		expect((init as RequestInit).method).toBe('POST');
		expect(api.isShareRoute()).toBe(false);
	});

	it('signed-in: POSTs performance-data/bulk', async () => {
		configRef.current = { segmentId: 7 };
		const api = await import('./segment-stats-api');
		fetchSpy.mockResolvedValue(jsonResponse([]));

		await api.fetchPerfChunk(7, [1, 2], 0);

		const [url, init] = must(fetchSpy.mock.calls[0], 'one fetch call');
		expect(url).toBe('/api/SegmentsApi/7/performance-data/bulk');
		expect((init as RequestInit).method).toBe('POST');
	});

	// ---- share route ----

	it('share: GETs tracks by PAGE and sends no ids and no body', async () => {
		// The server derives the permitted set from the grant; sending ids would be meaningless and the
		// share surface is GET-only.
		configRef.current = { segmentId: 7, apiBase: SHARE_BASE };
		const api = await import('./segment-stats-api');
		fetchSpy.mockResolvedValue(jsonResponse({ tracks: [{ trackId: 4, points: [{ lat: 1, lng: 2 }] }], total: 1 }));

		const page = await api.fetchTracksPage(0, 150, 0);

		const [url, init] = must(fetchSpy.mock.calls[0], 'one fetch call');
		expect(url).toBe(`${SHARE_BASE}/tracks?skip=0&take=150&maxPoints=0`);
		expect(init).toBeUndefined();                 // a plain GET — no method, no body
		expect(String(url)).not.toContain('trackIds');
		// ...and the {tracks:[{trackId,points}]} wire form is normalised to the dictionary the loaders use.
		expect(page.tracks).toEqual({ '4': [{ lat: 1, lng: 2 }] });
		expect(page.total).toBe(1);
	});

	it('share: GETs performance with a query string and unwraps {efforts}', async () => {
		configRef.current = { segmentId: 7, apiBase: SHARE_BASE };
		const api = await import('./segment-stats-api');
		fetchSpy.mockResolvedValue(jsonResponse({ efforts: [{ effortId: 9 }] }));

		const wire = await api.fetchPerfChunk(7, [9, 10], 500);

		const [url, init] = must(fetchSpy.mock.calls[0], 'one fetch call');
		expect(url).toBe(`${SHARE_BASE}/performance?effortIds=9,10&maxPoints=500`);
		expect(init).toBeUndefined();
		expect(wire).toEqual([{ effortId: 9 }]);      // NOT the wrapper object
	});

	it('share: single track unwraps {trackId, points}', async () => {
		configRef.current = { segmentId: 7, apiBase: SHARE_BASE };
		const api = await import('./segment-stats-api');
		fetchSpy.mockResolvedValue(jsonResponse({ trackId: 4, points: [{ lat: 1, lng: 2 }] }));

		const points = await api.fetchSingleTrack(4, 0);

		expect(must(fetchSpy.mock.calls[0], 'one fetch call')[0]).toBe(`${SHARE_BASE}/tracks/4`);
		expect(points).toEqual([{ lat: 1, lng: 2 }]);
	});

	it('share: a refused track is null, not a throw', async () => {
		// 404 is the grant declining a track it does not cover — indistinguishable from an unknown id by
		// design, so the map skips it silently rather than surfacing an error.
		configRef.current = { segmentId: 7, apiBase: SHARE_BASE };
		const api = await import('./segment-stats-api');
		fetchSpy.mockResolvedValue(jsonResponse(null, false, 404));

		expect(await api.fetchSingleTrack(999, 0)).toBeNull();
	});

	it('share: never emits an /api/SegmentsApi URL', async () => {
		// The regression that caused the original miss: a literal base leaking through on the share route.
		configRef.current = { segmentId: 7, apiBase: SHARE_BASE };
		const api = await import('./segment-stats-api');
		fetchSpy.mockResolvedValue(jsonResponse({ tracks: [], total: 0, efforts: [] }));

		await api.fetchTracksPage(0, 10, 0);
		await api.fetchPerfChunk(7, [1], 0);
		await api.fetchSingleTrack(1, 0);

		expect(fetchSpy).toHaveBeenCalledTimes(3);     // anti-vacuity
		for (const [url] of fetchSpy.mock.calls) {
			expect(String(url)).not.toContain('/api/SegmentsApi');
			expect(String(url)).toContain(SHARE_BASE);
		}
	});
});
