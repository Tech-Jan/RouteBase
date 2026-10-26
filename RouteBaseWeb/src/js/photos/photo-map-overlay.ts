// photo-map-overlay.ts — reusable photo overlay for any MapLibre map (Activity Details + Activities
// overview). Renders markers through a native clustered GeoJSON source so nearby photos collapse into a
// count badge; past THUMB_ZOOM each unclustered photo's icon becomes its own thumbnail.
//
// EVERY click here opens the full-size viewer, paged: a photo brings its whole ride, a cluster brings
// its leaves. There is no on-map preview step — a click on a picture means "show me that picture", and
// the paging is also what makes a photo buried under an overlapping icon (or inside a cluster)
// reachable without moving a single pin.
//
// Everything draws on ONE symbol layer — pins and thumbnails are both registered images, swapped by a
// data-driven `icon-image` expression. There are deliberately no DOM markers here: a replay camera
// calls jumpTo() every frame, and a maplibregl.Marker reacts to that with two _update passes and a
// forced terrain depth readback (gl.readPixels) per frame. See photo-icon-registry.ts.
//
// The core is map- and data-source-agnostic: the host feeds an already-filtered marker set via
// setMarkers() and drives enable()/disable(); it never fetches or knows about tracks/filters. One overlay
// per map. `maplibregl` is a page global (CDN) on every map page.

import type { MapTouchEvent } from "maplibre-gl";
import { openPhotoFullscreen, type LightboxPhoto } from "./photo-lightbox";
import { createTickThrottle } from "../core/tick-throttle";
import type { PhotoMarker } from "./photo-map-types";
import { photoImageUrl } from "./photo-image-url";
import {
	createPhotoIconRegistry, ensurePinImages, photoIconName,
	IMG_PIN, PIN_COLOR,
	type PhotoIconRegistry,
} from "./photo-icon-registry";

// Zoom at/above which unclustered photos show their own thumbnail; clusters break apart just below it.
// Exported so hosts that jump the camera to a photo land on its thumbnail, not an anonymous pin.
export const THUMB_ZOOM = 14;
const CLUSTER_MAX_ZOOM = THUMB_ZOOM - 1;
const CLUSTER_RADIUS = 50;

// Icon top-up cadence. The camera can move 60×/s during a replay but the set of photos near the
// viewport changes far more slowly, and an unregistered icon degrades to a pin rather than a hole —
// so this can be lazy without ever showing a gap.
const ICON_TOPUP_MS = 200;
// Fraction of the viewport span to look beyond its edges, so icons are ready before they scroll in.
const ICON_TOPUP_PAD = 0.5;

// Finger-sized tap radius, in screen pixels. MapLibre's own layer `click` fires only when a press
// lifts within its mouse-tuned ~3 px `clickTolerance`, so a drift-prone phone tap on a photo icon
// reads as a pan and the photo never opens — the owner's originally-reported bug. A dedicated touch
// tap handler treats any single-finger touch that lifts within this many pixels of where it landed
// as a tap on the feature under it, independent of the mouse tolerance (which stays tight — a 12 px
// mouse drag is still a pan, not a click). Touch only; the mouse click path is untouched.
const TAP_MAX_PX = 12;

const SRC = "photo-overlay";
const L_CLUSTERS = "photo-overlay-clusters";
const L_COUNT = "photo-overlay-count";
const L_POINTS = "photo-overlay-points";

/**
 * The unclustered-photo symbol layer — pins and thumbnails on ONE layer, chosen per feature.
 *
 * Below THUMB_ZOOM: the camera pin. At/above it: the photo's own thumbnail, falling back to the pin
 * while that icon is still loading (or forever, if it failed) — `coalesce` skips an image the style
 * doesn't hold, which is what lets photo-icon-registry register lazily without ever showing a gap.
 * Order matters: the photo icon must come first, or the pin would always win.
 *
 * The pin is named by the constant, not read per feature: there is one pin for every photo.
 *
 * Exported so its contract can be tested against the real definition rather than a copy of it.
 */
