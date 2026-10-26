import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest';
import { wireTrackEditEntry, resumeLabel, readResumeHint, type TrackEditEntryConfig } from './edit-mode-entry';

// The controller is the only thing the entry may load on click; stub it so the test never boots the editor.
const enterEditMode = vi.fn();
vi.mock('./edit-mode-controller', () => ({ enterEditMode: (...args: unknown[]) => enterEditMode(...args) }));

const discardSession = vi.fn((_id: string) => Promise.resolve());
vi.mock('./edit-api', () => ({ discardSession: (id: string) => discardSession(id) }));

const settle = (): Promise<void> => new Promise((resolve) => setTimeout(resolve, 50));

function config(): TrackEditEntryConfig {
	return { activityId: 42, isOwner: true, tzOffsetHours: 2 };
}

/** What Details renders when the ride is FREE: the plain editor door. */
function renderFree(): void {
	document.body.innerHTML = '<button id="trackEditEnterBtn">Edit track</button>';
}

/** What Details renders when the ride is CHECKED OUT: the same slot, two different buttons (EditorSurfaces). */
function renderCheckedOut(sessionId: string, edits: number, updated: string): void {
	document.body.innerHTML =
		`<button id="trackEditResumeBtn" data-resume-session-id="${sessionId}" ` +
		`data-resume-edits="${edits}" data-resume-updated="${updated}">` +
		'<span id="trackEditResumeLabel"></span></button>' +
		'<button id="trackEditDiscardOpenBtn">Discard edit</button>';
}

const fetchSpy = vi.fn();

beforeEach(() => {
	document.body.innerHTML = '';
	enterEditMode.mockClear();
	discardSession.mockClear();
	fetchSpy.mockClear();
	vi.stubGlobal('fetch', fetchSpy);
	window.history.replaceState({}, '', '/Activities/Details/42');
});

afterEach(() => vi.unstubAllGlobals());

describe('the editor door', () => {
	it('labels Resume from the server-rendered hint, without any HTTP request', async () => {
		renderCheckedOut('sess-1', 3, new Date(Date.now() - 2 * 60_000).toISOString());

		wireTrackEditEntry(config());
		await settle();

		expect(document.getElementById('trackEditResumeLabel')?.textContent)
			.toBe('Resume edit · 3 changes, 2 min ago');
		expect(fetchSpy).not.toHaveBeenCalled(); // the page-load lookup is gone (F-1 done-when)
	});

	it('Resume hands the controller the session id — the full session loads only on click', async () => {
		renderCheckedOut('sess-7', 1, new Date().toISOString());
		wireTrackEditEntry(config());

		document.getElementById('trackEditResumeBtn')?.click();
		await settle();

		expect(enterEditMode).toHaveBeenCalledWith({ activityId: 42, tzOffsetHours: 2 }, 'sess-7');
		expect(fetchSpy).not.toHaveBeenCalled(); // the controller (stubbed here) owns the one GET
	});

	// Abandoning without entering: deleting the session row releases the ride's checkout pointer, which is what
	// un-blocks the history panel. Nothing else has to know.
	it('Discard edit deletes the session and reloads', async () => {
		renderCheckedOut('sess-9', 2, new Date().toISOString());
		const reload = vi.fn();
		Object.defineProperty(window, 'location', { value: { reload, search: '' }, writable: true });
		vi.stubGlobal('confirm', () => true);
		wireTrackEditEntry(config());

		document.getElementById('trackEditDiscardOpenBtn')?.click();
		await settle();

		expect(discardSession).toHaveBeenCalledWith('sess-9');
		expect(reload).toHaveBeenCalled();
		expect(enterEditMode).not.toHaveBeenCalled(); // discarding is not entering
	});

	it('a free ride offers Edit, and costs zero extra requests', async () => {
		renderFree();
		wireTrackEditEntry(config());
		await settle();

		expect(readResumeHint()).toBeNull();
		document.getElementById('trackEditEnterBtn')?.click();
		await settle();

		expect(enterEditMode).toHaveBeenCalledWith({ activityId: 42, tzOffsetHours: 2 }, undefined);
		expect(fetchSpy).not.toHaveBeenCalled();
	});

	it('a non-owner wires nothing', async () => {
		renderCheckedOut('sess-9', 1, new Date().toISOString());

		wireTrackEditEntry({ ...config(), isOwner: false });
		document.getElementById('trackEditResumeBtn')?.click();
		await settle();

		expect(enterEditMode).not.toHaveBeenCalled();
	});
});

describe('resumeLabel', () => {
	it('counts changes, and does not say "1 changes"', () => {
		const at = new Date(Date.now() - 3 * 3600_000).toISOString();
		expect(resumeLabel({ sessionId: 's', editCount: 1, updatedAt: at })).toBe('Resume edit · 1 change, 3 h ago');
		expect(resumeLabel({ sessionId: 's', editCount: 4, updatedAt: at })).toBe('Resume edit · 4 changes, 3 h ago');
	});
});
