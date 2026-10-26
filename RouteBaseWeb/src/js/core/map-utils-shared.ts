/**
 * map-utils-shared.ts — Map-domain shared utilities
 *
 * Map size controls, fullscreen, replay speed options, replay camera helpers,
 * and track distance normalization — all plain named ESM exports (import them
 * directly).
 */
import { readData, setData } from '../core/data-attrs';
import { mapHandlers, setTransformCenterAltitude } from './map-internals';
import type { GpsPoint } from './core-types';
import { calculateCumulativeDistances, totalDistanceKm } from './geo';
import { must } from './invariant';

// ════════════════════════════════════════════════════════════════
// 11. MapLibre Safe Layer/Source Removal
//
//     Centralises the guard-check-then-remove-then-catch pattern
//     that is repeated across every file that tears down MapLibre
//     layers and sources.  Consumers call these instead of
//     inlining `if (map.getLayer(id)) map.removeLayer(id)`.
// ════════════════════════════════════════════════════════════════

/** Safely remove a single MapLibre layer (no-op if absent). */
export function safeRemoveLayer(map: maplibregl.Map, id: string): void {
	try {
		if (map.getLayer(id)) map.removeLayer(id);
	} catch (_e) { /* teardown: layer may already be removed */ }
}

/** Safely remove a single MapLibre source (no-op if absent). */
export function safeRemoveSource(map: maplibregl.Map, id: string): void {
	try {
		if (map.getSource(id)) map.removeSource(id);
	} catch (_e) { /* teardown: source may already be removed */ }
}

/**
 * Bulk-remove MapLibre layers then sources in one call.
 * Layers are removed first (sources can't be removed while referenced).
 */
export function safeCleanupLayers(
	map: maplibregl.Map,
	layerIds: string[],
	sourceIds: string[],
): void {
	for (let i = 0; i < layerIds.length; i++) {
		safeRemoveLayer(map, must(layerIds[i], 'i < length (loop-bounded)'));
	}
	for (let j = 0; j < sourceIds.length; j++) {
		safeRemoveSource(map, must(sourceIds[j], 'j < length (loop-bounded)'));
	}
}

// ════════════════════════════════════════════════════════════════
// 12. Map Size Controls — reusable grow/shrink/reset/fullscreen
//
//     Eliminates duplicated map size logic across activity-details,
//     activities-overview, and segment-statistics pages.
//
//     Usage:
//       mapSizeControls({
//           mapDivId:      'map',
//           growBtnId:     'mapGrowBtn',
//           shrinkBtnId:   'mapShrinkBtn',
//           resetBtnId:    'mapResetBtn',
//           defaultHeight: 600,
//           invalidate:    function () { ... }  // resize callback
//       });
//
//       mapFullscreen({
//           btnId:    'mapFullscreenBtn',
//           cardId:   'mapCard',
//           invalidate: function () { ... }
//       });
// ════════════════════════════════════════════════════════════════

/**
 * Wire up map grow/shrink/reset buttons for a map container.
 * Returns a teardown that unwires the buttons (call from the owning shell's destroy).
 */
