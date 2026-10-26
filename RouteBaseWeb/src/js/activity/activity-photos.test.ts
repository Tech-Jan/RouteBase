import { describe, it, expect, vi, beforeEach, afterEach, type Mock } from 'vitest';
import { must } from '../core/invariant';
import { openPhotoFullscreen } from '../photos/photo-lightbox';

// B5: the share photo panel is capped server-side (share payload bounds) and never pages
// further, so a Total that exceeds what came back must be surfaced, not silently dropped.

vi.mock('../photos/photo-map-overlay', () => ({
	THUMB_ZOOM: 14,
	PhotoMapOverlay: vi.fn().mockImplementation(function (this: Record<string, unknown>) {
		this.setMarkers = vi.fn();
		this.enable = vi.fn();
		this.disable = vi.fn();
		this.isEnabled = vi.fn(() => false);
	}),
}));

vi.mock('../photos/photo-lightbox', () => ({ openPhotoFullscreen: vi.fn() }));

// The panel reads its map from map-state and builds popups off the `maplibregl` page global; both
// are stubbed so the placement path can run for real (arm → map click → POST → confirmation popup).
const mapStub = {
	on: vi.fn(),
	off: vi.fn(),
	flyTo: vi.fn(),
	easeTo: vi.fn(),
	getZoom: vi.fn(() => 12),
	getCanvas: vi.fn(() => ({ style: {} })),
};
vi.mock('../core/map-state', () => ({ getMaplibreMap: () => mapStub }));

/** A maplibregl.Popup with the surface activity-photos uses; remove() fires 'close' like the real one. */
class FakePopup {
	static last: FakePopup | null = null;
	lngLat: [number, number] | null = null;
	removed = false;
	private el = document.createElement('div');
	private closeCbs: (() => void)[] = [];
	constructor() { FakePopup.last = this; }
	setLngLat(ll: [number, number]): this { this.lngLat = ll; return this; }
	setHTML(html: string): this { this.el.innerHTML = html; return this; }
	addTo(): this { return this; }
	getElement(): HTMLElement { return this.el; }
	on(type: string, cb: () => void): this { if (type === 'close') this.closeCbs.push(cb); return this; }
	remove(): void { this.removed = true; this.closeCbs.splice(0).forEach((cb) => cb()); }
}

function photoRow(id: number, extra: Record<string, unknown> = {}) {
	return { photoId: id, trackId: 1, thumbHashBase64: null, markerLat: null, markerLng: null,
		locationSource: null, canRestore: false, capturedUtc: null, tzOffsetHours: null,
		origFilename: null, description: null, v: 'v1', ...extra };
}

function stubPhotos(items: unknown[], total = items.length): void {
	vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
		ok: true,
		json: async () => ({ total, page: 1, pageSize: 200, items }),
	}));
}

describe('activity-photos truncation notice', () => {
	beforeEach(() => {
		vi.resetModules();
		document.body.innerHTML = `
			<div id="detPhotosTab"></div>
			<div id="detPhotosBody"></div>
			<div id="detPhotosGrid"></div>
			<span id="detPhotosCount"></span>
			<p id="detPhotosTruncated" hidden></p>
			<p id="detPhotosEmpty" hidden></p>
			<button id="detPhotosMapToggle" aria-pressed="false"></button>
		`;
	});

	afterEach(() => {
		vi.unstubAllGlobals();
	});

	it('shows "showing first N of Total" when the server reports more than it returned', async () => {
		const items = Array.from({ length: 3 }, (_, i) => photoRow(i));
		vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
			ok: true,
			json: async () => ({ total: 5, page: 1, pageSize: 200, items }),
		}));

		const { setupActivityPhotos } = await import('./activity-photos');
		setupActivityPhotos(1, false, '/s/tok/api/photos');
		await vi.waitFor(() => expect(document.getElementById('detPhotosGrid')!.children.length).toBe(3));

		const notice = document.getElementById('detPhotosTruncated') as HTMLElement;
		expect(notice.hidden).toBe(false);
		expect(notice.textContent).toBe('Showing first 3 of 5 photos');
	});

	it('stays hidden when every photo came back', async () => {
		const items = Array.from({ length: 3 }, (_, i) => photoRow(i));
		stubPhotos(items, 3);

		const { setupActivityPhotos } = await import('./activity-photos');
		setupActivityPhotos(1, false, '/api/photos');
		await vi.waitFor(() => expect(document.getElementById('detPhotosGrid')!.children.length).toBe(3));

		const notice = document.getElementById('detPhotosTruncated') as HTMLElement;
		expect(notice.hidden).toBe(true);
	});
});

