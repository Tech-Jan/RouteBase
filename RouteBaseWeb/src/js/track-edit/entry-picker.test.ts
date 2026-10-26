import { describe, expect, it, beforeEach, vi } from 'vitest';
import { must } from '../core/invariant';

// Stub the overview seam so importing the page module never pulls the MapLibre chunk (the real
// getOverviewSelectionBridge is never used here — every test injects its own mock bridge).
vi.mock('../overview/activities-overview', () => ({
	getOverviewSelectionBridge: () => ({ getHighlightIds: () => null, onHighlightChanged: () => {}, getTrackMeta: () => null }),
}));

import { initEntryPicker, type EntryPickerDeps, type EntryPickerApi } from './entry-picker';
import { EditApiError } from './edit-api';
import { TRACK_EDIT_API_ERRORS, type SessionStateResponse } from './edit-session-api';
import type { OverviewSelectionBridge } from '../overview/activities-overview';
import type { OverviewPickBridge } from '../overview/overview-pick-bridge';
import type { EditOp } from './edit-ops';

const tick = (): Promise<void> => new Promise((r) => setTimeout(r, 0));
const load = (activityId: number): EditOp => ({ op: 'loadActivity', activityId });

function sessionResp(o: {
	ops: EditOp[]; cursor?: number; slots?: number; status?: 'active' | 'saved' | 'discarded'; sessionId?: string;
}): SessionStateResponse {
	const slotN = o.slots ?? o.ops.filter((op) => op.op === 'loadActivity').length;
	return {
		sessionId: o.sessionId ?? 'sess-1',
		script: { schemaVersion: 1, ops: o.ops },
		cursor: o.cursor ?? o.ops.length,
		stamps: [],
		status: o.status ?? 'active',
		slotCounts: Array.from({ length: slotN }, (_v, i) => ({ slotId: i, pointCount: 100 })),
		updatedAt: '2026-07-12T00:00:00Z',
	};
}

function makeDeps(o: { ids?: number[] | null; meta?: Record<number, { name: string; date: string }>; search?: string; picked?: number[] }) {
	let ids = o.ids ?? null;
	const meta = o.meta ?? {};
	const subs: Array<(ids: number[] | null) => void> = [];
	const bridge: OverviewSelectionBridge = {
		getHighlightIds: () => ids,
		onHighlightChanged: (cb) => { subs.push(cb); cb(ids); },
		getTrackMeta: (id) => meta[id] ?? null,
	};
	// Mock pick bridge (editor checkbox selection) with a setPicked() driver.
	let picked = o.picked ?? [];
	const pickSubs: Array<(ids: number[]) => void> = [];
	const clearPicks = vi.fn(() => { picked = []; pickSubs.forEach((cb) => cb(picked)); });
	const picks: OverviewPickBridge = {
		getPickedIds: () => picked,
		onPickedChanged: (cb) => { pickSubs.push(cb); cb(picked); },
		clearPicks,
	};
	const navigate = vi.fn();
	const api = { createSession: vi.fn(), getSession: vi.fn(), appendOps: vi.fn() };
	const deps: EntryPickerDeps = { bridge, picks, api: api as unknown as EntryPickerApi, navigate, search: o.search ?? '' };
	return {
		deps, navigate, api, clearPicks,
		setHighlight: (n: number[] | null) => { ids = n; subs.forEach((cb) => cb(ids)); },
		setPicked: (n: number[]) => { picked = n; pickSubs.forEach((cb) => cb(picked)); },
	};
}

function mountButton(): HTMLButtonElement {
	const btn = document.createElement('button');
	btn.id = 'trackEditOpenBtn';
	btn.hidden = true;
	const label = document.createElement('span');
	label.className = 'act-map-mode-label';
	label.textContent = 'Open in editor';
	btn.appendChild(label);
	document.body.appendChild(btn);
	return btn;
}

beforeEach(() => { document.body.innerHTML = ''; vi.restoreAllMocks(); });