export const PHOTO_POINTS_LAYER: Extract<maplibregl.LayerSpecification, { type: "symbol" }> = {
	id: L_POINTS,
	type: "symbol",
	source: SRC,
	filter: ["!", ["has", "point_count"]],
	layout: {
		"icon-image": [
			"step", ["zoom"],
			["image", IMG_PIN],
			THUMB_ZOOM, ["coalesce", ["image", ["get", "icon"]], ["image", IMG_PIN]],
		],
		"icon-size": ["step", ["zoom"], 0.9, THUMB_ZOOM, 1],
		// Load-bearing for performance: without it MapLibre runs symbol collision detection every
		// frame, which is superlinear in icon count (see race-sim/race-markers.ts).
		"icon-allow-overlap": true,
	},
};

export interface PhotoMapOverlayDeps {
	/** The live map, or null if it isn't built yet (the overlay retries on the next setMarkers/refresh). */
	getMap: () => maplibregl.Map | null;
	/** Thumbnail-icon registry. Defaults to the real one; injected in tests to keep them off the network. */
	icons?: PhotoIconRegistry;
}

export class PhotoMapOverlay {
	private markers: PhotoMarker[] = [];
	private enabled = false;
	private listenersAttached = false;
	private awaitingStyle = false; // one pending style.load wait at a time (no stacked listeners)
	private refreshRaf: number | null = null;
	private readonly icons: PhotoIconRegistry;
	private readonly iconThrottle = createTickThrottle(ICON_TOPUP_MS);

	// ── Touch tap detection (finger-sized, independent of MapLibre's ~3 px mouse clickTolerance) ──
	// Screen pixel where the current single-finger touch landed, or null when no tap is in progress
	// (or a second finger is down). Drift from here on touchend decides tap-vs-pan.
	private touchTapStart: { x: number; y: number } | null = null;
	// True once a gesture has ≥2 fingers down — a pinch/rotate belongs to MapLibre, never a tap.
	private touchMultiFinger = false;
	// Set when a touch tap opens the viewer, so MapLibre's own layer click on the SAME tap (which it
	// still fires when the drift was also within its ~3 px tolerance) is swallowed instead of opening
	// a second time. Consumed by the click handlers; reset when the next gesture starts.
	private suppressLayerClick = false;

	constructor(private readonly deps: PhotoMapOverlayDeps) {
		this.icons = deps.icons ?? createPhotoIconRegistry();
	}

	private mapOrNull(): maplibregl.Map | null { return this.deps.getMap(); }

	// ── Public API ───────────────────────────────────────────────────────────────

	/** Replace the marker set (already filtered/scoped by the host). Re-renders if currently enabled. */
	setMarkers(markers: PhotoMarker[]): void {
		this.markers = markers;
		if (this.enabled) this.scheduleRefresh();
	}

	isEnabled(): boolean { return this.enabled; }

	enable(): void {
		if (this.enabled) { this.scheduleRefresh(); return; }
		this.enabled = true;
		this.activate();
	}

	disable(): void {
		if (!this.enabled) return;
		this.enabled = false;
		// An open fullscreen viewer is deliberately left alone: it is a modal the user dismisses.
		const map = this.mapOrNull();
		if (map) { this.detachListeners(map); this.icons.destroy(map); this.removeLayers(map); }
		// Clear any pending style wait: the once("style.load")/once("idle") completers self-guard on this
		// flag (and self-remove when they fire), so dropping it here lets a later re-enable arm a fresh
		// wait instead of returning early forever if the map consumed both one-shots while disabled.
		this.awaitingStyle = false;
		// Drop any in-flight touch-tap state so a re-enable starts clean.
		this.touchTapStart = null;
		this.touchMultiFinger = false;
		this.suppressLayerClick = false;
	}

	/** Re-render the current markers (e.g. the host swapped data while enabled). */
	refresh(): void { if (this.enabled) this.scheduleRefresh(); }

