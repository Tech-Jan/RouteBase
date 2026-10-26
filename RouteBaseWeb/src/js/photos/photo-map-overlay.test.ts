import { describe, it, expect, vi, beforeEach, type Mock } from 'vitest';
import { openPhotoFullscreen, type LightboxOptions, type LightboxPhoto } from './photo-lightbox';
import { PhotoMapOverlay, PHOTO_POINTS_LAYER } from './photo-map-overlay';
import { IMG_PIN } from './photo-icon-registry';

// PU-03: onClusterClick must recheck `enabled` after awaiting getClusterLeaves — a disabled
// overlay (user toggled photos off mid-click) must not spawn a viewer.

vi.mock('./photo-lightbox', () => ({ openPhotoFullscreen: vi.fn() }));

interface Handle { overlay: PhotoMapOverlay; resolve(v: GeoJSON.Feature[]): void; }

function makeOverlay(): Handle {
	// getClusterLeaves is called during onClusterClick (after makeOverlay returns), so expose the
	// resolver via a method that reads it at call time rather than capturing the initial undefined.
	let resolveLeaves: ((v: GeoJSON.Feature[]) => void) | null = null;
	const source = {
		getClusterLeaves: () => new Promise<GeoJSON.Feature[]>((r) => { resolveLeaves = r; }),
	};
	const map = { getSource: () => source, easeTo: vi.fn() } as unknown as maplibregl.Map;
	const overlay = new PhotoMapOverlay({ getMap: () => map });
	return { overlay, resolve: (v) => resolveLeaves!(v) };
}

/** One cluster leaf, in the shape MapLibre hands back from getClusterLeaves. */
const leaf = (photoId: number, trackId: number, lng = 7): GeoJSON.Feature => ({
	type: 'Feature',
	geometry: { type: 'Point', coordinates: [lng, 47] },
	properties: { photoId, trackId, v: 'z' },
});

const clusterEvent = {
	features: [{ type: 'Feature', geometry: { type: 'Point', coordinates: [7, 47] }, properties: { cluster_id: 1 } }],
} as unknown as maplibregl.MapLayerMouseEvent;

const flush = () => new Promise((r) => setTimeout(r, 0));

// Contract change: a cluster click used to open a paged gallery popup on the map. It now opens the
// same fullscreen viewer a photo click does — there is no preview step anywhere in this overlay.
// The PU-03 intent is untouched: after the await, a disabled overlay must open nothing at all.
describe('PhotoMapOverlay.onClusterClick (PU-03)', () => {
	beforeEach(() => vi.clearAllMocks());

	async function clickCluster(disableMidAwait = false) {
		const { overlay, resolve: resolveLeaves } = makeOverlay();
		(overlay as unknown as { enabled: boolean }).enabled = true;
		const p = (overlay as unknown as { onClusterClick(e: unknown): Promise<void> }).onClusterClick(clusterEvent);
		if (disableMidAwait) (overlay as unknown as { enabled: boolean }).enabled = false;
		// Two rides in one cluster — the Activities-overview case.
		resolveLeaves([leaf(5, 10), leaf(6, 11, 7.001)]);
		await p; await flush();
		return (openPhotoFullscreen as Mock).mock.calls[0] as [LightboxPhoto[], number] | undefined;
	}

	it('opens the viewer on the whole cluster when still enabled', async () => {
		const call = await clickCluster();
		expect(call).toBeDefined();
		expect(call![0]).toHaveLength(2); // every leaf, across rides — none unreachable
		expect(call![1]).toBe(0);
	});

	it('opens nothing when disabled during the getClusterLeaves await', async () => {
		expect(await clickCluster(true)).toBeUndefined();
	});
});

