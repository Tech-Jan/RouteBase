import { describe, it, expect, vi, afterEach } from 'vitest';
import { must } from '../core/invariant';
import {
	chunkIds,
	skipMessage,
	bucketEdits,
	collectDeletes,
	hiddenRowsWarning,
	createBulkController,
	MAX_BULK_IDS,
	type PendingEdit,
} from './activities-bulk-controller';

// The bulk bar's pure helpers: batching a large selection under the server's per-request cap, the
// owner-scope shortfall message, and grouping staged edits into minimal commit payloads. All DOM-free.

describe('chunkIds', () => {
	it('returns no batches for an empty selection', () => {
		expect(chunkIds([])).toEqual([]);
	});

	it('returns a single batch when at or under the cap', () => {
		expect(chunkIds([1, 2, 3], 3)).toEqual([[1, 2, 3]]);
	});

	it('splits into ≤size batches, covering every id in order', () => {
		expect(chunkIds([1, 2, 3, 4, 5], 2)).toEqual([[1, 2], [3, 4], [5]]);
	});

	it('defaults the batch size to the server cap', () => {
		const ids = Array.from({ length: MAX_BULK_IDS + 1 }, (_, i) => i + 1);
		const batches = chunkIds(ids);
		expect(batches).toHaveLength(2);
		expect(batches[0]).toHaveLength(MAX_BULK_IDS);
		expect(batches[1]).toEqual([MAX_BULK_IDS + 1]);
		// no id lost or duplicated across the split
		expect(batches.flat()).toEqual(ids);
	});
});

describe('skipMessage', () => {
	it('is null when everything selected was applied', () => {
		expect(skipMessage(5, 5, 'Moved')).toBeNull();
		expect(skipMessage(6, 5, 'Moved')).toBeNull(); // defensively, never warn on an over-count
	});

	it('reports the shortfall when the server changed fewer than were selected', () => {
		expect(skipMessage(3, 5, 'Moved')).toBe("Moved 3 of 5 — the rest aren't yours to change.");
	});
});

describe('bucketEdits / collectDeletes (staged-commit grouping)', () => {
	const map = (entries: Array<[number, PendingEdit]>): Map<number, PendingEdit> => new Map(entries);

	it('groups rows sharing the same visibility/type into one bucket', () => {
		const buckets = bucketEdits(
			map([
				[1, { visibility: 'public' }],
				[2, { visibility: 'public' }],
				[3, { visibility: 'private' }],
			]),
		);
		expect(buckets).toEqual([
			{ trackIds: [1, 2], visibility: 'public', activityType: undefined },
			{ trackIds: [3], visibility: 'private', activityType: undefined },
		]);
	});

	it('keys a bucket by BOTH visibility and type (a combined edit is its own bucket)', () => {
		const buckets = bucketEdits(
			map([
				[1, { visibility: 'public' }],
				[2, { visibility: 'public', activityType: 'Run' }],
			]),
		);
		expect(buckets).toHaveLength(2);
	});

	it('excludes rows staged for deletion (delete wins over an edit on the same row)', () => {
		const pending = map([
			[1, { visibility: 'public', deleted: true }],
			[2, { visibility: 'public' }],
		]);
		expect(bucketEdits(pending)).toEqual([{ trackIds: [2], visibility: 'public', activityType: undefined }]);
		expect(collectDeletes(pending)).toEqual([1]);
	});

	it('ignores rows with no effective change', () => {
		expect(bucketEdits(map([[1, {}]]))).toEqual([]);
		expect(collectDeletes(map([[1, {}]]))).toEqual([]);
	});
});

describe('hiddenRowsWarning (FE-07 hidden-selection guard)', () => {
	it('is null when every acted-on row passes the active filters', () => {
		expect(hiddenRowsWarning([1, 2, 3], new Set([1, 2, 3, 4]))).toBeNull();
	});

	it('is null for an empty acted set', () => {
		expect(hiddenRowsWarning([], new Set())).toBeNull();
	});

	it('reports total and hidden counts when the filters hide part of the selection', () => {
		expect(hiddenRowsWarning([1, 2, 3, 4, 5], new Set([1, 2]))).toBe(
			'5 selected, 3 currently hidden by filters — proceed?',
		);
	});
});

