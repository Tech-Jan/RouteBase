import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// Stub ctx — the apply controller reads effortVisibility (initial baseline), setVisibleEffortIndices, and
// config.canExcludeEfforts (the removal/restore capability). These cases all exercise the OWNER path, so
// the capability is on; the share path is pinned separately below.
const { setVisibleEffortIndices, config } = vi.hoisted(() => ({
	setVisibleEffortIndices: vi.fn(),
	config: { canExcludeEfforts: true } as { canExcludeEfforts: boolean },
}));
vi.mock('./segment-statistics', () => ({
	ctx: { effortVisibility: new Set<number>(), setVisibleEffortIndices, config },
}));

import { createApplyController } from './segment-stats-apply-controller';
import { must } from '../core/invariant';

function applyButton(toolbar: HTMLElement): HTMLButtonElement {
	return toolbar.querySelector('button') as HTMLButtonElement;
}

const flush = () => new Promise((r) => setTimeout(r, 10));

describe('Apply controller — SS-03 / SS-04', () => {
	let toolbar: HTMLElement;

	beforeEach(() => {
		setVisibleEffortIndices.mockClear();
		toolbar = document.createElement('div');
		document.body.appendChild(toolbar);
	});

	afterEach(() => {
		vi.unstubAllGlobals();
		toolbar.remove();
	});

	it('deletes exactly the ids marked at confirm time; a row marked mid-flight is a no-op (SS-03)', async () => {
		const fetchMock = vi.fn((_url: string) => Promise.resolve({ ok: true, status: 200 }));
		const confirmMock = vi.fn((_msg?: string) => true);
		vi.stubGlobal('fetch', fetchMock);
		vi.stubGlobal('confirm', confirmMock);
		vi.stubGlobal('location', { reload: vi.fn() } as unknown as Location);

		const ctl = createApplyController();
		ctl.mountButton(toolbar);
		ctl.toggleRemoval(1);
		ctl.toggleRemoval(2);

		applyButton(toolbar).click(); // apply() suspends at the first await → applying = true

		// Marking a new row while applying is frozen (returns the pre-existing state, does not enqueue it).
		expect(ctl.toggleRemoval(3)).toBe(false);
		expect(ctl.isMarked(3)).toBe(false);

		await flush();
		expect(fetchMock).toHaveBeenCalledTimes(2);
		const urls = fetchMock.mock.calls.map((c) => c[0]);
		expect(urls).toContain('/api/SegmentsApi/efforts/1');
		expect(urls).toContain('/api/SegmentsApi/efforts/2');
		expect(urls).not.toContain('/api/SegmentsApi/efforts/3');
	});

	// Item 25 replaced SS-04: removal is now a DURABLE exclusion, so the old "re-analysis will re-add"
	// warning became wrong — the copy must say the exclusion survives re-analysis and is restorable.
	it('confirm copy states the exclusion is durable and restorable (item 25, ex-SS-04)', async () => {
		const confirmMock = vi.fn((_msg?: string) => false); // cancel — no deletion needed to inspect the copy
		vi.stubGlobal('fetch', vi.fn());
		vi.stubGlobal('confirm', confirmMock);
		vi.stubGlobal('location', { reload: vi.fn() } as unknown as Location);

		const ctl = createApplyController();
		ctl.mountButton(toolbar);
		ctl.toggleRemoval(1);
		applyButton(toolbar).click();
		await flush();

		expect(confirmMock).toHaveBeenCalled();
		const copy = must(confirmMock.mock.calls[0], 'one confirm call')[0] as string;
		expect(copy).toContain('survives re-analysis');
		expect(copy).toContain('Restore');
		expect(copy).not.toContain('re-add');
	});

	// Item 25: excluded rows marked for restore POST to the restore endpoint on Apply; restores alone
	// need no confirm (non-destructive).
	it('restores marked rows via POST /restore without a confirm', async () => {
		const fetchMock = vi.fn((_url: string, _init?: RequestInit) => Promise.resolve({ ok: true, status: 204 }));
		const confirmMock = vi.fn((_msg?: string) => true);
		vi.stubGlobal('fetch', fetchMock);
		vi.stubGlobal('confirm', confirmMock);
		vi.stubGlobal('location', { reload: vi.fn() } as unknown as Location);

		const ctl = createApplyController();
		ctl.mountButton(toolbar);
		expect(ctl.toggleRestore(7)).toBe(true);
		expect(ctl.isRestoreMarked(7)).toBe(true);
		applyButton(toolbar).click();
		await flush();

		expect(confirmMock).not.toHaveBeenCalled();
		expect(fetchMock).toHaveBeenCalledTimes(1);
		const restoreCall = must(fetchMock.mock.calls[0], 'one fetch call');
		expect(restoreCall[0]).toBe('/api/SegmentsApi/efforts/7/restore');
		expect((restoreCall[1] as RequestInit).method).toBe('POST');
	});

	it('visibility-only apply syncs the effective set without any DELETE', async () => {
		const fetchMock = vi.fn();
		vi.stubGlobal('fetch', fetchMock);
		vi.stubGlobal('location', { reload: vi.fn() } as unknown as Location);

		const ctl = createApplyController();
		ctl.mountButton(toolbar);
		ctl.noteTableState([1, 2], new Set<number>([1, 2]));
		applyButton(toolbar).click();
		await flush();

		expect(fetchMock).not.toHaveBeenCalled();
		expect(setVisibleEffortIndices).toHaveBeenCalledTimes(1);
		expect(must(setVisibleEffortIndices.mock.calls[0], 'one call')[0].sort()).toEqual([1, 2]);
	});

	// Defence in depth for the share view: the Excluded/actions columns and their delegation are never
	// built when the capability is off, so nothing can mark a row — but the Apply button IS mounted for
	// every viewer (it also applies the visibility filter), so a crafted DOM could otherwise reach an
	// owner mutation. The server gates on OwnedSegments regardless; this stops the attempt.
	it('never issues a removal or restore when the route cannot exclude efforts', async () => {
		const fetchMock = vi.fn(() => Promise.resolve({ ok: true, status: 200 }));
		const confirmMock = vi.fn(() => true);
		vi.stubGlobal('fetch', fetchMock);
		vi.stubGlobal('confirm', confirmMock);
		vi.stubGlobal('location', { reload: vi.fn() } as unknown as Location);

		config.canExcludeEfforts = false;
		try {
			const ctl = createApplyController();
			ctl.mountButton(toolbar);
			ctl.toggleRemoval(1);
			ctl.toggleRestore(7);
			ctl.noteTableState([1, 7], new Set<number>([1]));
			applyButton(toolbar).click();
			await flush();

			expect(fetchMock).not.toHaveBeenCalled();
			expect(confirmMock).not.toHaveBeenCalled();
			// The visibility half still works — it is a viewer feature, not an owner action.
			expect(setVisibleEffortIndices).toHaveBeenCalledTimes(1);
		} finally {
			config.canExcludeEfforts = true;
		}
	});
});
