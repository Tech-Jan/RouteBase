// @vitest-environment node
import { describe, it, expect, vi, afterEach } from 'vitest';
import { createStatsData, type StatsSummary } from './statistics-data';
import type { GridFilterState } from '../core/grid/grid-filter-state';
import type { GridColumn } from '../core/grid/grid-types';

const flush = (): Promise<void> => new Promise((r) => setTimeout(r, 0));

// Minimal GridFilterState — createStatsData only calls snapshot() + subscribe().
function fakeState(): GridFilterState {
	return {
		snapshot: () => ({ filters: {} }),
		subscribe: () => {},
	} as unknown as GridFilterState;
}

const SUMMARY: StatsSummary = {
	overall: {
		totalActivities: 3, totalDistance: 30, totalAscent: 0, totalDescent: 0,
		totalTime: 0, totalMovingTime: 0, averageSpeed: 0,
	},
	byType: [],
};

describe('createStatsData cache (ST-04)', () => {
	afterEach(() => vi.restoreAllMocks());

	it('retries a failed fetch on the next refresh for the same filter state', async () => {
		let fail = true;
		vi.stubGlobal('fetch', vi.fn(async (url: string) => {
			if (fail) throw new Error('offline');
			return { ok: true, json: async () => (url.includes('summary') ? SUMMARY : []) } as unknown as Response;
		}));

		const data = createStatsData(fakeState(), []);
		const summaries: StatsSummary[] = [];
		data.onSummary((s) => summaries.push(s));

		data.refresh();
		await flush();
		expect(summaries).toHaveLength(0); // failed → no render, cache not poisoned

		fail = false;
		data.refresh();
		await flush();
		expect(summaries).toHaveLength(1); // retried cleanly, not a cached empty fallback
		expect(summaries[0]).toEqual(SUMMARY);
	});

	it('does not overwrite the latest render with a superseded fetch', async () => {
		// Two refreshes for DIFFERENT filter states (distinct query strings), whose fetches are gated so the
		// SECOND (latest) resolves first and the FIRST lands stale afterwards. Without the qs !== latest guard,
		// the late 'a' response would clobber the 'b' render.
		const gates: Record<string, { resolve: () => void; promise: Promise<void> }> = {};
		const gate = (key: string): Promise<void> => {
			if (!gates[key]) {
				let resolve!: () => void;
				const promise = new Promise<void>((r) => { resolve = r; });
				gates[key] = { resolve, promise };
			}
			return gates[key].promise;
		};

		vi.stubGlobal('fetch', vi.fn(async (url: string) => {
			const key = url.includes('text=b') ? 'b' : 'a';
			await gate(key);
			const summary: StatsSummary = { overall: { ...SUMMARY.overall, totalActivities: key === 'b' ? 2 : 1 }, byType: [] };
			return { ok: true, json: async () => (url.includes('summary') ? summary : []) } as unknown as Response;
		}));

		// snapshot returns filter {name:'a'} on the first refresh, {name:'b'} on the second → two query strings.
		let call = 0;
		const state = {
			snapshot: () => ({ filters: { name: { text: call++ === 0 ? 'a' : 'b' } } }),
			subscribe: () => {},
		} as unknown as GridFilterState;
		const columns = [{ key: 'name', header: 'Name', filter: { type: 'text' } }] as unknown as GridColumn<unknown>[];

		const data = createStatsData(state, columns);
		const summaries: StatsSummary[] = [];
		data.onSummary((s) => summaries.push(s));

		data.refresh(); // qs = f.name.text=a, latest := a
		data.refresh(); // qs = f.name.text=b, latest := b
		await flush();
		expect(summaries).toHaveLength(0); // both fetches still gated

		gates['b']!.resolve(); // the LATEST fetch lands first
		await flush();
		expect(summaries).toHaveLength(1);
		expect(summaries[0]!.overall.totalActivities).toBe(2);

		gates['a']!.resolve(); // the superseded fetch lands late
		await flush();
		expect(summaries).toHaveLength(1); // stale 'a' dropped — the latest render survives
		expect(summaries[0]!.overall.totalActivities).toBe(2);
	});
});

describe('createStatsData first-load error (FUX-06)', () => {
	afterEach(() => vi.restoreAllMocks());

	it('fires onError when the first load fails with nothing to fall back on', async () => {
		vi.stubGlobal('fetch', vi.fn(async () => { throw new Error('offline'); }));

		const data = createStatsData(fakeState(), []);
		let errors = 0;
		data.onError(() => { errors++; });

		data.refresh();
		await flush();
		expect(errors).toBe(1); // no last-good render → the failure is surfaced, not swallowed
	});

	it('stays quiet on a failure that lands after a good render (ST-04 keeps last-good)', async () => {
		// 'a' succeeds, 'b' fails. Once 'a' has rendered, the 'b' failure must not raise the banner over
		// the valid data already on screen.
		let call = 0;
		const state = {
			snapshot: () => ({ filters: { name: { text: call++ === 0 ? 'a' : 'b' } } }),
			subscribe: () => {},
		} as unknown as GridFilterState;
		const columns = [{ key: 'name', header: 'Name', filter: { type: 'text' } }] as unknown as GridColumn<unknown>[];
		vi.stubGlobal('fetch', vi.fn(async (url: string) => {
			if (url.includes('text=b')) throw new Error('offline');
			return { ok: true, json: async () => (url.includes('summary') ? SUMMARY : []) } as unknown as Response;
		}));

		const data = createStatsData(state, columns);
		let errors = 0;
		data.onError(() => { errors++; });
		const summaries: StatsSummary[] = [];
		data.onSummary((s) => summaries.push(s));

		data.refresh(); // 'a' — renders, hasRendered := true
		await flush();
		expect(summaries).toHaveLength(1);

		data.refresh(); // 'b' — fails, but valid data is already shown
		await flush();
		expect(errors).toBe(0);
	});
});