export function mapSizeControls(opts: {
	mapDivId: string;
	growBtnId?: string | undefined;
	shrinkBtnId?: string | undefined;
	resetBtnId?: string | undefined;
	defaultHeight?: number | undefined;
	minHeight?: number | undefined;
	maxHeight?: number | undefined;
	step?: number | undefined;
	invalidate?: (() => void) | undefined;
}): () => void {
	const mapDivId = opts.mapDivId;
	const defaultHeight = opts.defaultHeight || 600;
	const minHeight = opts.minHeight || 300;
	const maxHeight = opts.maxHeight || 2400;
	const step = opts.step || 100;
	const invalidate = opts.invalidate || (() => {});

	function changeHeight(delta: number): void {
		const mapDiv = document.getElementById(mapDivId);
		if (!mapDiv) return;
		const current = parseInt(mapDiv.style.height) || parseInt(getComputedStyle(mapDiv).height) || defaultHeight;
		const next = Math.max(minHeight, Math.min(maxHeight, current + delta));
		mapDiv.style.height = next + "px";
		invalidate();
	}

	const growBtn = opts.growBtnId ? document.getElementById(opts.growBtnId) : null;
	const shrinkBtn = opts.shrinkBtnId ? document.getElementById(opts.shrinkBtnId) : null;
	const resetBtn = opts.resetBtnId ? document.getElementById(opts.resetBtnId) : null;

	const onGrow = (e: Event): void => {
		e.preventDefault();
		changeHeight(step);
	};
	const onShrink = (e: Event): void => {
		e.preventDefault();
		changeHeight(-step);
	};
	const onReset = (e: Event): void => {
		e.preventDefault();
		const mapDiv = document.getElementById(mapDivId);
		if (mapDiv) mapDiv.style.height = defaultHeight + "px";
		invalidate();
	};
	growBtn?.addEventListener("click", onGrow);
	shrinkBtn?.addEventListener("click", onShrink);
	resetBtn?.addEventListener("click", onReset);

	// Teardown, called by MapShell.destroy(): without it a shell re-init (live-share map Retry) stacks
	// one more listener set per cycle — one grow click then jumps the height k× and fires k resizes.
	return (): void => {
		growBtn?.removeEventListener("click", onGrow);
		shrinkBtn?.removeEventListener("click", onShrink);
		resetBtn?.removeEventListener("click", onReset);
	};
}

/**
 * Wire up a fullscreen toggle button for a map card container.
 */
export function mapFullscreen(opts: {
	btnId: string;
	cardId: string;
	invalidate?: () => void;
}): { isFullscreen(): boolean } {
	const btn = document.getElementById(opts.btnId);
	const card = document.getElementById(opts.cardId);
	const invalidate = opts.invalidate || (() => {});
	let _isFullscreen = false;

	if (btn && card) {
		// MS-10: guard against a second wiring of the same button — the stacked click
		// + document keydown listeners would double-toggle (net no-op / broken fullscreen).
		if (readData(btn, 'fsWired')) return { isFullscreen: () => _isFullscreen };
		setData(btn, 'fsWired', "1");
		btn.addEventListener("click", (e) => {
			e.preventDefault();
			_isFullscreen = !_isFullscreen;
			if (_isFullscreen) {
				card.classList.add("map-fullscreen");
				document.body.classList.add("map-fullscreen-active");
				btn.innerHTML = '<i class="bi bi-fullscreen-exit"></i>';
			} else {
				card.classList.remove("map-fullscreen");
				document.body.classList.remove("map-fullscreen-active");
				btn.innerHTML = '<i class="bi bi-fullscreen"></i>';
			}
			setTimeout(invalidate, 100);
		});

		document.addEventListener("keydown", (e: KeyboardEvent) => {
			if (e.key === "Escape" && _isFullscreen) {
				btn.click();
			}
		});
	}

	return {
		isFullscreen: () => _isFullscreen,
	};
}

// ════════════════════════════════════════════════════════════════
// 13. Replay follow-toggle label
//
//     The speed ladder lives in core/speed-ladder.ts and is wired by
//     core/replay-transport.ts for all three replays. The old activity-only
//     builder here (roundReplaySpeed / buildReplaySpeedOptions / its
//     formatDurationShort label helper) is retired — one ladder, one label.
// ════════════════════════════════════════════════════════════════

/**
 * Render the shared state of a replay "follow" header toggle: the "(unfollow)"/"(follow)" label
 * plus the `is-following` class. One source for the label text + class so the three replays can't
 * drift. The button's `title` stays caller-owned — it names the domain target (the marker /
 * effort #N / a rider), which legitimately differs per replay.
 */
export function renderFollowToggleLabel(el: HTMLElement, following: boolean): void {
	el.textContent = following ? "(unfollow)" : "(follow)";
	el.classList.toggle("is-following", following);
}