	destroy(): void { this.disable(); }

	// ── Activation / rendering ─────────────────────────────────────────────────────

	private activate(): void {
		const map = this.mapOrNull();
		if (!map) return; // map not built yet — a later setMarkers()/refresh() completes activation
		// Wait on `style.load`, NOT `load`: `load` fires at most once per map, so if the style is mid-reload
		// (terrain apply, basemap switch, bfcache restore) after that one-shot has already fired, a `once("load")`
		// would never resolve and the overlay would stay blank until the user toggled it off/on. `style.load`
		// fires on the initial load AND every setStyle, so activation always completes.
		if (!map.isStyleLoaded()) {
			if (this.awaitingStyle) return; // already queued — don't stack listeners
			this.awaitingStyle = true;
			// isStyleLoaded() also returns false while tiles/DEM are merely IN FLIGHT — after style.load
			// already fired (the documented MapLibre gotcha; see activities-overview-maplibre._styleReady).
			// A style.load-only fallback would strand the overlay blank in that window, so arm an idle
			// self-heal too; whichever fires first completes activation (the flag guards the other).
			const complete = () => {
				if (!this.awaitingStyle) return;
				this.awaitingStyle = false;
				if (this.enabled) this.activate();
			};
			map.once("style.load", complete);
			map.once("idle", complete);
			return;
		}
		this.ensureLayers(map);
		this.attachListeners(map);
		this.render(map);
	}

	private scheduleRefresh(): void {
		if (this.refreshRaf != null) return;
		this.refreshRaf = requestAnimationFrame(() => { this.refreshRaf = null; if (this.enabled) this.activate(); });
	}

	private render(map: maplibregl.Map): void {
		const src = map.getSource(SRC) as maplibregl.GeoJSONSource | undefined;
		if (!src) return;
		src.setData({ type: "FeatureCollection", features: this.buildFeatures() });
		this.topUpIcons(map, true);
	}

	// Register thumbnail icons for photos in (or just outside) the viewport. This is the only
	// camera-driven work the overlay does, and it is deliberately cheap: an O(n) scan of a list that
	// is capped at a couple hundred, with no source query, no DOM and no per-feature allocation. It
	// is safe to leave attached under a 60 Hz replay camera — the throttle collapses a frame storm
	// into one pass every ICON_TOPUP_MS, and a missed pass only delays a thumbnail behind its pin.
	private topUpIcons(map: maplibregl.Map, force = false): void {
		if (!this.enabled || !this.markers.length) return;
		if (!this.iconThrottle.shouldRun(force)) return;

		let west: number, east: number, south: number, north: number;
		try {
			const b = map.getBounds();
			west = b.getWest(); east = b.getEast(); south = b.getSouth(); north = b.getNorth();
		} catch (_e) {
			return; // teardown: the map can be gone between the event and here
		}
		const padX = (east - west) * ICON_TOPUP_PAD, padY = (north - south) * ICON_TOPUP_PAD;

		const wanted: PhotoMarker[] = [];
		for (const p of this.markers) {
			if (p.lng < west - padX || p.lng > east + padX) continue;
			if (p.lat < south - padY || p.lat > north + padY) continue;
			wanted.push(p);
		}
		if (wanted.length) this.icons.ensure(map, wanted);
	}

	private buildFeatures(): GeoJSON.Feature[] {
		const feats: GeoJSON.Feature[] = [];
		for (const p of this.markers) {
			feats.push({
				type: "Feature",
				geometry: { type: "Point", coordinates: [p.lng, p.lat] },
				properties: {
					photoId: p.photoId,
					trackId: p.trackId,
					v: p.v,
					thumb: p.thumbHashBase64 ?? "",
					// Nothing draws with locationSource any more (one pin for every photo), but a cluster
					// leaf comes back as bare feature properties and featToMarker has to rebuild a whole
					// PhotoMarker from them — dropping it here would make every rebuilt marker claim a
					// placement it doesn't have.
					src: p.locationSource ?? "",
					// The photo's own thumbnail once the registry has it; the layer falls back to the
					// shared pin until then (and forever if it fails).
					icon: photoIconName(p.photoId),
				},
			});
		}
		return feats;
	}

