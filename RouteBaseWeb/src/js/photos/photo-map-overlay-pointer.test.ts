import { describe, it, expect, vi, beforeEach, afterEach, type Mock } from 'vitest';
import { PhotoMapOverlay } from './photo-map-overlay';
import { IMG_PIN } from './photo-icon-registry';
import { openPhotoFullscreen, type LightboxPhoto } from './photo-lightbox';
import { createFakeMap, type FakeMap } from '../core/fake-map-harness';

// The overlay's FIRST pointer-path test: a click delivered to the real listener the overlay attaches
// (map.on('click', 'photo-overlay-points', …)), through the shared fake map's layer-scoped emit — not by
// reaching into the private onPointClick. This is the seam d969e9ef reshaped (DOM markers → symbol
// layer): the whole click journey now runs through MapLibre's dispatch, so it is the part worth pinning.

vi.mock('./photo-lightbox', () => ({ openPhotoFullscreen: vi.fn() }));

const L_POINTS = 'photo-overlay-points';
const L_CLUSTERS = 'photo-overlay-clusters';

const markers = [
	{ photoId: 1, trackId: 10, lat: 47.01, lng: 7.01, locationSource: 'exif', thumbHashBase64: null, v: 'a' },
	{ photoId: 2, trackId: 10, lat: 47.02, lng: 7.02, locationSource: 'exif', thumbHashBase64: null, v: 'a' },
	{ photoId: 3, trackId: 11, lat: 47.03, lng: 7.03, locationSource: 'exif', thumbHashBase64: null, v: 'a' },
];

function fakeIcons() {
	return { ensure: vi.fn(), reset: vi.fn(), destroy: vi.fn() };
}

/** A mounted overlay + the harness it drives. Any zoom: a point click opens the viewer. */
function mount(zoom: number): FakeMap {
	const h = createFakeMap({ zoom, styleLoaded: true });
	h.images.add(IMG_PIN); // skip the canvas pin-image build (jsdom has no 2d ctx)
	const overlay = new PhotoMapOverlay({ getMap: () => h.map, icons: fakeIcons() });
	overlay.enable();
	(overlay as unknown as { markers: unknown[] }).markers = markers;
	(overlay as unknown as { activate(): void }).activate();
	return h;
}

/** The feature MapLibre hands a click on the points layer — geometry + the properties featToMarker reads. */
function pointFeature(photoId: number, trackId: number) {
	const m = markers.find((x) => x.photoId === photoId);
	return {
		features: [{
			type: 'Feature',
			geometry: { type: 'Point', coordinates: [m?.lng ?? 7, m?.lat ?? 47] },
			properties: { photoId, trackId, v: 'a' },
		}],
	};
}

beforeEach(() => vi.clearAllMocks());
afterEach(() => vi.restoreAllMocks());

describe('PhotoMapOverlay point click through the real listener', () => {
	it('attaches a click listener on the points layer once mounted', () => {
		const h = mount(16);
		expect(h.hasListener('click', L_POINTS)).toBe(true);
	});

	it('opens the viewer on the clicked photo\'s ride when a points-layer click arrives', () => {
		const h = mount(16);
		h.emitLayer('click', L_POINTS, pointFeature(2, 10));

		const call = (openPhotoFullscreen as Mock).mock.calls[0] as [LightboxPhoto[], number] | undefined;
		expect(call).toBeDefined();
		expect(call![0]).toHaveLength(2); // track 10's two photos, not track 11's
		expect(call![1]).toBe(1);         // started on photo 2
	});

	// Below the thumbnail threshold the icon is an anonymous pin — the click still goes straight to
	// fullscreen, through the same real dispatch. No preview popup anywhere on this path.
	it('opens the viewer from a pin click below the thumbnail threshold', () => {
		const h = mount(11);
		h.emitLayer('click', L_POINTS, pointFeature(2, 10));

		expect((openPhotoFullscreen as Mock)).toHaveBeenCalledTimes(1);
	});

	it('toggles the canvas cursor on mouseenter/mouseleave of the points layer', () => {
		const h = mount(16);
		h.emitLayer('mouseenter', L_POINTS);
		expect(h.map.getCanvas().style.cursor).toBe('pointer');
		h.emitLayer('mouseleave', L_POINTS);
		expect(h.map.getCanvas().style.cursor).toBe('');
	});
});

// The reported bug: a symbol-layer photo icon obeys MapLibre's ~3 px mouse `clickTolerance`, so a
// drift-prone phone tap registers as a pan and never fires the layer `click` the viewer opens on.
// The fix is a touch tap handler with a finger-sized threshold (TAP_MAX_PX = 12), driven here through
// the SAME real dispatch the app uses: a map-level touchstart records where the finger landed, a
// layer-scoped touchend decides tap-vs-pan. jsdom has no Touch/multi-touch PointerEvent, so the
// harness's emit/emitLayer deliver MapLibre-shaped {point, points, features} events directly.
const touchStart = (x: number, y: number, extraFingers = 0) => ({
	point: { x, y },
	points: [{ x, y }, ...Array.from({ length: extraFingers }, (_, i) => ({ x: x + 100 + i, y }))],
});
const touchEndOnPoint = (x: number, y: number, photoId: number, trackId: number) => ({
	point: { x, y },
	...pointFeature(photoId, trackId),
});
const clusterTouchEnd = (x: number, y: number) => ({
	point: { x, y },
	features: [{ type: 'Feature', geometry: { type: 'Point', coordinates: [7, 47] }, properties: { cluster_id: 1 } }],
});
const flush = () => new Promise((r) => setTimeout(r, 0));