/**
 * Wire a slower/faster button pair + label to step through a replay speed
 * <select> element. The <select> stays the source of truth (kept in the DOM
 * but hidden) so the existing dynamic option-building and change handlers keep
 * working untouched; the buttons just move selectedIndex and fire `change`.
 *
 * @returns refresh() — call after the options are (re)built to resync the
 *          label text and the disabled state of the end buttons; and destroy()
 *          — remove the button click listeners (part of the caller's teardown,
 *          so a create→destroy→re-create on a live page cannot stack them).
 */
export function setupSpeedStepper(opts: {
	selectEl: HTMLSelectElement | null;
	slowerBtn: HTMLElement | null;
	fasterBtn: HTMLElement | null;
	labelEl: HTMLElement | null;
}): { refresh: () => void; destroy: () => void } {
	const { selectEl, slowerBtn, fasterBtn, labelEl } = opts;

	function refresh(): void {
		if (!selectEl) return;
		const idx = selectEl.selectedIndex;
		const count = selectEl.options.length;
		if (labelEl) {
			labelEl.textContent = idx >= 0 && count > 0 ? selectEl.value + "x" : "--";
		}
		if (slowerBtn) slowerBtn.classList.toggle("is-disabled", idx <= 0);
		if (fasterBtn) fasterBtn.classList.toggle("is-disabled", idx >= count - 1);
	}

	function step(delta: number): void {
		if (!selectEl) return;
		const next = selectEl.selectedIndex + delta;
		if (next < 0 || next >= selectEl.options.length) return;
		selectEl.selectedIndex = next;
		selectEl.dispatchEvent(new Event("change"));
		refresh();
	}

	// Named handlers (not inline arrows) so destroy() can remove them.
	const onSlower = (): void => step(-1);
	const onFaster = (): void => step(1);
	if (slowerBtn) slowerBtn.addEventListener("click", onSlower);
	if (fasterBtn) fasterBtn.addEventListener("click", onFaster);

	refresh();
	return {
		refresh,
		destroy(): void {
			slowerBtn?.removeEventListener("click", onSlower);
			fasterBtn?.removeEventListener("click", onFaster);
		},
	};
}

// ════════════════════════════════════════════════════════════════
// 14. Replay Camera Helpers
//
//     Shared between activity-replay.ts and segment-stats-replay.ts.
//     Avoids duplicating the terrain-elevation fix and the
//     "skip jumpTo when nothing changed" camera-change detection.
// ════════════════════════════════════════════════════════════════

/**
 * Make a camera write invisible to MapLibre's gesture handlers.
 *
 * Every camera setter ends in `jumpTo` → `stop()` → `handlers.stop()` → `reset()` on EVERY handler.
 * That is fatal during a drag's PENDING window — between mousedown and the first move past the 1 px
 * clickTolerance, `DragHandler` has recorded only `_lastPoint` and neither `isMoving()` nor
 * `handlers.isActive()` reports anything, so a follow's gate has no reason to stand back. `reset()`
 * then does `delete this._lastPoint`, `dragMove` bails without it, and the drag is dead until the
 * button is released and pressed again.
 *
 * `HandlerManager.stop()` opens with `if (this._updatingCamera) return` — its own guard for "this
 * stop came from a gesture update, do not undo it". Borrowing the flag makes our write equally
 * invisible, so the forming gesture survives and nothing has to be withheld.
 *
 * Restores rather than clears, so a nested write cannot strip an outer shield. If MapLibre ever
 * renames the field the assignment is simply inert — unshielded, not broken.
 */
export function withHandlersShielded(map: maplibregl.Map, write: () => void): void {
	const handlers = mapHandlers(map);
	const previous = handlers?._updatingCamera;
	if (handlers) handlers._updatingCamera = true;
	try {
		write();
	} finally {
		if (handlers) handlers._updatingCamera = previous ?? false;
	}
}

