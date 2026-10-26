import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest';
import { must } from '../core/invariant';
import type { SlotTrack } from './apply-script';
import type { SessionStateResponse, SaveResponse } from './edit-session-api';
import { TRACK_EDIT_API_ERRORS } from './edit-session-api';

/**
 * edit-mode-controller.ts — 472 lines, zero test file before tonight, the hub every other track-edit unit
 * test assumes is wired correctly. Every collaborator is stubbed EXCEPT edit-session-state.ts/apply-script.ts
 * (pure, twin-validated — kept real so op application drives genuine state transitions) and edit-save-target.ts
 * (pure). Covers the three concrete failure modes the test-quality audit named: a dropped `afterApply()` call,
 * a reordered `saveDialogOpen` guard, and a wrong navigate-after-save target.
 */

// ---- edit-api: real EditApiError (instanceof checks matter), everything else a controllable mock ----
const createSession = vi.fn();
const getSession = vi.fn();
const getSources = vi.fn();
const appendOps = vi.fn();
const moveCursor = vi.fn();
const savePreflight = vi.fn();
const saveSession = vi.fn();
const discardSession = vi.fn();
const dequantizeSources = vi.fn((...a: unknown[]) => a[0]);
const fetchTrackSuggestions = vi.fn();
const searchTracks = vi.fn();

vi.mock('./edit-api', async () => {
	const actual = await vi.importActual<typeof import('./edit-api')>('./edit-api');
	return {
		EditApiError: actual.EditApiError,
		createSession: (...a: unknown[]) => createSession(...a),
		getSession: (...a: unknown[]) => getSession(...a),
		getSources: (...a: unknown[]) => getSources(...a),
		appendOps: (...a: unknown[]) => appendOps(...a),
		moveCursor: (...a: unknown[]) => moveCursor(...a),
		savePreflight: (...a: unknown[]) => savePreflight(...a),
		saveSession: (...a: unknown[]) => saveSession(...a),
		discardSession: (...a: unknown[]) => discardSession(...a),
		dequantizeSources: (...a: unknown[]) => dequantizeSources(...a),
		fetchTrackSuggestions: (...a: unknown[]) => fetchTrackSuggestions(...a),
		searchTracks: (...a: unknown[]) => searchTracks(...a),
	};
});

// ---- everything DOM/UI: capture the deps the controller wires them with, do nothing real ----
const toolbarHandle = {
	onOrdinalClick: vi.fn(), isToolArmed: vi.fn(() => false), refresh: vi.fn(),
	afterApply: vi.fn(), setDisabled: vi.fn(), destroy: vi.fn(),
};
const createToolbar = vi.fn((_deps: unknown) => toolbarHandle);
vi.mock('./edit-toolbar', () => ({ createToolbar: (deps: unknown) => createToolbar(deps) }));

const slotToolsHandle = { refresh: vi.fn() };
const createSlotTools = vi.fn((_deps: unknown) => slotToolsHandle);
vi.mock('./slot-tools', () => ({ createSlotTools: (deps: unknown) => createSlotTools(deps) }));

const initSlotOverlay = vi.fn();
const clearSlotOverlay = vi.fn();
vi.mock('./slot-overlay', () => ({
	initSlotOverlay: (...a: unknown[]) => initSlotOverlay(...a),
	clearSlotOverlay: (...a: unknown[]) => clearSlotOverlay(...a),
}));

const renderPreview = vi.fn();
vi.mock('./edit-preview-render', () => ({ renderPreview: (...a: unknown[]) => renderPreview(...a) }));

const showDesyncBanner = vi.fn();
const removeDesyncBanner = vi.fn();
const injectKpiChip = vi.fn();
const setEditSessionParam = vi.fn();
const navigatePlain = vi.fn();
vi.mock('./edit-mode-chrome', () => ({
	showDesyncBanner: (...a: unknown[]) => showDesyncBanner(...a),
	removeDesyncBanner: (...a: unknown[]) => removeDesyncBanner(...a),
	injectKpiChip: (...a: unknown[]) => injectKpiChip(...a),
	setEditSessionParam: (...a: unknown[]) => setEditSessionParam(...a),
	navigatePlain: (...a: unknown[]) => navigatePlain(...a),
}));

function fakeDialogHandle() { return { el: document.createElement('div'), close: vi.fn() }; }
const openSaveDialog = vi.fn((_deps: unknown) => fakeDialogHandle());
const openDiscardDialog = vi.fn((_deps: unknown) => fakeDialogHandle());
const openStaleDialog = vi.fn((_deps: unknown) => fakeDialogHandle());
vi.mock('./edit-dialogs', () => ({
	openSaveDialog: (deps: unknown) => openSaveDialog(deps),
	openDiscardDialog: (deps: unknown) => openDiscardDialog(deps),
	openStaleDialog: (deps: unknown) => openStaleDialog(deps),
}));