// The grid, the map markers and the full-size viewer all page ONE array in ONE order, so an index means
// the same photo in all three. The API hands back newest-first; on a single ride the useful reading is
// along the ride, so the panel flips it once on load.
describe('activity-photos viewer', () => {
	beforeEach(() => {
		vi.resetModules();
		vi.clearAllMocks();
		document.body.innerHTML = `
			<div id="detPhotosTab"></div>
			<div id="detPhotosBody"></div>
			<div id="detPhotosGrid"></div>
			<span id="detPhotosCount"></span>
			<p id="detPhotosTruncated" hidden></p>
			<p id="detPhotosEmpty" hidden></p>
			<button id="detPhotosMapToggle" aria-pressed="false"></button>
		`;
	});

	afterEach(() => vi.unstubAllGlobals());

	async function mount(items: unknown[]): Promise<HTMLElement> {
		stubPhotos(items);
		const { setupActivityPhotos } = await import('./activity-photos');
		setupActivityPhotos(1, false, '/api/photos');
		const grid = document.getElementById('detPhotosGrid')!;
		await vi.waitFor(() => expect(grid.children.length).toBe(items.length));
		return grid as HTMLElement;
	}

	const dated = (id: number, capturedUtc: string) => photoRow(id, { capturedUtc, origFilename: `f${id}.jpg` });

	it('renders the grid oldest-first, whichever order the API used', async () => {
		const grid = await mount([
			dated(3, '2026-07-24T10:30:00Z'), // newest-first, as PhotoQueryService returns it
			dated(2, '2026-07-24T10:15:00Z'),
			dated(1, '2026-07-24T10:00:00Z'),
		]);
		const alts = [...grid.querySelectorAll('img')].map((i) => i.getAttribute('alt'));
		expect(alts).toEqual(['f1.jpg', 'f2.jpg', 'f3.jpg']);
	});

	it('sinks undated photos to the end rather than jumbling the ride', async () => {
		const grid = await mount([dated(2, '2026-07-24T10:15:00Z'), photoRow(9, { origFilename: 'f9.jpg' }), dated(1, '2026-07-24T10:00:00Z')]);
		const alts = [...grid.querySelectorAll('img')].map((i) => i.getAttribute('alt'));
		expect(alts).toEqual(['f1.jpg', 'f2.jpg', 'f9.jpg']);
	});

	// One tap, and a close button that is always there. This used to open a MapLibre popup whose × is a
	// small dark glyph that vanishes over a dark photo, with fullscreen only a second tap away.
	it('opens the full-size viewer on the first tap, positioned on the tapped photo', async () => {
		const grid = await mount([
			dated(1, '2026-07-24T10:00:00Z'),
			dated(2, '2026-07-24T10:15:00Z'),
			dated(3, '2026-07-24T10:30:00Z'),
		]);

		(grid.children[1] as HTMLElement).dispatchEvent(new MouseEvent('click', { bubbles: true }));

		const [list, index] = must((openPhotoFullscreen as Mock).mock.calls[0], 'one fullscreen open');
		expect(list).toHaveLength(3); // the whole activity — the arrows walk the ride
		expect(index).toBe(1);
		expect(must(list[1], 'photo 1 of 3').url).toContain('/api/photos/2/image');
	});

	// The caption used to live in the map popup this path no longer opens, so the viewer has to carry it.
	it('carries the caption into the viewer', async () => {
		const grid = await mount([photoRow(1, { description: 'Summit' })]);
		(grid.children[0] as HTMLElement).dispatchEvent(new MouseEvent('click', { bubbles: true }));
		expect(must(must((openPhotoFullscreen as Mock).mock.calls[0], 'one fullscreen open')[0][0], 'first photo').caption).toBe('Summit');
	});

	it('passes a null caption for a photo without one', async () => {
		const grid = await mount([photoRow(1)]);
		(grid.children[0] as HTMLElement).dispatchEvent(new MouseEvent('click', { bubbles: true }));
		expect(must(must((openPhotoFullscreen as Mock).mock.calls[0], 'one fullscreen open')[0][0], 'first photo').caption).toBeNull();
	});

	// Unplaced photos have no map location; they must still open, just without a pan.
	it('opens an unplaced photo with no map location instead of skipping it', async () => {
		const grid = await mount([photoRow(1)]);
		(grid.children[0] as HTMLElement).dispatchEvent(new MouseEvent('click', { bubbles: true }));
		expect(must(must((openPhotoFullscreen as Mock).mock.calls[0], 'one fullscreen open')[0][0], 'first photo').lngLat).toBeNull();
	});

	// Contract change: this used to also raise the zoom to THUMB_ZOOM. The map overlay's own onShow pans
	// centre-only ("the user's zoom stays theirs"), and under a replay the viewer hands the camera back
	// at whatever framing it leaves behind — so a glance at a photo must not re-zoom the ride.
	it('pans to the photo without touching the zoom', async () => {
		const grid = await mount([photoRow(1, { markerLat: 47, markerLng: 7 })]);
		(grid.children[0] as HTMLElement).dispatchEvent(new MouseEvent('click', { bubbles: true }));

		const opts = must((openPhotoFullscreen as Mock).mock.calls[0], 'one fullscreen open')[2];
		opts.onShow({ url: 'u', lngLat: [7, 47] }, 0);

		expect(mapStub.easeTo).toHaveBeenCalledWith({ center: [7, 47], duration: 400 });
	});
});