/**
 * Apply terrain surface elevation to the map camera's center altitude.
 * Throttled to ~5fps (expensive raster tile sampling). Skipped at low tilt
 * (`minPitch`) where the offset is small — but note the offset scales with
 * terrain height, so a live follow map over mountains needs a low `minPitch`
 * (the recorder's gentle 10° tilt still puts a peak tens of px off-centre).
 *
 * @param map          MapLibre map instance (typed as any — avoids hard maplibre-gl dep)
 * @param lng          Current camera center longitude
 * @param lat          Current camera center latitude
 * @param state        Mutable state object with { lastTime: number }
 * @param intervalMs   Minimum ms between elevation queries (default 200)
 * @param minPitch     Skip below/at this pitch (default 10 — replay's playing camera; the shared idle
 *                     writer passes a low value so the recorder's 10° follow still gets corrected)
 */
export function applyTerrainElevation(
	map: maplibregl.Map,
	lng: number,
	lat: number,
	state: { lastTime?: number },
	intervalMs?: number,
	minPitch: number = 10,
): void {
	if (!map) return;
	intervalMs = intervalMs || 200;
	const now = performance.now();
	if (now - (state.lastTime || 0) < intervalMs) return;
	state.lastTime = now;

	// No terrain on this map → nothing to correct, and no reason to read the camera pitch: a follow on a
	// 2D basemap must not touch the camera getters at all (pinned by segment-stats-replay's no-seed test).
	let hasTerrain = false;
	try { hasTerrain = !!(map.getTerrain && map.getTerrain()); } catch (_e) { hasTerrain = false; }
	if (!hasTerrain || typeof map.queryTerrainElevation !== "function") return;

	const currentPitch = map.getPitch ? map.getPitch() : 0;
	if (currentPitch <= minPitch) return;

	try {
		const elev = map.queryTerrainElevation({ lng: lng, lat: lat });
		if (elev !== null && elev !== undefined) {
			// Shielded: setCenterElevation IS `jumpTo({elevation})`, so at ~5×/s on a tilted 3D basemap an
			// unshielded re-clamp lands inside the pending window of whatever drag the user is starting and
			// resets it — the "right-drag won't grab" bug, in the one place a follow's gate cannot cover.
			withHandlersShielded(map, () => {
				if (typeof map.setCenterElevation === "function") {
					map.setCenterElevation(elev);
				} else if (map.transform) {
					setTransformCenterAltitude(map.transform, elev);
				}
			});
		}
	} catch (_e) { /* defensive: terrain tiles may not be ready */ }
}

/**
 * Collapse the compact attribution back to its ⓘ button — exactly what a map drag would have done.
 *
 * MapLibre's AttributionControl opens expanded and minimises itself on ONE event: `map.on('drag', …)`
 * (see its `_updateCompactMinimize`). A follow camera turns the native drag handler off
 * (replay-follow-camera.engageFollowHandlers) and owns the centre itself, so that event never fires — on
 * a map nobody happened to drag beforehand the credit bar stays spread across the corner for the whole
 * replay. This does what MapLibre's own minimiser does, no more: drop `maplibregl-compact-show` and
 * leave the `open` attribute alone, so the ⓘ still opens the credits on click.
 *
 * Method-check rather than optional chaining: a map fake without a container must be a no-op, not a
 * caught throw that logs on every follow in the test suite.
 */
export function collapseMapAttribution(map: maplibregl.Map): void {
	try {
		const container = typeof map.getContainer === 'function' ? map.getContainer() : null;
		if (!container) return;
		const bars = container.querySelectorAll(
			'.maplibregl-ctrl-attrib.maplibregl-compact.maplibregl-compact-show',
		);
		bars.forEach((bar) => bar.classList.remove('maplibregl-compact-show'));
	} catch (e: unknown) {
		console.warn('[map-utils-shared] collapseMapAttribution failed:', e);
	}
}

