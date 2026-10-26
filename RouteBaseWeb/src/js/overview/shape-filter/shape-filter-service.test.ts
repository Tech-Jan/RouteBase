import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { must } from '../../core/invariant';

// The service pulls the owner filter off the grid bridge; stub it so the module loads headless.
vi.mock('../overview-grid-bridge', () => ({
	getActivitiesOwnerFilter: () => null,
}));

import { ShapeFilterService, type PreviewResult } from './shape-filter-service';
import { ShapeStore } from './shape-store';
import { zoneFromBounds } from './shape-model';

interface Deferred<T> {
	promise: Promise<T>;
	resolve: (v: T) => void;
}
function deferred<T>(): Deferred<T> {
	let resolve!: (v: T) => void;
	const promise = new Promise<T>((r) => { resolve = r; });
	return { promise, resolve };
}

// Just the Response surface ShapeFilterService.post reads.
type FakeResponse = { ok: boolean; status?: number; json: () => Promise<unknown>; text?: () => Promise<string> };

describe('ShapeFilterService preview (two-phase, abort, errors)', () => {
	let store: ShapeStore;
	let results: PreviewResult[];
	let service: ShapeFilterService;
	const fetches: Array<Deferred<FakeResponse>> = [];

	// Flush the fetch→json→onResult microtask chain (and any follow-on phase-2 fetch) under fake timers.
	const settle = async () => {
		await vi.advanceTimersByTimeAsync(0);
		await Promise.resolve();
		await Promise.resolve();
	};

	beforeEach(() => {
		vi.useFakeTimers();
		fetches.length = 0;
		vi.stubGlobal('fetch', vi.fn(() => {
			const d = deferred<FakeResponse>();
			fetches.push(d);
			return d.promise;
		}));
		store = new ShapeStore();
		results = [];
		service = new ShapeFilterService(store, () => 'whole', () => null, () => 15, (r) => results.push(r));
		service.start();
	});

	afterEach(() => {
		service.stop();
		vi.unstubAllGlobals();
		vi.useRealTimers();
	});

	it('emits the fast bbox estimate, then the exact refinement (two-phase)', async () => {
		store.add(zoneFromBounds({ south: 0, west: 0, north: 1, east: 1 }));
		await vi.advanceTimersByTimeAsync(300); // debounce → phase 1 (fast) fetch begins
		expect(fetches.length).toBe(1);

		must(fetches[0], 'phase-1 fetch').resolve({ ok: true, json: async () => ({ count: 34, trackIds: [1, 2, 3] }) });
		await settle();
		// Phase 1: the fast estimate is emitted with exact:false, and phase 2 (exact) is now in flight.
		expect(results.at(-1)).toMatchObject({ count: 34, trackIds: [1, 2, 3], exact: false });
		expect(fetches.length).toBe(2);
		expect(must(vi.mocked(fetch).mock.calls[1], 'phase-2 call')[0]).toBe('/api/SegmentsApi/shapes/preview/exact');

		must(fetches[1], 'phase-2 fetch').resolve({ ok: true, json: async () => ({ count: 12, trackIds: [1], effortCount: 20, truncated: false }) });
		await settle();
		// Phase 2: the precise count refines the number and carries exact:true + effortCount.
		expect(results.at(-1)).toMatchObject({ count: 12, trackIds: [1], exact: true, effortCount: 20, truncated: false });
		expect(results).toHaveLength(2);
	});

	it('emits nothing for a phase whose response is not ok, and still runs the next phase', async () => {
		const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
		store.add(zoneFromBounds({ south: 0, west: 0, north: 1, east: 1 }));
		await vi.advanceTimersByTimeAsync(300);
		expect(fetches.length).toBe(1);

		// Phase 1 rejected by the server → no fast result emitted, but phase 2 still fires.
		must(fetches[0], 'phase-1 fetch').resolve({ ok: false, status: 500, text: async () => 'boom', json: async () => ({}) });
		await settle();
		expect(results).toHaveLength(0);
		expect(warn).toHaveBeenCalled();
		expect(fetches.length).toBe(2);

		// Phase 2 also rejected → still nothing emitted (the count never regresses to a garbage value).
		must(fetches[1], 'phase-2 fetch').resolve({ ok: false, status: 500, text: async () => 'boom', json: async () => ({}) });
		await settle();
		expect(results).toHaveLength(0);
		warn.mockRestore();
	});

	it('a preview resolving after the store empties does not overwrite the zero result', async () => {
		store.add(zoneFromBounds({ south: 0, west: 0, north: 1, east: 1 }));
		await vi.advanceTimersByTimeAsync(300); // debounce → fetchPreview begins, fast fetch pending
		expect(fetches.length).toBe(1);

		store.clear();
		await vi.advanceTimersByTimeAsync(300); // empty branch: cancel in-flight + emit {count:0}
		expect(results.at(-1)?.count).toBe(0);

		// The stale fast preview now resolves — its aborted signal must swallow the result.
		must(fetches[0], 'phase-1 fetch').resolve({ ok: true, json: async () => ({ count: 34, trackIds: [1, 2] }) });
		await vi.advanceTimersByTimeAsync(0);
		await Promise.resolve();

		expect(results.at(-1)?.count).toBe(0);
		expect(fetches.length).toBe(1); // exact preview was never issued (aborted before phase 2)
	});
});