	// ── Layers / images ────────────────────────────────────────────────────────────

	private ensureLayers(map: maplibregl.Map): void {
		if (map.getSource(SRC)) return;

		ensurePinImages(map);

		map.addSource(SRC, {
			type: "geojson",
			data: { type: "FeatureCollection", features: this.buildFeatures() },
			cluster: true,
			clusterRadius: CLUSTER_RADIUS,
			clusterMaxZoom: CLUSTER_MAX_ZOOM,
		});

		map.addLayer({
			id: L_CLUSTERS,
			type: "circle",
			source: SRC,
			filter: ["has", "point_count"],
			paint: {
				"circle-color": PIN_COLOR,
				"circle-opacity": 0.9,
				"circle-stroke-width": 2,
				"circle-stroke-color": "#ffffff",
				// Grow the disc with the group size.
				"circle-radius": ["step", ["get", "point_count"], 15, 10, 19, 50, 25],
			},
		});

		// Count label. Font depends on the basemap's glyphs; if unavailable the number simply doesn't draw
		// (the disc still shows and clicking it opens the viewer, which reports the exact count in its
		// position readout) — acceptable degradation.
		map.addLayer({
			id: L_COUNT,
			type: "symbol",
			source: SRC,
			filter: ["has", "point_count"],
			layout: {
				"text-field": ["get", "point_count_abbreviated"],
				"text-font": ["Noto Sans Regular", "Open Sans Regular"],
				"text-size": 12,
				"text-allow-overlap": true,
			},
			paint: { "text-color": "#ffffff" },
		});

		map.addLayer(PHOTO_POINTS_LAYER);

	}

	private removeLayers(map: maplibregl.Map): void {
		for (const id of [L_COUNT, L_CLUSTERS, L_POINTS]) if (map.getLayer(id)) map.removeLayer(id);
		if (map.getSource(SRC)) map.removeSource(SRC);
		// Leave the registered pin images in place — they're cheap and survive a re-enable.
	}

	// ── Listeners (bound refs so off() matches on()) ─────────────────────────────────

	private readonly _onMove = () => { const m = this.mapOrNull(); if (m) this.topUpIcons(m); };
	// A basemap switch wipes layers AND every registered image — the registry has to forget what it
	// thinks the style holds, or no icon would ever be re-added.
	private readonly _onStyleLoad = () => {
		this.icons.reset();
		if (this.enabled) this.activate();
	};
	private readonly _onClusterClick = (e: maplibregl.MapLayerMouseEvent) => this.onClusterClick(e);
	private readonly _onPointClick = (e: maplibregl.MapLayerMouseEvent) => this.onPointClick(e);
	private readonly _cursorOn = () => { const m = this.mapOrNull(); if (m) m.getCanvas().style.cursor = "pointer"; };
	private readonly _cursorOff = () => { const m = this.mapOrNull(); if (m) m.getCanvas().style.cursor = ""; };

	// Touch-tap listeners: a map-level touchstart records where the finger landed (and whether a
	// second one joins), and a layer-scoped touchend on each interactive layer decides tap-vs-pan and
	// opens. touchend fires regardless of MapLibre's mouse clickTolerance, which is what closes the
	// drift-tap gap. See TAP_MAX_PX.
	private readonly _onTouchStart = (e: MapTouchEvent) => {
		// A new gesture: drop any suppression a previous tap left unconsumed (see suppressLayerClick).
		this.suppressLayerClick = false;
		const fingers = e.points?.length ?? 1;
		if (fingers > 1) { this.touchMultiFinger = true; this.touchTapStart = null; return; }
		this.touchMultiFinger = false;
		this.touchTapStart = { x: e.point.x, y: e.point.y };
	};
	private readonly _onPointTouchEnd = (e: maplibregl.MapLayerTouchEvent) => {
		if (!this.isTouchTap(e)) return;
		this.suppressLayerClick = true;
		this.handlePointOpen(e);
	};
	private readonly _onClusterTouchEnd = (e: maplibregl.MapLayerTouchEvent) => {
		if (!this.isTouchTap(e)) return;
		this.suppressLayerClick = true;
		void this.handleClusterOpen(e);
	};