// A MapLibre Popup is anchored to a coordinate, so a replay camera drags it across the screen every
// frame. Replay start calls closeAllActionPanels — that has to take the placement popup with it, or the
// user is left chasing a 60 Hz moving × (core/map-user-gesture only covers the deliberate map gesture).
describe('activity-photos placement popup', () => {
	beforeEach(() => {
		vi.resetModules();
		vi.clearAllMocks();
		FakePopup.last = null;
		vi.stubGlobal('maplibregl', { Popup: FakePopup });
		document.body.innerHTML = `
			<div id="detPhotosTab"></div>
			<div id="detPhotosBody"></div>
			<div id="detPhotosGrid"></div>
			<span id="detPhotosCount"></span>
			<p id="detPhotosTruncated" hidden></p>
			<p id="detPhotosEmpty" hidden></p>
			<button id="detPhotosMapToggle" aria-pressed="false"></button>
			<div id="mapCard"></div>
		`;
	});

	afterEach(() => vi.unstubAllGlobals());

	/** Drive a real placement: arm it from the pin button, then fire the map click it registered. */
	async function placeAPhoto() {
		vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
			ok: true,
			json: async () => ({ total: 1, page: 1, pageSize: 200, items: [photoRow(1)] }),
		}));
		const { setupActivityPhotos } = await import('./activity-photos');
		const { closeAllActionPanels } = await import('./details-shared');
		setupActivityPhotos(1, true, '/api/photos');
		const grid = document.getElementById('detPhotosGrid')!;
		await vi.waitFor(() => expect(grid.children.length).toBe(1));

		(grid.querySelector('.det-photo-place-btn') as HTMLElement).dispatchEvent(
			new MouseEvent('click', { bubbles: true }));
		const onMapClick = mapStub.on.mock.calls.find((c) => c[0] === 'click')![1] as
			(e: { lngLat: { lat: number; lng: number } }) => void;
		onMapClick({ lngLat: { lat: 47, lng: 7 } });
		await vi.waitFor(() => expect(FakePopup.last).not.toBeNull());

		return { closeAllActionPanels };
	}

	it('opens a confirmation popup where the pin was dropped', async () => {
		await placeAPhoto();
		expect(FakePopup.last!.lngLat).toEqual([7, 47]);
		expect(FakePopup.last!.removed).toBe(false);
	});

	it('drops that popup when the panels close — a replay start must not leave it riding the camera', async () => {
		const { closeAllActionPanels } = await placeAPhoto();

		closeAllActionPanels();

		expect(FakePopup.last!.removed).toBe(true);
	});
});