const closeAllActionPanels = vi.fn();
const setOnChartClick = vi.fn();
vi.mock('./activity-access', () => ({
	closeAllActionPanels: (...a: unknown[]) => closeAllActionPanels(...a),
	setOnChartClick: (...a: unknown[]) => setOnChartClick(...a),
}));

vi.mock('../core/map-state', () => ({ getMaplibreMap: () => null }));

// add-track / append dialogs are only reachable through slot-tools (mocked away) — imported but never invoked.
vi.mock('./add-track-dialog', () => ({ openAddTrackDialog: vi.fn(), buildQueryPoint: vi.fn() }));
vi.mock('./append-dialog', () => ({ openAppendDialog: vi.fn() }));

// The controller holds module-level singleton state (`saveDialogOpen` is never reset by exitEditMode) — a
// fresh module instance per test is the only reliable isolation (matches live-recording.test.ts's pattern).
let mod: typeof import('./edit-mode-controller');

const settle = (): Promise<void> => new Promise((resolve) => setTimeout(resolve, 20));

function deferred<T>(): { promise: Promise<T>; resolve: (v: T) => void } {
	let resolve!: (v: T) => void;
	const promise = new Promise<T>((r) => { resolve = r; });
	return { promise, resolve };
}

function sourceTrack(count: number): SlotTrack {
	const time = new Float64Array(count);
	for (let i = 0; i < count; i++) time[i] = i * 10;
	return { count, channels: { time } };
}

function sessionResponse(overrides: Partial<SessionStateResponse> = {}): SessionStateResponse {
	return {
		sessionId: 'sess-1',
		script: { schemaVersion: 1, ops: [{ op: 'loadActivity', activityId: 1 }] },
		cursor: 1,
		stamps: [],
		status: 'active',
		slotCounts: [{ slotId: 0, pointCount: 5 }],
		updatedAt: new Date().toISOString(),
		...overrides,
	};
}

function stubEnter(): void {
	createSession.mockResolvedValue(sessionResponse());
	getSources.mockResolvedValue({
		slots: new Map([[1, sourceTrack(5)]]),
		tzByActivity: new Map([[1, 2]]),
		nameByActivity: new Map([[1, 'Ride 1']]),
	});
	appendOps.mockResolvedValue(sessionResponse());
	moveCursor.mockResolvedValue(sessionResponse());
}

beforeEach(async () => {
	vi.resetModules();
	vi.clearAllMocks();
	document.body.innerHTML = '';
	stubEnter();
	mod = await import('./edit-mode-controller');
});

afterEach(() => {
	mod.exitEditMode(); // detach this instance's window/document listeners before the next test's fresh import
});

describe('enterEditMode / exitEditMode', () => {
	it('builds state from the created session and mounts the chrome', async () => {
		await mod.enterEditMode({ activityId: 1, tzOffsetHours: 0 });

		expect(createSession).toHaveBeenCalledWith([1]);
		expect(getSources).toHaveBeenCalledWith('sess-1');
		expect(injectKpiChip).toHaveBeenCalled();
		expect(setEditSessionParam).toHaveBeenCalledWith('sess-1');
		expect(createToolbar).toHaveBeenCalledTimes(1);
		expect(createSlotTools).toHaveBeenCalledTimes(1);
		expect(initSlotOverlay).toHaveBeenCalledTimes(1);
		expect(renderPreview).toHaveBeenCalledTimes(1);
		expect(document.body.classList.contains('track-edit-active')).toBe(true);
		expect(mod.getEditorContext()).not.toBeNull();
	});

	it('a second enterEditMode call while already editing is a no-op', async () => {
		await mod.enterEditMode({ activityId: 1, tzOffsetHours: 0 });
		createSession.mockClear();

		await mod.enterEditMode({ activityId: 1, tzOffsetHours: 0 });

		expect(createSession).not.toHaveBeenCalled();
	});

	it('two CONCURRENT enterEditMode calls create only one session (synchronous guard)', async () => {
		// Both start before the first resolves its session load. The guard must be held from enter's first
		// synchronous line — set after the awaits (the bug), both pass the check and build a session + toolbar.
		const p1 = mod.enterEditMode({ activityId: 1, tzOffsetHours: 0 });
		const p2 = mod.enterEditMode({ activityId: 1, tzOffsetHours: 0 });
		await Promise.all([p1, p2]);

		expect(createSession).toHaveBeenCalledTimes(1);
		expect(createToolbar).toHaveBeenCalledTimes(1);
	});

	it('a failed enter releases the guard so a retry can start a fresh session', async () => {
		createSession.mockRejectedValueOnce(new Error('boom'));
		await mod.enterEditMode({ activityId: 1, tzOffsetHours: 0 }); // fails, guard released
		expect(mod.getEditorContext()).toBeNull();

		await mod.enterEditMode({ activityId: 1, tzOffsetHours: 0 }); // retry succeeds
		expect(createSession).toHaveBeenCalledTimes(2);
		expect(mod.getEditorContext()).not.toBeNull();
	});

	it('exitEditMode tears the toolbar/overlay/body-class down and clears the context', async () => {
		await mod.enterEditMode({ activityId: 1, tzOffsetHours: 0 });

		mod.exitEditMode();

		expect(toolbarHandle.destroy).toHaveBeenCalled();
		expect(clearSlotOverlay).toHaveBeenCalled();
		expect(document.body.classList.contains('track-edit-active')).toBe(false);
		expect(mod.getEditorContext()).toBeNull();
	});

	it('resumeSessionId fetches the full session via getSession, not createSession', async () => {
		getSession.mockResolvedValue(sessionResponse({ sessionId: 'sess-resume', cursor: 1 }));
		getSources.mockResolvedValue({
			slots: new Map([[1, sourceTrack(5)]]),
			tzByActivity: new Map([[1, 0]]),
			nameByActivity: new Map([[1, 'Ride 1']]),
		});

		await mod.enterEditMode({ activityId: 1, tzOffsetHours: 0 }, 'sess-resume');

		expect(getSession).toHaveBeenCalledWith('sess-resume');
		expect(createSession).not.toHaveBeenCalled();
	});
});

