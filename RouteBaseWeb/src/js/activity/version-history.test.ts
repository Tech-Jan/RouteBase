import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest';

// The panel wires itself on import, so each case must re-import it against a freshly built DOM.
async function mountPanel(attrs: Record<string, string>): Promise<void> {
	const data = Object.entries(attrs).map(([k, v]) => `${k}="${v}"`).join(' ');
	document.body.innerHTML =
		`<div id="versionHistoryPanel" ${data}>
			<div class="det-notice" id="versionCheckedOutNotice" hidden></div>
			<ul class="version-list" id="versionList"></ul>
			<div class="det-notice" id="versionStatus">Loading…</div>
			<div class="version-panel-actions" id="versionPanelActions" hidden>
				<button id="versionResetBtn"></button>
			</div>
			<button id="parkToggleBtn" hidden></button>
		</div>`;
	vi.resetModules();
	await import('./version-history');
	await new Promise((resolve) => setTimeout(resolve, 0)); // let any fetch settle
}

const fetchSpy = vi.fn();

beforeEach(() => {
	fetchSpy.mockReset();
	fetchSpy.mockResolvedValue({ ok: true, json: () => Promise.resolve({ versions: [] }) });
	vi.stubGlobal('fetch', fetchSpy);
});

afterEach(() => vi.unstubAllGlobals());

const status = (): string => document.getElementById('versionStatus')?.textContent ?? '';

describe('the history panel decides whether to fetch at all', () => {
	// The point of the change: most rides are imported once and never edited, and the panel used to spend an
	// HTTP round-trip on EVERY owner page load just to learn it had nothing to show.
	it('makes NO request for a never-edited ride', async () => {
		await mountPanel({
			'data-track-id': '7', 'data-lifecycle': 'released', 'data-checked-out': 'false',
			'data-version-count': '1', 'data-any-commit': 'false',
		});

		expect(fetchSpy).not.toHaveBeenCalled();
		expect(status()).toContain('has not been edited');
	});

	it('fetches once the ride has more than one version', async () => {
		await mountPanel({
			'data-track-id': '7', 'data-lifecycle': 'released', 'data-checked-out': 'false',
			'data-version-count': '3', 'data-any-commit': 'true',
		});

		expect(fetchSpy).toHaveBeenCalledWith('/api/activities/7/versions');
	});

	// A merge product has ONE version — the merge's. It has a history (it can be un-merged), so the panel must
	// still fetch, or the "Undo this save" row never renders and the ride can never be un-merged.
	it('fetches for a ride BORN from a save, despite its single version', async () => {
		await mountPanel({
			'data-track-id': '9', 'data-lifecycle': 'released', 'data-checked-out': 'false',
			'data-version-count': '1', 'data-any-commit': 'true',
		});

		expect(fetchSpy).toHaveBeenCalledWith('/api/activities/9/versions');
	});

	// Missing/garbled attributes must not silently hide a real history — fall back to asking the server.
	it('fetches when the server facts are absent', async () => {
		await mountPanel({
			'data-track-id': '7', 'data-lifecycle': 'released', 'data-checked-out': 'false',
		});

		expect(fetchSpy).toHaveBeenCalledWith('/api/activities/7/versions');
	});
});