// The overlay must complete activation on `style.load`, not the one-shot `load`. When the style is
// mid-reload (terrain apply / basemap switch / bfcache) after `load` already fired, a `once("load")`
// never resolves → overlay stays blank until the user toggles it off/on. This pins the fix.
describe('PhotoMapOverlay activation lifecycle', () => {
	function makeLifecycleMap() {
		let styleLoaded = false;
		let source: { setData(): void } | undefined;
		const once: Record<string, (() => void)[]> = {};
		const calls = { addSource: 0, addLayer: 0 };
		const map = {
			isStyleLoaded: () => styleLoaded,
			once: (ev: string, cb: () => void) => { (once[ev] ??= []).push(cb); },
			on: () => {}, off: () => {},
			hasImage: () => true, addImage: () => {}, removeImage: () => {},
			getSource: () => source,
			addSource: () => { calls.addSource++; source = { setData: () => {} }; },
			addLayer: () => { calls.addLayer++; },
			getLayer: () => ({}),
			getZoom: () => 5,
			getBounds: () => ({ getWest: () => 0, getEast: () => 1, getSouth: () => 0, getNorth: () => 1 }),
			setLayoutProperty: () => {},
		} as unknown as maplibregl.Map;
		return {
			map, calls,
			setStyleLoaded: (v: boolean) => { styleLoaded = v; },
			fire: (ev: string) => (once[ev] ?? []).splice(0).forEach((cb) => cb()),
			hasOnce: (ev: string) => (once[ev] ?? []).length > 0,
		};
	}

	it('defers to style.load (not the one-shot load) and activates when it fires', () => {
		const h = makeLifecycleMap();
		const overlay = new PhotoMapOverlay({ getMap: () => h.map });

		overlay.enable(); // style not loaded yet → must defer without depending on `load`
		expect(h.calls.addSource).toBe(0);
		expect(h.hasOnce('style.load')).toBe(true);
		expect(h.hasOnce('load')).toBe(false);

		h.setStyleLoaded(true);
		h.fire('style.load'); // terrain/basemap style finished (re)loading
		expect(h.calls.addSource).toBe(1); // activated — markers now render
	});

	it('does not stack style.load listeners across repeated activate calls', () => {
		const h = makeLifecycleMap();
		const overlay = new PhotoMapOverlay({ getMap: () => h.map });
		overlay.enable();
		overlay.setMarkers([]); // scheduleRefresh path is RAF-gated; call activate directly instead
		(overlay as unknown as { activate(): void }).activate();
		(overlay as unknown as { activate(): void }).activate();
		const pending = (h as unknown as { fire(ev: string): void });
		// Only one queued handler despite multiple activate attempts while unready.
		h.setStyleLoaded(true);
		pending.fire('style.load');
		expect(h.calls.addSource).toBe(1);
	});
});

// Above THUMB_ZOOM the icons ARE the photos, drawn with icon-allow-overlap on — so photos taken seconds
// apart stack, and a click can only ever hit the topmost feature. Nothing separates them on the map
// (that would cost per-frame work, or lie about where the pin is); instead the click hands the viewer
// the whole ride, and its ‹ › reach whatever was underneath.
//
// Contract change: the click no longer branches on zoom. Below the threshold it used to open an on-map
// preview popup first; a click on a picture now always means "show me that picture".
describe('PhotoMapOverlay.onPointClick', () => {
	const marker = (photoId: number, trackId: number) => ({
		photoId, trackId, lat: 47 + photoId * 1e-5, lng: 7 + photoId * 1e-5,
		locationSource: 'exif', thumbHashBase64: null, v: 'a',
	});
	// Two photos of one ride, seconds apart, plus one from another ride (Activities-overview case).
	const markers = [marker(1, 10), marker(2, 10), marker(3, 11)];

	function clickPhoto(photoId: number, trackId: number, zoom = 16) {
		const easeTo = vi.fn();
		const map = { getZoom: () => zoom, easeTo } as unknown as maplibregl.Map;
		const overlay = new PhotoMapOverlay({ getMap: () => map });
		(overlay as unknown as { markers: unknown[] }).markers = markers;
		(overlay as unknown as { onPointClick(e: unknown): void }).onPointClick({
			features: [{
				type: 'Feature',
				geometry: { type: 'Point', coordinates: [7, 47] },
				properties: { photoId, trackId, v: 'a' },
			}],
		});
		const call = (openPhotoFullscreen as Mock).mock.calls[0] as [LightboxPhoto[], number, LightboxOptions];
		return { easeTo, list: call?.[0], index: call?.[1], opts: call?.[2] };
	}

	beforeEach(() => vi.clearAllMocks());

	it('hands over the clicked photo\'s whole ride, positioned on that photo', () => {
		const { list, index } = clickPhoto(2, 10);
		expect(list).toHaveLength(2); // track 11's photo is another ride — not in this collection
		expect(index).toBe(1);
	});

	it('pans the map to each photo as the viewer pages', () => {
		const { easeTo, opts } = clickPhoto(1, 10);
		opts.onShow!({ url: 'x', lngLat: [8, 48] }, 1);
		expect(easeTo).toHaveBeenCalledWith(expect.objectContaining({ center: [8, 48] }));
	});

	it('still shows a photo the overlay no longer holds', () => {
		const { list, index } = clickPhoto(99, 42);
		expect(list).toHaveLength(1);
		expect(index).toBe(0);
	});

	it('opens the viewer below the thumbnail threshold too — no preview popup step', () => {
		const { list, index } = clickPhoto(2, 10, 11); // anonymous pin, clusters still broken apart
		expect(list).toHaveLength(2);
		expect(index).toBe(1);
	});
});