describe('afterApply() fires after every mutation (finding #7)', () => {
	it('fires on applyOp, undo, and redo — not just some of them', async () => {
		await mod.enterEditMode({ activityId: 1, tzOffsetHours: 0 });
		const ctx = mod.getEditorContext()!;

		const applied = ctx.applyOp({ op: 'cropTo', slot: 0, from: 0, to: 2 });
		expect(applied.ok).toBe(true);
		expect(toolbarHandle.afterApply).toHaveBeenCalledTimes(1);

		const toolbarDeps = must(createToolbar.mock.calls[0], 'toolbar created')[0] as { undo(): void; redo(): void };
		toolbarDeps.undo();
		expect(toolbarHandle.afterApply).toHaveBeenCalledTimes(2);

		toolbarDeps.redo();
		expect(toolbarHandle.afterApply).toHaveBeenCalledTimes(3);
	});

	it('does NOT fire when undo is clamped at the seed floor (no-op, no replay)', async () => {
		await mod.enterEditMode({ activityId: 1, tzOffsetHours: 0 }); // cursor === seedCount already
		const toolbarDeps = must(createToolbar.mock.calls[0], 'toolbar created')[0] as { undo(): void };

		toolbarDeps.undo();

		expect(toolbarHandle.afterApply).not.toHaveBeenCalled();
	});

	it('fires on a slot switch (doSetActiveSlot), driven through the same context/toolbar wiring', async () => {
		await mod.enterEditMode({ activityId: 1, tzOffsetHours: 0 });
		const ctx = mod.getEditorContext()!;
		const split = ctx.applyOp({ op: 'splitAt', slot: 0, index: 2 }); // 5-count slot → two live slots (0, 1)
		expect(split.ok).toBe(true);
		toolbarHandle.afterApply.mockClear();

		ctx.setActiveSlot(1);

		expect(toolbarHandle.afterApply).toHaveBeenCalledTimes(1);
	});

	it('a rejected op applies neither state nor afterApply', async () => {
		await mod.enterEditMode({ activityId: 1, tzOffsetHours: 0 });
		const ctx = mod.getEditorContext()!;

		// out-of-bounds crop: from/to beyond the 5-point source
		const result = ctx.applyOp({ op: 'cropTo', slot: 0, from: 10, to: 20 });

		expect(result.ok).toBe(false);
		expect(toolbarHandle.afterApply).not.toHaveBeenCalled();
	});
});

