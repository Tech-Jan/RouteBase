/**
 * fake-map-harness.ts — one shared fake MapLibre map for the interaction tests.
 *
 * Test-only (nothing in the app imports it). Before this, 13+ test files each rolled their own map
 * stub — most only as much of it as one assertion needed — so a change to how a module drives the map
 * (a new handler, a layer-scoped click, a shielded jumpTo) had to be re-stubbed everywhere. This is the
 * union of those stubs, in one place:
 *
 *  - the 8 gesture handlers, each an { enable, disable } spy plus `_updatingCamera` on `handlers`
 *    (the field withHandlersShielded borrows), so a test can read WHICH handlers a module toggled;
 *  - the camera writes (jumpTo / easeTo / flyTo) as spies, with getters for the current view;
 *  - a real event registry with MapLibre's layer-scoped `on(type, layerId, cb)` overload, driven by
 *    `emit()` / `emitLayer()` — so a click on the photo layer can be delivered the way MapLibre does;
 *  - a source/layer/image registry, enough for PhotoMapOverlay to actually mount and attach listeners.
 *
 * Pointer- and touch-event factories live here too: jsdom ships neither a `Touch` nor a usable
 * `PointerEvent` for multi-touch, so every gesture test needs the same hand-built event shapes.
 *
 * Pair it with core/raf-test-harness.installFakeRaf when the code under test coalesces into a frame.
 */

import { vi } from 'vitest';

const GESTURE_HANDLERS = [
	'dragPan', 'scrollZoom', 'boxZoom', 'dragRotate',
	'keyboard', 'doubleClickZoom', 'touchZoomRotate', 'touchPitch',
] as const;

export type GestureHandlerName = (typeof GESTURE_HANDLERS)[number];

type Listener = (...args: unknown[]) => void;

export interface FakeMapOptions {
	zoom?: number;
	center?: { lng: number; lat: number };
	bearing?: number;
	pitch?: number;
	styleLoaded?: boolean;
	/** Bounding box the overlay reads via getBounds(); defaults to a ~0.2° box around (7, 47). */
	bounds?: { west: number; east: number; south: number; north: number };
}

export interface FakeMap {
	// ── gesture handlers (enable/disable spies) ──
	handlers: Record<GestureHandlerName, { enable: ReturnType<typeof vi.fn>; disable: ReturnType<typeof vi.fn> }>;
	/** `handlers._updatingCamera` seen from inside the most recent jumpTo (the shield flag). */
	shieldedDuringJump: boolean[];

	// ── camera ──
	jumpTo: ReturnType<typeof vi.fn>;
	easeTo: ReturnType<typeof vi.fn>;
	flyTo: ReturnType<typeof vi.fn>;
	setMoving(v: boolean): void;
	setGestureLive(v: boolean): void;

	// ── events ──
	/** Deliver a map-level event (no layer filter). */
	emit(type: string, event?: unknown): void;
	/** Deliver a layer-scoped event, the way MapLibre does for a click on a rendered feature. */
	emitLayer(type: string, layerId: string, event?: unknown): void;
	/** Fire a `once`-registered handler and drop it (mirrors MapLibre consuming the one-shot). */
	fireOnce(type: string): void;
	hasListener(type: string, layerId?: string): boolean;

	// ── the object handed to the code under test ──
	readonly map: maplibregl.Map;

	// ── introspection ──
	sources: Set<string>;
	layers: Set<string>;
	images: Set<string>;
	setStyleLoaded(v: boolean): void;
}

