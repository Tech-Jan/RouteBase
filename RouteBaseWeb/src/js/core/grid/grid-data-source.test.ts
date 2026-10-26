import { describe, it, expect, vi, afterEach } from 'vitest';
import { HttpPostGridDataSource } from './grid-data-source';
import { must } from '../invariant';
import type { GridQuery } from './grid-types';

// B3 (FE-06): the activities grid posts its query — the shape restriction is rule-document JSON
// that outgrows a GET query string. Pins the POST wire mechanics + the abort-previous semantics.

const query: GridQuery = {
	filters: {},
	sort: { column: 'date', direction: 'desc' },
	sorts: [],
	page: 2,
	pageSize: 50,
};

describe('HttpPostGridDataSource', () => {
	afterEach(() => vi.unstubAllGlobals());

	it('POSTs the built body as JSON and resolves the parsed response', async () => {
		const response = { rows: [{ id: 1 }], filteredIds: [1], total: 1, unfilteredTotal: 3 };
		const fetchSpy = vi.fn((_url: string, _init?: RequestInit) =>
			Promise.resolve({ ok: true, json: () => Promise.resolve(response) } as unknown as Response));
		vi.stubGlobal('fetch', fetchSpy);

		const source = new HttpPostGridDataSource('/api/activities/grid', (q) => ({ page: q.page, shape: 'doc' }));
		const res = await source.fetch(query);

		expect(res).toEqual(response);
		expect(fetchSpy).toHaveBeenCalledTimes(1);
		const [url, init] = must(fetchSpy.mock.calls[0], 'one fetch call');
		expect(url).toBe('/api/activities/grid');
		expect(init?.method).toBe('POST');
		expect((init?.headers as Record<string, string>)['Content-Type']).toBe('application/json');
		expect(JSON.parse(init?.body as string)).toEqual({ page: 2, shape: 'doc' });
	});

	it('aborts the previous in-flight request when a new fetch starts', async () => {
		const seenSignals: AbortSignal[] = [];
		vi.stubGlobal('fetch', vi.fn((_url: string, init?: RequestInit) => {
			seenSignals.push(init!.signal!);
			return Promise.resolve({ ok: true, json: () => Promise.resolve({}) } as unknown as Response);
		}));

		const source = new HttpPostGridDataSource('/api/x', () => ({}));
		await source.fetch(query);
		await source.fetch(query);

		expect(seenSignals).toHaveLength(2);
		expect(must(seenSignals[0], 'first signal').aborted).toBe(true);
		expect(must(seenSignals[1], 'second signal').aborted).toBe(false);
	});

	it('rejects on a non-OK status', async () => {
		vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({ ok: false, status: 500 } as unknown as Response)));
		const source = new HttpPostGridDataSource('/api/x', () => ({}));
		await expect(source.fetch(query)).rejects.toThrow('HTTP 500');
	});
});