describe('entry-picker Flow A', () => {
	// T-EP15
	it('button reacts to the bridge (hidden on null, labelled on a set)', () => {
		const btn = mountButton();
		const { deps, setHighlight } = makeDeps({ ids: null });
		initEntryPicker(deps);
		expect(btn.hidden).toBe(true); // immediate fire with null

		setHighlight([3, 1, 2]);
		expect(btn.hidden).toBe(false);
		expect(btn.disabled).toBe(false);
		expect(btn.querySelector('.act-map-mode-label')!.textContent).toBe('Open 3 in editor');
	});

	// T-EP16
	it('click posts chronologically-ordered ids and redirects to the response slot0', async () => {
		const btn = mountButton();
		const meta = {
			1: { name: 'A', date: '01.01.2026 09:00' },
			3: { name: 'B', date: '01.01.2026 10:00' },
			2: { name: 'C', date: '01.01.2026 11:00' },
		};
		const { deps, navigate, api } = makeDeps({ ids: [3, 1, 2], meta });
		api.createSession.mockResolvedValue(sessionResp({ ops: [load(99)], cursor: 1, sessionId: 'sess-1' }));
		initEntryPicker(deps);

		btn.click();
		await tick();

		expect(api.createSession).toHaveBeenCalledWith([1, 3, 2]); // D-A5-1 chronological
		expect(navigate).toHaveBeenCalledWith('/Activities/Details/99?editSession=sess-1'); // response slot0
	});

	// T-EP17
	it('a 409 offender error names the track, does not navigate, and re-enables the button', async () => {
		const btn = mountButton();
		const meta = { 3: { name: 'Morning Run', date: '01.01.2026 10:00' } };
		const { deps, navigate, api } = makeDeps({ ids: [3, 1, 2], meta });
		api.createSession.mockRejectedValue(new EditApiError(409, TRACK_EDIT_API_ERRORS.stillRecording, undefined, 3));
		initEntryPicker(deps);

		btn.click();
		await tick();

		expect(navigate).not.toHaveBeenCalled();
		const toast = document.querySelector('.entry-picker-toast');
		expect(toast?.textContent).toContain('Morning Run');
		expect(btn.disabled).toBe(false); // re-enabled after the failure
	});
});

describe('entry-picker Flow A front door (?editorPick=1)', () => {
	// Unified rule: selection = the effective highlight (marks ?? paint), so painting alone selects; the Clear
	// button appears only while explicit marks exist and routes to the pick bridge.
	// T-EP22 — bar visible from zero; paint (setHighlight) selects with no Clear; marks (setPicked) add Clear.
	it('bar counts the highlight (paint selects), Clear appears only with marks and reverts them', () => {
		const { deps, setHighlight, setPicked, clearPicks } = makeDeps({ ids: null, search: '?editorPick=1' });
		initEntryPicker(deps);

		const bar = document.querySelector('.entry-picker-bar');
		expect(bar).not.toBeNull(); // visible before any track is picked
		const openBtn = bar!.querySelector('.entry-picker-add') as HTMLButtonElement;
		const clearBtn = bar!.querySelector('.entry-picker-clear') as HTMLButtonElement;
		expect(openBtn.disabled).toBe(true);
		expect(clearBtn.hidden).toBe(true);
		expect(bar!.querySelector('.entry-picker-bar-reason')!.textContent).toContain('paint the map');

		setHighlight([5, 2]); // paint highlights 2 tracks — selectable with no marks
		expect(openBtn.disabled).toBe(false);
		expect(openBtn.textContent).toBe('Open 2 in editor');
		expect(clearBtn.hidden).toBe(true); // nothing to Clear (no marks)

		setPicked([5, 2]); // now the tracks are checkbox-marked → Clear appears
		expect(clearBtn.hidden).toBe(false);

		clearBtn.click();
		expect(clearPicks).toHaveBeenCalledTimes(1);
		expect(clearBtn.hidden).toBe(true); // marks gone → Clear hidden; highlight (paint) still selects
		expect(openBtn.disabled).toBe(false);
	});

	// T-EP23
	it('Open posts the highlighted ids (ordered) and redirects; Cancel leaves the front door', async () => {
		const meta = { 2: { name: 'A', date: '01.01.2026 09:00' }, 5: { name: 'B', date: '01.01.2026 10:00' } };
		const open = makeDeps({ ids: [5, 2], meta, search: '?editorPick=1' }); // paint-only (no marks)
		open.api.createSession.mockResolvedValue(sessionResp({ ops: [load(2)], cursor: 1, sessionId: 'sess-9' }));
		initEntryPicker(open.deps);

		(document.querySelector('.entry-picker-add') as HTMLButtonElement).click();
		await tick();
		expect(open.api.createSession).toHaveBeenCalledWith([2, 5]); // D-A5-1 chronological (via getTrackMeta)
		expect(open.navigate).toHaveBeenCalledWith('/Activities/Details/2?editSession=sess-9');

		document.body.innerHTML = '';
		const cancel = makeDeps({ ids: [5, 2], meta, search: '?editorPick=1' });
		initEntryPicker(cancel.deps);
		(document.querySelector('.entry-picker-cancel') as HTMLButtonElement).click();
		expect(cancel.api.createSession).not.toHaveBeenCalled();
		expect(cancel.navigate).toHaveBeenCalledWith('/Activities');
	});
});