/** A fake map rich enough for the follow camera, the touch handler, and the photo overlay. */
export function createFakeMap(opts: FakeMapOptions = {}): FakeMap {
	let zoom = opts.zoom ?? 12;
	const center = opts.center ?? { lng: 7, lat: 47 };
	let bearing = opts.bearing ?? 0;
	let pitch = opts.pitch ?? 0;
	let styleLoaded = opts.styleLoaded ?? true;
	const b = opts.bounds ?? { west: 6.9, east: 7.1, south: 46.9, north: 47.1 };

	let moving = false;
	let gestureLive = false;

	const handlers = Object.fromEntries(
		GESTURE_HANDLERS.map((h) => [h, { enable: vi.fn(), disable: vi.fn() }]),
	) as FakeMap['handlers'];

	const shieldedDuringJump: boolean[] = [];
	const handlerManager = { isActive: (): boolean => gestureLive, _updatingCamera: false };

	// Event registry. Keyed by "type" for map-level and "type\0layerId" for layer-scoped, so the two
	// overloads of on(...) never collide and emitLayer targets exactly the layer handlers.
	const listeners = new Map<string, Set<Listener>>();
	const onceListeners = new Map<string, Set<Listener>>();
	const key = (type: string, layerId?: string) => (layerId ? `${type}\0${layerId}` : type);

	// Nested the way MapLibre nests them: canvas ⊂ canvasContainer ⊂ container, with popups appended to
	// the CONTAINER — so a test can tell a gesture on the map surface from a click on popup chrome
	// (core/map-user-gesture depends on exactly that boundary).
	const canvas = document.createElement('canvas');
	const container = document.createElement('div');
	const canvasContainer = document.createElement('div');
	canvasContainer.appendChild(canvas);
	container.appendChild(canvasContainer);

	const sources = new Set<string>();
	const layers = new Set<string>();
	const images = new Set<string>();
	// Per-source data + a settable cluster-leaves resolver, so cluster tests can drive the await.
	const sourceObjs = new Map<string, Record<string, unknown>>();

	const jumpTo = vi.fn((cam: Record<string, unknown>) => {
		shieldedDuringJump.push(handlerManager._updatingCamera);
		if (typeof cam.zoom === 'number') zoom = cam.zoom;
		if (typeof cam.bearing === 'number') bearing = cam.bearing;
		if (typeof cam.pitch === 'number') pitch = cam.pitch;
	});
	const easeTo = vi.fn();
	const flyTo = vi.fn();

	function normaliseOn(type: string, a: unknown, b2?: unknown): { key: string; cb: Listener } {
		// on(type, cb) OR on(type, layerId, cb) — the layer overload passes the id as the 2nd arg.
		if (typeof a === 'string') return { key: key(type, a), cb: b2 as Listener };
		return { key: key(type), cb: a as Listener };
	}

	const map = {
		// style / lifecycle
		isStyleLoaded: () => styleLoaded,
		once: (type: string, cb: Listener) => {
			let set = onceListeners.get(type);
			if (!set) {
				set = new Set();
				onceListeners.set(type, set);
			}
			set.add(cb);
		},
		on: (type: string, a: unknown, b2?: unknown) => {
			const { key: k, cb } = normaliseOn(type, a, b2);
			let set = listeners.get(k);
			if (!set) {
				set = new Set();
				listeners.set(k, set);
			}
			set.add(cb);
		},
		off: (type: string, a: unknown, b2?: unknown) => {
			const { key: k, cb } = normaliseOn(type, a, b2);
			listeners.get(k)?.delete(cb);
		},

		// camera getters
		getZoom: () => zoom,
		getCenter: () => ({ ...center }),
		getBearing: () => bearing,
		getPitch: () => pitch,
		getBounds: () => ({
			getWest: () => b.west, getEast: () => b.east,
			getSouth: () => b.south, getNorth: () => b.north,
			contains: () => true,
		}),
		project: ([lng, lat]: [number, number]) => ({ x: (lng - b.west) * 1000, y: (b.north - lat) * 1000 }),
		unproject: ([x, y]: [number, number]) => ({ lng: b.west + x / 1000, lat: b.north - y / 1000 }),

		// camera writes
		jumpTo,
		easeTo,
		flyTo,
		isMoving: () => moving,

		// handler manager (withHandlersShielded / isGestureLive read this)
		handlers: handlerManager,

		// elements
		getCanvas: () => canvas,
		getContainer: () => container,
		getCanvasContainer: () => canvasContainer,

		// sources / layers / images
		getSource: (id: string) => sourceObjs.get(id),
		addSource: (id: string) => {
			sources.add(id);
			sourceObjs.set(id, {
				setData: vi.fn(),
				getClusterLeaves: vi.fn(() => Promise.resolve([] as GeoJSON.Feature[])),
			});
		},
		removeSource: (id: string) => { sources.delete(id); sourceObjs.delete(id); },
		addLayer: (spec: { id: string }) => { layers.add(spec.id); },
		removeLayer: (id: string) => { layers.delete(id); },
		getLayer: (id: string) => (layers.has(id) ? { id } : undefined),
		hasImage: (id: string) => images.has(id),
		addImage: (id: string) => { images.add(id); },
		removeImage: (id: string) => { images.delete(id); },
		setLayoutProperty: vi.fn(),

		// the gesture handlers, spread as top-level props (map.dragPan, map.scrollZoom, …)
		...handlers,
		// cast-ok -- this harness IS the fake-map seam: a structural stub standing in for the whole
		// Map class (ratchet leg (e) pins its casts; do not replicate this pattern outside the harness)
	} as unknown as maplibregl.Map;

	function deliver(k: string, event: unknown): void {
		listeners.get(k)?.forEach((cb) => cb(event ?? {}));
	}

	return {
		handlers,
		shieldedDuringJump,
		jumpTo,
		easeTo,
		flyTo,
		setMoving: (v) => { moving = v; },
		setGestureLive: (v) => { gestureLive = v; },
		emit: (type, event) => deliver(key(type), event),
		emitLayer: (type, layerId, event) => deliver(key(type, layerId), event),
		fireOnce: (type) => {
			const set = onceListeners.get(type);
			if (!set) return;
			const cbs = [...set];
			set.clear();
			cbs.forEach((cb) => cb());
		},
		hasListener: (type, layerId) => (listeners.get(key(type, layerId))?.size ?? 0) > 0,
		map,
		sources,
		layers,
		images,
		setStyleLoaded: (v) => { styleLoaded = v; },
	};
}

// ── Event factories (jsdom has no Touch ctor and no multi-touch PointerEvent) ────────────────────

export interface FakeTouch { id: number; x: number; y: number }

/** A TouchEvent jsdom will dispatch — points are plain objects with the fields the handlers read. */
export function makeTouchEvent(type: string, points: FakeTouch[]): TouchEvent {
	const touches = points.map((p) => ({ identifier: p.id, clientX: p.x, clientY: p.y }));
	const e = new Event(type, { bubbles: true, cancelable: true }) as unknown as {
		changedTouches: unknown; touches: unknown;
	};
	e.changedTouches = touches;
	e.touches = touches;
	return e as unknown as TouchEvent;
}

/** A PointerEvent jsdom will dispatch, carrying the one pointerId + coords the zoom code reads. */
export function makePointerEvent(
	type: string,
	init: { id?: number; x?: number; y?: number; target?: EventTarget } = {},
): PointerEvent {
	const e = new Event(type, { bubbles: true, cancelable: true }) as unknown as {
		pointerId: number; clientX: number; clientY: number;
	};
	e.pointerId = init.id ?? 1;
	e.clientX = init.x ?? 0;
	e.clientY = init.y ?? 0;
	return e as unknown as PointerEvent;
}