describe('saveDialogOpen double-dialog guard (finding #7)', () => {
	it('a second requestSave while flush is still pending opens no second dialog', async () => {
		await mod.enterEditMode({ activityId: 1, tzOffsetHours: 0 });
		const ctx = mod.getEditorContext()!;

		// Put a write in flight so queue.flush() genuinely stays pending across the guard window.
		const pendingAppend = deferred<SessionStateResponse>();
		appendOps.mockReturnValueOnce(pendingAppend.promise);
		const applied = ctx.applyOp({ op: 'cropTo', slot: 0, from: 0, to: 2 });
		expect(applied.ok).toBe(true);

		const toolbarDeps = must(createToolbar.mock.calls[0], 'toolbar created')[0] as { requestSave(): void };
		toolbarDeps.requestSave(); // 1st: raises saveDialogOpen synchronously, then awaits flush()
		toolbarDeps.requestSave(); // 2nd: must see the flag already raised and bail out immediately

		pendingAppend.resolve(sessionResponse({ cursor: 2 })); // let the in-flight write (and flush) drain
		await settle();

		expect(openSaveDialog).toHaveBeenCalledTimes(1);
	});

	it('a fresh requestSave after the dialog closes (onCancel) opens a new one', async () => {
		await mod.enterEditMode({ activityId: 1, tzOffsetHours: 0 });
		const toolbarDeps = must(createToolbar.mock.calls[0], 'toolbar created')[0] as { requestSave(): void };

		toolbarDeps.requestSave();
		await settle();
		expect(openSaveDialog).toHaveBeenCalledTimes(1);
		const firstDeps = must(openSaveDialog.mock.calls[0], 'save dialog opened')[0] as { onCancel(): void };
		firstDeps.onCancel();

		toolbarDeps.requestSave();
		await settle();

		expect(openSaveDialog).toHaveBeenCalledTimes(2);
	});
});

describe('navigate-after-save dispatch (finding #7)', () => {
	async function openAndConfirm(mode: 'replace' | 'copy', response: SaveResponse): Promise<void> {
		await mod.enterEditMode({ activityId: 1, tzOffsetHours: 0 });
		saveSession.mockResolvedValue(response);
		const toolbarDeps = must(createToolbar.mock.calls[0], 'toolbar created')[0] as { requestSave(): void };

		toolbarDeps.requestSave();
		await settle();
		const dialogDeps = must(openSaveDialog.mock.calls[0], 'save dialog opened')[0] as { onConfirm(mode: string): void };
		dialogDeps.onConfirm(mode);
		await settle();
	}

	it('replace that keeps the current activity alive reloads it, in-page', async () => {
		await openAndConfirm('replace', {
			commitId: 1, slots: [{ slotId: 0, trackId: 1, name: 'Ride 1', created: false }], deriveQueued: false,
			failedSlots: null,
		});

		expect(navigatePlain).toHaveBeenCalledWith(1);
		expect(mod.getEditorContext()).toBeNull(); // exitEditMode ran before navigating
	});

	it('copy (or replace-after-merge) that lands on a NEW activity navigates to it directly', async () => {
		const reload = vi.fn();
		Object.defineProperty(window, 'location', { value: { href: '', reload }, writable: true });

		await openAndConfirm('copy', {
			commitId: 2, slots: [{ slotId: 0, trackId: 99, name: 'Copy of Ride 1', created: true }], deriveQueued: false,
			failedSlots: null,
		});

		expect(window.location.href).toBe('/Activities/Details/99');
		expect(navigatePlain).not.toHaveBeenCalled();
	});

	it('every produced slot failing sends the user to the activities list, and warns about the trashed inputs', async () => {
		Object.defineProperty(window, 'location', { value: { href: '' }, writable: true });
		const alertSpy = vi.fn();
		vi.stubGlobal('alert', alertSpy);

		await openAndConfirm('replace', {
			commitId: 3, slots: [], deriveQueued: false, failedSlots: [{ slotId: 0, name: 'Merged Ride' }],
		});

		expect(alertSpy).toHaveBeenCalledWith(expect.stringContaining('Merged Ride'));
		expect(window.location.href).toBe('/Activities');
		vi.unstubAllGlobals();
	});

	it('a stale-track save failure opens the stale dialog instead of navigating', async () => {
		// Dynamic import of the (mocked) module this test's `mod` epoch is already using — importActual again
		// here would re-evaluate './edit-api' fresh post-resetModules and mint a DIFFERENT EditApiError class,
		// failing the controller's `instanceof` check although the code and status match.
		const { EditApiError } = await import('./edit-api');
		await mod.enterEditMode({ activityId: 1, tzOffsetHours: 0 });
		saveSession.mockRejectedValue(new EditApiError(409, TRACK_EDIT_API_ERRORS.staleTrack));
		const toolbarDeps = must(createToolbar.mock.calls[0], 'toolbar created')[0] as { requestSave(): void };

		toolbarDeps.requestSave();
		await settle();
		const dialogDeps = must(openSaveDialog.mock.calls[0], 'save dialog opened')[0] as { onConfirm(mode: string): void };
		dialogDeps.onConfirm('replace');
		await settle();

		expect(openStaleDialog).toHaveBeenCalledTimes(1);
		expect(navigatePlain).not.toHaveBeenCalled();
		expect(mod.getEditorContext()).not.toBeNull(); // no exit — the user hasn't chosen discard/keep yet
	});
});