describe('PhotoMapOverlay touch tap (finger-sized, past MapLibre\'s 3px mouse tolerance)', () => {
	it('attaches touch listeners on both interactive layers once mounted', () => {
		const h = mount(16);
		expect(h.hasListener('touchstart')).toBe(true);
		expect(h.hasListener('touchend', L_POINTS)).toBe(true);
		expect(h.hasListener('touchend', L_CLUSTERS)).toBe(true);
	});

	it('opens the viewer on a 3px-drift touch tap of a photo icon', () => {
		const h = mount(16);
		h.emit('touchstart', touchStart(100, 100));
		h.emitLayer('touchend', L_POINTS, touchEndOnPoint(103, 100, 2, 10)); // 3px
		expect(openPhotoFullscreen as Mock).toHaveBeenCalledTimes(1);
	});

	// The actual bug: ~10px of finger drift is a pan to MapLibre (no click), a tap to a human.
	it('opens the viewer on a ~11px sloppy tap that MapLibre would swallow as a pan', () => {
		const h = mount(16);
		h.emit('touchstart', touchStart(100, 100));
		h.emitLayer('touchend', L_POINTS, touchEndOnPoint(110, 105, 2, 10)); // ~11.2px
		const call = (openPhotoFullscreen as Mock).mock.calls[0] as [LightboxPhoto[], number] | undefined;
		expect(call).toBeDefined();
		expect(call![0]).toHaveLength(2); // track 10's ride
		expect(call![1]).toBe(1);
	});

	it('does NOT open on a 30px drag — that is a pan, not a tap', () => {
		const h = mount(16);
		h.emit('touchstart', touchStart(100, 100));
		h.emitLayer('touchend', L_POINTS, touchEndOnPoint(130, 100, 2, 10)); // 30px
		expect(openPhotoFullscreen as Mock).not.toHaveBeenCalled();
	});

	it('does NOT open on a two-finger gesture that happens to end over a photo (a pinch)', () => {
		const h = mount(16);
		h.emit('touchstart', touchStart(100, 100, 1)); // two fingers down
		h.emitLayer('touchend', L_POINTS, touchEndOnPoint(101, 100, 2, 10));
		expect(openPhotoFullscreen as Mock).not.toHaveBeenCalled();
	});

	// A clean tap fires BOTH our touchend AND MapLibre's own layer click. Exactly one viewer.
	it('opens exactly once when a clean tap also fires MapLibre\'s click', () => {
		const h = mount(16);
		h.emit('touchstart', touchStart(100, 100));
		h.emitLayer('touchend', L_POINTS, touchEndOnPoint(101, 100, 2, 10)); // our path opens it
		h.emitLayer('click', L_POINTS, pointFeature(2, 10));                 // MapLibre's synthesized click
		expect(openPhotoFullscreen as Mock).toHaveBeenCalledTimes(1);
	});

	// The mouse path must keep the tight tolerance: a layer click with no preceding touch still opens
	// (nothing raised the global clickTolerance), and a suppression from a prior tap never leaks here.
	it('leaves the mouse click path unchanged', () => {
		const h = mount(16);
		h.emitLayer('click', L_POINTS, pointFeature(2, 10));
		expect(openPhotoFullscreen as Mock).toHaveBeenCalledTimes(1);
	});

	it('opens the cluster viewer on a sloppy tap of a cluster badge', async () => {
		const h = mount(11); // below THUMB_ZOOM: photos are clustered
		h.emit('touchstart', touchStart(100, 100));
		h.emitLayer('touchend', L_CLUSTERS, clusterTouchEnd(109, 100)); // 9px
		await flush(); // getClusterLeaves resolves on a microtask
		expect(openPhotoFullscreen as Mock).toHaveBeenCalledTimes(1);
	});
});

// disable() during the style wait must clear `awaitingStyle`, or the `if (this.awaitingStyle) return`
// guard in activate() swallows a later re-enable and the overlay stays blank for good (the map may have
// consumed its style.load/idle one-shots while disabled). Driven through the shared harness's own
// isStyleLoaded/once/fireOnce so it exercises the real activation path, not a bespoke stub.
describe('PhotoMapOverlay style-wait re-enable after disable', () => {
	it('re-activates when re-enabled while still unstyled', () => {
		const h = createFakeMap({ styleLoaded: false });
		h.images.add(IMG_PIN);
		const overlay = new PhotoMapOverlay({ getMap: () => h.map, icons: fakeIcons() });
		const awaiting = () => (overlay as unknown as { awaitingStyle: boolean }).awaitingStyle;

		overlay.enable();
		expect(awaiting()).toBe(true);
		expect(h.sources.size).toBe(0); // deferred — style not ready

		overlay.disable();
		expect(awaiting()).toBe(false); // the fix: the pending wait is cleared

		overlay.enable(); // still unstyled → must re-arm the wait rather than early-return
		h.setStyleLoaded(true);
		h.fireOnce('style.load');
		expect(h.sources.size).toBe(1); // finally activated — the source is added
	});
});