describe('entry-picker Flow B', () => {
	// T-EP18
	it('mounts the confirm bar and subtracts in-session ids', async () => {
		const { deps, api } = makeDeps({ ids: [10, 30, 40], search: '?pickFor=sess-1' });
		api.getSession.mockResolvedValue(sessionResp({ ops: [load(10), load(20)], cursor: 2 }));
		initEntryPicker(deps);
		await tick();

		const bar = document.querySelector('.entry-picker-bar');
		expect(bar).not.toBeNull();
		expect(bar!.querySelector('.entry-picker-bar-msg')!.textContent).toContain('2 selected · 1 already in session');
	});

	// T-EP19
	it('a 404 session shows a banner, strips pickFor, and mounts no bar', async () => {
		const replace = vi.spyOn(window.history, 'replaceState');
		const { deps, api } = makeDeps({ ids: [1], search: '?pickFor=sess-x&foo=1' });
		api.getSession.mockRejectedValue(new EditApiError(404, 'httpError'));
		initEntryPicker(deps);
		await tick();

		expect(document.querySelector('.entry-picker-banner')).not.toBeNull();
		expect(document.querySelector('.entry-picker-bar')).toBeNull();
		expect(replace).toHaveBeenCalled();
		const url = String(must(replace.mock.calls[0], 'one replaceState')[2]);
		expect(url).not.toContain('pickFor');
		expect(url).toContain('foo=1');
	});

	// T-EP20
	it('Add posts {baseCursor, ops} and redirects from the response; Cancel navigates without a POST', async () => {
		const meta = { 30: { name: 'X', date: '01.01.2026 09:00' }, 40: { name: 'Y', date: '01.01.2026 10:00' } };

		// Add path
		const add = makeDeps({ ids: [10, 30, 40], meta, search: '?pickFor=sess-1' });
		add.api.getSession.mockResolvedValue(sessionResp({ ops: [load(10)], cursor: 1, sessionId: 'sess-1' }));
		add.api.appendOps.mockResolvedValue(sessionResp({ ops: [load(77), load(30), load(40)], cursor: 3, sessionId: 'sess-1' }));
		initEntryPicker(add.deps);
		await tick();
		(document.querySelector('.entry-picker-add') as HTMLButtonElement).click();
		await tick();

		expect(add.api.appendOps).toHaveBeenCalledWith('sess-1', 1, [load(30), load(40)]);
		expect(add.navigate).toHaveBeenCalledWith('/Activities/Details/77?editSession=sess-1');

		// Cancel path (fresh mount)
		document.body.innerHTML = '';
		const cancel = makeDeps({ ids: [10, 30], meta, search: '?pickFor=sess-1' });
		cancel.api.getSession.mockResolvedValue(sessionResp({ ops: [load(10)], cursor: 1, sessionId: 'sess-1' }));
		initEntryPicker(cancel.deps);
		await tick();
		(document.querySelector('.entry-picker-cancel') as HTMLButtonElement).click();

		expect(cancel.api.appendOps).not.toHaveBeenCalled();
		expect(cancel.navigate).toHaveBeenCalledWith('/Activities/Details/10?editSession=sess-1'); // GET slot0
	});

	// T-EP21
	it('a cursorConflict on Add refetches the session and shows the refreshed message', async () => {
		const meta = { 30: { name: 'X', date: '01.01.2026 09:00' } };
		const { deps, navigate, api } = makeDeps({ ids: [10, 30], meta, search: '?pickFor=sess-1' });
		api.getSession
			.mockResolvedValueOnce(sessionResp({ ops: [load(10)], cursor: 1, sessionId: 'sess-1' }))
			.mockResolvedValueOnce(sessionResp({ ops: [load(10), load(50)], cursor: 2, sessionId: 'sess-1' }));
		api.appendOps.mockRejectedValue(new EditApiError(409, TRACK_EDIT_API_ERRORS.cursorConflict));
		initEntryPicker(deps);
		await tick();
		(document.querySelector('.entry-picker-add') as HTMLButtonElement).click();
		await tick();
		await tick();

		expect(api.getSession).toHaveBeenCalledTimes(2); // initial + resync
		expect(document.querySelector('.entry-picker-bar-reason')!.textContent).toContain('selection refreshed');
		expect(navigate).not.toHaveBeenCalled();
	});
});