// The one layer that draws every unclustered photo. Its icon-image expression was verified against
// the MapLibre style spec (validateStyleMin) and its evaluator: at zoom 12 it resolves to the pin
// regardless, at zoom 16 to `photo-thumb-<id>` when that image is registered and the pin when it is
// not. What is pinned here is the structure that behaviour depends on — the parts a well-meaning
// simplification would break silently, since a wrong icon-image throws nothing, it just draws wrong.
describe('PHOTO_POINTS_LAYER icon fallback contract', () => {
	const layout = PHOTO_POINTS_LAYER.layout!;
	const iconImage = layout['icon-image'] as unknown[];

	it('switches on zoom at the thumbnail threshold', () => {
		expect(iconImage[0]).toBe('step');
		expect(iconImage[1]).toEqual(['zoom']);
		expect(iconImage[3]).toBe(14); // THUMB_ZOOM — clusters break apart just below it
	});

	// One pin for every photo now — the per-feature `pin` property went away with the amber
	// estimated-placement variant, so the expression names the registered image directly.
	it('shows the plain pin below the threshold', () => {
		expect(iconImage[2]).toEqual(['image', IMG_PIN]);
	});

	it('prefers the photo icon and falls back to the pin above it — in that order', () => {
		// Reversing these two makes the pin always win, so thumbnails would never appear.
		expect(iconImage[4]).toEqual([
			'coalesce', ['image', ['get', 'icon']], ['image', IMG_PIN],
		]);
	});

	it('keeps collision detection off', () => {
		// Turning this off would reintroduce per-frame symbol collision work, superlinear in count.
		expect(layout['icon-allow-overlap']).toBe(true);
	});

	it('draws only unclustered features', () => {
		expect(PHOTO_POINTS_LAYER.filter).toEqual(['!', ['has', 'point_count']]);
	});
});

