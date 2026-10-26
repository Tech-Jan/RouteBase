import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// GP-02: ensureData() must dedupe concurrent fetches — a toggle-on that arrives while another fetch
// is already in flight must await the *same* promise instead of returning early with allMarkers still
// null, which would enable() the overlay with zero markers set (analysis-grids-page.md GP-02).

vi.mock('./activities-overview', () => ({
	getActivitiesMapShell: vi.fn(() => null),
	getVisibleTrackIds: vi.fn(() => [1, 2]),
	onVisibleTracksChanged: vi.fn(),
}));

vi.mock('../photos/photo-map-overlay', () => ({
	PhotoMapOverlay: vi.fn().mockImplementation(function (this: Record<string, unknown>) {
		this.setMarkers = vi.fn();
		this.enable = vi.fn();
		this.disable = vi.fn();
	}),
}));

describe('activities-photos-overlay', () => {
	beforeEach(() => {
		vi.resetModules();
		document.body.innerHTML = '<button id="actPhotosToggle"></button>';
	});

	afterEach(() => {
		vi.unstubAllGlobals();
	});

	it('concurrent toggle-on calls share one fetch and both see the resolved markers', async () => {
		let resolveFetch: (v: unknown) => void = () => {};
		const fetchFn = vi.fn(() => new Promise((resolve) => { resolveFetch = resolve; }));
		vi.stubGlobal('fetch', fetchFn as unknown as typeof fetch);

		const { setupPhotosOverlay } = await import('./activities-photos-overlay');
		const { PhotoMapOverlay } = await import('../photos/photo-map-overlay');
		setupPhotosOverlay();
		const overlayInstance = vi.mocked(PhotoMapOverlay).mock.results[0]!.value;

		const btn = document.getElementById('actPhotosToggle') as HTMLButtonElement;
		btn.click(); // toggle on — starts the fetch, ensureData() suspends awaiting it
		btn.click(); // toggle off — synchronous, no fetch
		btn.click(); // toggle on again — ensureData() must await the SAME in-flight fetch, not start a new one

		resolveFetch({ ok: true, json: async () => ({ items: [{ trackId: 1 }, { trackId: 2 }] }) });
		await vi.waitFor(() => expect(overlayInstance.setMarkers).toHaveBeenCalled());

		expect(fetchFn).toHaveBeenCalledTimes(1);
		expect(overlayInstance.setMarkers).toHaveBeenCalledWith([{ trackId: 1 }, { trackId: 2 }]);
	});
});