/** Camera change detector returned by createCameraChangeDetector(). */
export interface CameraChangeDetector {
	hasChanged(lat: number, lng: number, zoom: number, bearing: number, pitch: number): boolean;
	reset(): void;
}

/**
 * Create a camera change detector for replay animation loops.
 * Returns an object that tracks the last camera state and reports
 * whether a new jumpTo call is needed.
 */
export function createCameraChangeDetector(): CameraChangeDetector {
	const THRESHOLD_SQ = 1e-10; // ~1m² in degrees²
	const last: {
		lat: number | null;
		lng: number | null;
		zoom: number | null;
		bearing: number | null;
		pitch: number | null;
	} = {
		lat: null,
		lng: null,
		zoom: null,
		bearing: null,
		pitch: null,
	};

	return {
		/**
		 * Check if the camera has changed enough to warrant a jumpTo call.
		 */
		hasChanged: (
			lat: number,
			lng: number,
			zoom: number,
			bearing: number,
			pitch: number,
		): boolean => {
			// lat and lng are only ever written together, so guarding both is the same first-call test.
			const { lat: lastLat, lng: lastLng } = last;
			if (lastLat === null || lastLng === null) {
				last.lat = lat;
				last.lng = lng;
				last.zoom = zoom;
				last.bearing = bearing;
				last.pitch = pitch;
				return true;
			}
			const dlat = lat - lastLat;
			const dlng = lng - lastLng;
			const movedSq = dlat * dlat + dlng * dlng;
			const changed =
				movedSq > THRESHOLD_SQ ||
				zoom !== last.zoom ||
				bearing !== last.bearing ||
				pitch !== last.pitch;

			if (changed) {
				last.lat = lat;
				last.lng = lng;
				last.zoom = zoom;
				last.bearing = bearing;
				last.pitch = pitch;
			}
			return changed;
		},
		/** Reset tracking state (call after seek or on start). */
		reset: (): void => {
			last.lat = null;
			last.lng = null;
			last.zoom = null;
			last.bearing = null;
			last.pitch = null;
		},
	};
}

// ════════════════════════════════════════════════════════════════
// 15. Track Distance Normalization
//
//     Used by activity-replay.ts. Ensures GPS point distances are in km
//     (not metres) and monotonic. Import the named export directly.
// ════════════════════════════════════════════════════════════════

/**
 * Normalize distances on a GPS track for replay use.
 * - Decides the metre-vs-km unit ONCE for the whole track (MS-05): a per-point
 *   ">100 ⇒ metres" heuristic corrupts >100 km tracks (tail divided, head kept)
 *   and misses sub-100 m metre values. Compare the reported final distance against
 *   the geometric total in km vs. m and convert the whole track accordingly.
 * - Recalculates cumulative distances if ANY point is non-monotonic (full scan,
 *   not just the first 10 — a >100 km corruption shows up past index 10).
 *
 * Mutates the array in-place.
 */
export function normalizeTrackDistances(track: GpsPoint[]): void {
	if (!track || track.length === 0) return;

	// Unit decision for the whole track.
	const geoKm = totalDistanceKm(track);
	const last = must(track.at(-1), 'non-empty (guarded above)').distance || 0;
	if (last > 0 && Math.abs(last / 1000 - geoKm) < Math.abs(last - geoKm)) {
		for (let i = 0; i < track.length; i++) {
			const p = must(track[i], 'i < length (loop-bounded)');
			if (typeof p.distance === "number") {
				p.distance = p.distance / 1000;
			}
		}
	}

	// Recalculate if non-monotonic anywhere.
	for (let j = 0; j < track.length - 1; j++) {
		const a = must(track[j], 'j < length-1 (loop-bounded)');
		const b = must(track[j + 1], 'j+1 <= length-1 (loop-bounded)');
		if ((b.distance || 0) <= (a.distance || 0)) {
			calculateCumulativeDistances(track);
			break;
		}
	}
}