describe('bulk commit confirm (FE-07: selection survives filter narrowing)', () => {
	afterEach(() => {
		vi.unstubAllGlobals();
		document.body.innerHTML = '';
	});

	it('select-all → narrow filter → apply: the dialog reports the hidden count', () => {
		const confirmSpy = vi.fn((_message?: string) => false); // decline, so nothing is written
		vi.stubGlobal('confirm', confirmSpy);
		const fetchSpy = vi.fn();
		vi.stubGlobal('fetch', fetchSpy);

		const ctl = createBulkController(['Ride']);
		// The grid reports only {1, 2} still passing the narrowed filters.
		ctl.attach({ redraw: () => {}, exit: () => {}, visibleIds: () => new Set([1, 2]) });
		const toolbar = document.createElement('div');
		document.body.appendChild(toolbar);
		ctl.mountBar(toolbar);

		ctl.setSelection(new Set([1, 2, 3, 4, 5])); // select-all before the filter narrowed
		(toolbar.querySelector('.act-bulk-delete') as HTMLButtonElement).click(); // stage deletion
		(toolbar.querySelector('.act-bulk-done') as HTMLButtonElement).click(); // apply

		expect(confirmSpy).toHaveBeenCalledTimes(1);
		const message = must(confirmSpy.mock.calls[0], 'one confirm call')[0] ?? '';
		expect(message).toContain('5 selected, 3 currently hidden by filters — proceed?');
		expect(fetchSpy).not.toHaveBeenCalled(); // confirm declined → no writes
	});

	it('omits the hidden-rows line when everything acted on is visible', () => {
		const confirmSpy = vi.fn((_message?: string) => false);
		vi.stubGlobal('confirm', confirmSpy);
		vi.stubGlobal('fetch', vi.fn());

		const ctl = createBulkController(['Ride']);
		ctl.attach({ redraw: () => {}, exit: () => {}, visibleIds: () => new Set([1, 2]) });
		const toolbar = document.createElement('div');
		document.body.appendChild(toolbar);
		ctl.mountBar(toolbar);

		ctl.setSelection(new Set([1, 2]));
		(toolbar.querySelector('.act-bulk-delete') as HTMLButtonElement).click();
		(toolbar.querySelector('.act-bulk-done') as HTMLButtonElement).click();

		const message = must(confirmSpy.mock.calls[0], 'one confirm call')[0] ?? '';
		expect(message).not.toContain('hidden by filters');
	});

	// confirm→true actually commits: one bulk/update per value-bucket, then one bulk/delete — the branch
	// the decline-only tests above never reach. Pins the request bodies and the server-count read-back.
	it('confirm→true: posts one bulk/update per bucket and one bulk/delete, reading server counts', async () => {
		vi.stubGlobal('confirm', vi.fn(() => true)); // accept → the commit runs for real
		const fetchSpy = vi.fn(async (path: string, init: { body: string }) => {
			const field = path.endsWith('/bulk/delete') ? 'deleted' : 'updated';
			const count = (JSON.parse(init.body).trackIds as number[]).length;
			return { ok: true, json: async () => ({ [field]: count }) } as unknown as Response;
		});
		vi.stubGlobal('fetch', fetchSpy);
		const alertSpy = vi.fn();
		vi.stubGlobal('alert', alertSpy);
		const exit = vi.fn();

		const ctl = createBulkController(['Ride', 'Run']);
		ctl.attach({ redraw: () => {}, exit, visibleIds: () => new Set([1, 2, 3]) });
		const toolbar = document.createElement('div');
		document.body.appendChild(toolbar);
		ctl.mountBar(toolbar);

		// Stage a visibility change on {1,2}…
		ctl.setSelection(new Set([1, 2]));
		const visSel = toolbar.querySelector('.act-bulk-select') as HTMLSelectElement;
		visSel.value = 'public';
		visSel.dispatchEvent(new Event('change'));
		// …and a deletion on {3}.
		ctl.setSelection(new Set([3]));
		(toolbar.querySelector('.act-bulk-delete') as HTMLButtonElement).click();

		(toolbar.querySelector('.act-bulk-done') as HTMLButtonElement).click();
		await vi.waitFor(() => expect(exit).toHaveBeenCalled()); // commit is async

		expect(fetchSpy).toHaveBeenCalledTimes(2);
		const update = fetchSpy.mock.calls.find(([p]) => p === '/api/activities/bulk/update')!;
		const del = fetchSpy.mock.calls.find(([p]) => p === '/api/activities/bulk/delete')!;
		expect(update).toBeTruthy();
		expect(del).toBeTruthy();
		expect(JSON.parse(update[1].body)).toEqual({ trackIds: [1, 2], visibility: 'public' });
		expect(JSON.parse(del[1].body)).toEqual({ trackIds: [3] });
		// Server applied everything staged (counts echoed) → no shortfall alert.
		expect(alertSpy).not.toHaveBeenCalled();
	});

	// Server owner-scoping applies FEWER than staged → the shortfall alert fires with server counts.
	it('confirm→true: reports the shortfall when the server changes fewer than staged', async () => {
		vi.stubGlobal('confirm', vi.fn(() => true));
		const fetchSpy = vi.fn(async (path: string) => {
			const body = path.endsWith('/bulk/delete') ? { deleted: 0 } : { updated: 1 };
			return { ok: true, json: async () => body } as unknown as Response;
		});
		vi.stubGlobal('fetch', fetchSpy);
		const alertSpy = vi.fn();
		vi.stubGlobal('alert', alertSpy);
		const exit = vi.fn();

		const ctl = createBulkController(['Ride']);
		ctl.attach({ redraw: () => {}, exit, visibleIds: () => new Set([1, 2, 3]) });
		const toolbar = document.createElement('div');
		document.body.appendChild(toolbar);
		ctl.mountBar(toolbar);

		ctl.setSelection(new Set([1, 2])); // stage two visibility edits, server will apply only 1
		const visSel = toolbar.querySelector('.act-bulk-select') as HTMLSelectElement;
		visSel.value = 'public';
		visSel.dispatchEvent(new Event('change'));
		ctl.setSelection(new Set([3])); // stage one delete, server will apply 0
		(toolbar.querySelector('.act-bulk-delete') as HTMLButtonElement).click();

		(toolbar.querySelector('.act-bulk-done') as HTMLButtonElement).click();
		await vi.waitFor(() => expect(exit).toHaveBeenCalled());

		expect(alertSpy).toHaveBeenCalledTimes(1);
		const alerted = must(alertSpy.mock.calls[0], 'one alert call')[0] as string;
		expect(alerted).toContain("Changed 1 of 2 — the rest aren't yours to change.");
		expect(alerted).toContain("Moved 0 of 1 — the rest aren't yours to change.");
	});
});