	private attachListeners(map: maplibregl.Map): void {
		if (this.listenersAttached) return;
		map.on("moveend", this._onMove);
		map.on("zoomend", this._onMove);
		map.on("style.load", this._onStyleLoad);
		map.on("click", L_CLUSTERS, this._onClusterClick);
		map.on("click", L_POINTS, this._onPointClick);
		map.on("touchstart", this._onTouchStart);
		map.on("touchend", L_CLUSTERS, this._onClusterTouchEnd);
		map.on("touchend", L_POINTS, this._onPointTouchEnd);
		for (const l of [L_CLUSTERS, L_POINTS]) {
			map.on("mouseenter", l, this._cursorOn);
			map.on("mouseleave", l, this._cursorOff);
		}
		this.listenersAttached = true;
	}

	private detachListeners(map: maplibregl.Map): void {
		if (!this.listenersAttached) return;
		map.off("moveend", this._onMove);
		map.off("zoomend", this._onMove);
		map.off("style.load", this._onStyleLoad);
		map.off("click", L_CLUSTERS, this._onClusterClick);
		map.off("click", L_POINTS, this._onPointClick);
		map.off("touchstart", this._onTouchStart);
		map.off("touchend", L_CLUSTERS, this._onClusterTouchEnd);
		map.off("touchend", L_POINTS, this._onPointTouchEnd);
		for (const l of [L_CLUSTERS, L_POINTS]) {
			map.off("mouseenter", l, this._cursorOn);
			map.off("mouseleave", l, this._cursorOff);
		}
		this.listenersAttached = false;
	}

	/** A single-finger touch that lifted within TAP_MAX_PX of where it landed — a tap, not a pan. */
	private isTouchTap(e: maplibregl.MapLayerTouchEvent): boolean {
		if (this.touchMultiFinger || !this.touchTapStart) return false;
		const p = e.point;
		if (!p) return false;
		return Math.hypot(p.x - this.touchTapStart.x, p.y - this.touchTapStart.y) <= TAP_MAX_PX;
	}

	/** Read-and-clear the flag that swallows MapLibre's own click after a touch tap already opened. */
	private consumeSuppressedClick(): boolean {
		if (!this.suppressLayerClick) return false;
		this.suppressLayerClick = false;
		return true;
	}

	// ── Click handlers ────────────────────────────────────────────────────────────────

	// The click handlers guard against a touch tap that already opened on the same gesture (see
	// suppressLayerClick); the open logic is shared with the touch-tap handlers via handle*Open.
	private onClusterClick(e: maplibregl.MapLayerMouseEvent): void {
		if (this.consumeSuppressedClick()) return;
		void this.handleClusterOpen(e);
	}

	private onPointClick(e: maplibregl.MapLayerMouseEvent): void {
		if (this.consumeSuppressedClick()) return;
		this.handlePointOpen(e);
	}

	private async handleClusterOpen(e: maplibregl.MapLayerMouseEvent | maplibregl.MapLayerTouchEvent): Promise<void> {
		const map = this.mapOrNull();
		const f = e.features?.[0];
		if (!map || !f) return;
		const clusterId = Number((f.properties as Record<string, unknown>).cluster_id);
		const src = map.getSource(SRC) as maplibregl.GeoJSONSource | undefined;
		if (!src) return;
		let leaves: GeoJSON.Feature[];
		try {
			leaves = await src.getClusterLeaves(clusterId, 1000, 0);
		} catch { return; }
		if (!this.enabled) return; // PU-03: overlay disabled while we awaited — don't spawn a viewer
		const photos = leaves
			.filter((l): l is GeoJSON.Feature<GeoJSON.Point> => l.geometry?.type === "Point")
			.map(featToMarker);
		// The cluster's own leaves, not the first one's ride: an Activities-overview cluster can mix
		// rides, and every photo counted on that badge has to stay reachable by paging.
		this.openFullscreen(map, photos, 0);
	}

