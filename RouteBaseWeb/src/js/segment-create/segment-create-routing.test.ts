// @vitest-environment node
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { must } from '../core/invariant';
import { fetchRoute } from './segment-create-routing';

// SC-01 characterization pins for the 2-provider routing fallback chain (BRouter → OSRM; the keyless
// ORS/GraphHopper hops were removed 2026-10-26).
// fetch is stubbed per test; console.warn silenced (the chain logs per failure).

const start = { lat: 47.0, lng: 7.0 };
const end = { lat: 47.1, lng: 7.1 };

beforeEach(() => {
	vi.spyOn(console, 'warn').mockImplementation(() => {});
});

afterEach(() => {
	vi.restoreAllMocks();
	vi.unstubAllGlobals();
});

const jsonResponse = (body: unknown) => ({ json: () => Promise.resolve(body) });

describe('segment-create routing — SC-01 pins', () => {
	it('returns flipped [lat,lng] from BRouter when the first provider succeeds', async () => {
		const fetchMock = vi.fn().mockResolvedValue(
			jsonResponse({ features: [{ geometry: { coordinates: [[7.0, 47.0], [7.05, 47.05]] } }] }),
		);
		vi.stubGlobal('fetch', fetchMock);

		const res = await fetchRoute(start, end);
		expect(res).toEqual([[47.0, 7.0], [47.05, 7.05]]);
		expect(fetchMock).toHaveBeenCalledTimes(1);
		expect(must(fetchMock.mock.calls[0], 'first routing call')[0]).toContain('brouter.de');
	});

	it('falls through BRouter → OSRM in order', async () => {
		const fetchMock = vi.fn((url: string) => {
			if (url.includes('brouter.de')) return Promise.reject(new Error('down'));
			if (url.includes('router.project-osrm.org')) {
				return Promise.resolve(
					jsonResponse({ code: 'Ok', routes: [{ geometry: { coordinates: [[7.2, 47.2]] } }] }),
				);
			}
			return Promise.reject(new Error('unexpected url'));
		});
		vi.stubGlobal('fetch', fetchMock);

		const res = await fetchRoute(start, end);
		expect(res).toEqual([[47.2, 7.2]]);

		const urls = fetchMock.mock.calls.map((c) => c[0] as string);
		expect(urls).toHaveLength(2);
		expect(urls[0]).toContain('brouter.de');
		expect(urls[1]).toContain('router.project-osrm.org');
	});

	it('falls through to OSRM when BRouter answers with no route (shape-miss)', async () => {
		const fetchMock = vi.fn((url: string) =>
			url.includes('brouter.de')
				? Promise.resolve(jsonResponse({}))
				: Promise.resolve(jsonResponse({ code: 'Ok', routes: [{ geometry: { coordinates: [[7.3, 47.3]] } }] })),
		);
		vi.stubGlobal('fetch', fetchMock);

		await expect(fetchRoute(start, end)).resolves.toEqual([[47.3, 7.3]]);
		expect(fetchMock).toHaveBeenCalledTimes(2);
	});

	it('returns null when all providers fail', async () => {
		const fetchMock = vi.fn().mockRejectedValue(new Error('offline'));
		vi.stubGlobal('fetch', fetchMock);

		await expect(fetchRoute(start, end)).resolves.toBeNull();
		expect(fetchMock).toHaveBeenCalledTimes(2);
	});
});