// ── The bug this overlay was rebuilt for ────────────────────────────────────────────────────────
//
// Photo thumbnails used to be DOM maplibregl.Markers kept in sync by a `moveend` handler that ran
// querySourceFeatures every time. The replay camera calls jumpTo() once per animation frame and
// jumpTo fires move/moveend SYNCHRONOUSLY, so both the source query and MapLibre's own per-Marker
// work (including a forced terrain depthAtPoint → gl.readPixels GPU stall) ran ~60×/s.
//
// Thumbnails are symbol-layer icons now, so a moving camera must cost effectively nothing.
describe('PhotoMapOverlay under a per-frame replay camera', () => {
	function makeReplayMap() {
		const handlers: Record<string, (() => void)[]> = {};
		const calls = { querySourceFeatures: 0, setData: 0, setLayoutProperty: 0, getBounds: 0 };
		const map = {
			isStyleLoaded: () => true,
			once: () => {},
			on: (ev: string, a: unknown, b?: unknown) => {
				// layer-scoped overload: on(event, layerId, handler)
				const cb = (typeof a === 'function' ? a : b) as () => void;
				(handlers[ev] ??= []).push(cb);
			},
			off: () => {},
			hasImage: () => true, addImage: () => {}, removeImage: () => {},
			getSource: () => ({ setData: () => { calls.setData++; } }),
			addSource: () => {}, addLayer: () => {}, getLayer: () => ({}),
			getZoom: () => 15.5, // replay engages above THUMB_ZOOM — thumbnails are live
			getBounds: () => {
				calls.getBounds++;
				// `contains` is the DOM-marker era's API. It is stubbed so that a regression back to
				// that design fails on the assertions below rather than on a missing stub.
				return {
					getWest: () => 6.9, getEast: () => 7.1, getSouth: () => 46.9, getNorth: () => 47.1,
					contains: () => true,
				};
			},
			querySourceFeatures: () => { calls.querySourceFeatures++; return []; },
			setLayoutProperty: () => { calls.setLayoutProperty++; },
		} as unknown as maplibregl.Map;
		return { map, calls, fire: (ev: string) => (handlers[ev] ?? []).forEach((cb) => cb()) };
	}

	const markers = Array.from({ length: 30 }, (_, i) => ({
		photoId: i + 1, trackId: 1, lat: 47 + i * 0.0001, lng: 7 + i * 0.0001,
		locationSource: 'exif', thumbHashBase64: null, v: 'abc',
	}));

	// Injected so the overlay never reaches the network, and so "how many icon passes ran" is
	// observable directly rather than inferred.
	function fakeIcons() {
		const ensure = vi.fn();
		return { registry: { ensure, reset: vi.fn(), destroy: vi.fn() }, ensure };
	}

	function mountedOverlay(h: ReturnType<typeof makeReplayMap>, icons: ReturnType<typeof fakeIcons>) {
		const overlay = new PhotoMapOverlay({ getMap: () => h.map, icons: icons.registry });
		overlay.enable();
		(overlay as unknown as { markers: unknown[] }).markers = markers;
		(overlay as unknown as { activate(): void }).activate();
		return overlay;
	}

	let markerCtor: ReturnType<typeof vi.fn>;
	beforeEach(() => {
		markerCtor = vi.fn(() => ({ setLngLat: () => ({ addTo: () => ({}) }), remove: () => {} }));
		(globalThis as unknown as { maplibregl: unknown }).maplibregl = { Marker: markerCtor };
	});

	it('does not query the source, rewrite layout or build DOM markers as the camera moves', () => {
		const h = makeReplayMap();
		const icons = fakeIcons();
		mountedOverlay(h, icons);

		const baseline = { ...h.calls };
		// One replay second at 60 fps: jumpTo fires move + moveend on every frame.
		for (let i = 0; i < 60; i++) h.fire('moveend');

		expect(h.calls.querySourceFeatures).toBe(0);
		expect(h.calls.setLayoutProperty).toBe(baseline.setLayoutProperty);
		expect(h.calls.setData).toBe(baseline.setData); // no source rewrite per frame either
		// The whole point: zero DOM markers, so zero Marker._update and zero terrain depth readbacks.
		expect(markerCtor).not.toHaveBeenCalled();
	});

	it('coalesces the icon top-up instead of running it every frame', () => {
		const h = makeReplayMap();
		const icons = fakeIcons();
		mountedOverlay(h, icons);

		const afterActivate = icons.ensure.mock.calls.length;
		for (let i = 0; i < 60; i++) h.fire('moveend');

		// The throttle is 200 ms and 60 synthetic frames take far less, so the whole burst collapses
		// to at most one extra pass — not 60.
		expect(icons.ensure.mock.calls.length - afterActivate).toBeLessThanOrEqual(1);
	});
});