	private handlePointOpen(e: maplibregl.MapLayerMouseEvent | maplibregl.MapLayerTouchEvent): void {
		const map = this.mapOrNull();
		const f = e.features?.[0];
		if (!map || !f || f.geometry.type !== "Point") return;
		const p = featToMarker(f as GeoJSON.Feature<GeoJSON.Point>);
		this.openViewer(map, p);
	}

	/**
	 * Open the full-size viewer on every photo of the clicked photo's ACTIVITY, starting at that photo.
	 *
	 * Scoping to the track matters on the Activities overview, where the overlay can hold every ride's
	 * photos at once — paging should walk one ride, not all of them. On Details there is only ever one.
	 */
	private openViewer(map: maplibregl.Map, p: PhotoMarker): void {
		const group = this.markers.filter((m) => m.trackId === p.trackId);
		const list = group.length ? group : [p]; // clicked a feature we no longer hold — still show it
		const start = Math.max(0, list.findIndex((m) => m.photoId === p.photoId));
		this.openFullscreen(map, list, start);
	}

	/**
	 * The one place photos leave this overlay: a pin click brings its ride, a cluster click its leaves.
	 *
	 * Paging is what makes an occluded photo reachable — above THUMB_ZOOM photos taken seconds apart
	 * draw as overlapping icons (icon-allow-overlap is on for a reason) and a click can only ever hit
	 * the topmost one; below it a whole cluster hides behind one badge. The arrows reach the rest
	 * without offsetting a single pin.
	 */
	private openFullscreen(map: maplibregl.Map, list: PhotoMarker[], start: number): void {
		openPhotoFullscreen(list.map(toLightboxPhoto), start, {
			// easeTo, not flyTo: paging is a rapid-fire action and a long flight per press would still
			// be in the air on the next one. Centre only — the user's zoom stays theirs. Safe under a
			// replay: the viewer has it paused and holds the camera.
			onShow: (photo) => { if (photo.lngLat) map.easeTo({ center: photo.lngLat, duration: 400 }); },
		});
	}
}

// ── Module helpers (stateless) ─────────────────────────────────────────────────────────

/** Map-marker → viewer item. Markers carry no caption (PhotoMarkerDto is the slim map shape). */
function toLightboxPhoto(p: PhotoMarker): LightboxPhoto {
	return { url: photoImageUrl(p, "preview"), lngLat: [p.lng, p.lat] };
}

const LOCATION_SOURCES = ["exif", "interpolated", "manual", "none"] as const satisfies
	readonly NonNullable<PhotoMarker["locationSource"]>[];

/** Rebuild a PhotoMarker from a source/leaf feature (properties + geometry coords). */
function featToMarker(f: GeoJSON.Feature<GeoJSON.Point>): PhotoMarker {
	const pr = (f.properties ?? {}) as Record<string, unknown>;
	const [lng, lat] = f.geometry.coordinates as [number, number];
	const thumb = typeof pr.thumb === "string" && pr.thumb ? pr.thumb : null;
	// Round-trips our own :240 write, but feature properties are untyped — re-narrow against the
	// wire union (D2 stamp) instead of casting, so a drifted value degrades to null not a lie.
	const srcVal = LOCATION_SOURCES.find((s) => s === pr.src) ?? null;
	return { photoId: Number(pr.photoId), trackId: Number(pr.trackId), lat, lng, locationSource: srcVal, thumbHashBase64: thumb, v: String(pr.v ?? "") };
}
