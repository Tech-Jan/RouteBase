/**
 * slot-overlay.ts — draws the NON-active edit slots as colored lines on the MapLibre map and lets a click on
 * one switch editing to it (A4 §2.1). The active slot stays on the untouched main track layer in red
 * (addTrackToMap), so `red = "the one you're editing"` is invariant; the overlay never touches `track-line`.
 * Colors are birth-stable per slotId (`colorForSlot`) — a slot keeps its hue when others die (dataviz rule:
 * color follows the entity). Called from the render pipeline every op/undo/redo/switch, so split-off slots
 * appear and style-switch re-asserts (M10) ride for free; `initSlotOverlay` wires the persistent click once.
 *
 * A4 replaces the I3 stub's internals behind the SAME `renderSlotOverlay(slots, activeSlotId)` signature.
 */

import { getMaplibreMap } from '../core/map-state';
import { must } from '../core/invariant';
import type { ReplaySlot } from './apply-script';

const SOURCE_ID = 'track-edit-slots';
const LAYER_ID = 'track-edit-slots-line';
const MAIN_TRACK_LAYER = 'track-line'; // active slot paints on top of the overlay

/** Reserved for the active slot (rendered via the main pipeline, not the overlay). Never a palette hue. */
export const ACTIVE_SLOT_COLOR = '#dc3545';

/** 7-hue dataviz-validated categorical order (the skill's reference minus red = active). Fixed order, assigned
 * by slot id so a slot's hue is birth-stable. Mod-7 collisions in long sessions are accepted (chips relieve). */
export const SLOT_COLORS = [
	'#2a78d6', '#1baf7a', '#eda100', '#008300', '#4a3aa7', '#e87ba4', '#eb6834',
] as const;

export function colorForSlot(slotId: number): string {
	const n = SLOT_COLORS.length;
	return must(SLOT_COLORS[((slotId % n) + n) % n], 'modulo index within SLOT_COLORS');
}

export interface SlotOverlayDeps {
	setActiveSlot(slotId: number): void;
	/** True while the split tool is armed or the cut popover is open (D-A4-10 — a slot click then no-ops). */
	isToolArmed(): boolean;
}

let deps: SlotOverlayDeps | null = null;
let clickWired = false;
/** A single armed one-shot 'idle' retry (below) — multiple skipped renders coalesce onto it. */
let pendingIdleRetry = false;
/** Last (slots, activeSlotId) so the deferred retry replays the current state, not stale args. */
let lastArgs: [ReplaySlot[], number] | null = null;

/** Wire the persistent click/hover handlers once (they survive style switches — registered on the map, not the
 * style). Idempotent; safe to call before the layer exists (MapLibre fires them once the layer paints). */
export function initSlotOverlay(d: SlotOverlayDeps): void {
	deps = d;
	const map = getMaplibreMap();
	if (!map || clickWired) return;
	map.on('click', LAYER_ID, onSlotClick);
	map.on('mouseenter', LAYER_ID, onMouseEnter);
	map.on('mouseleave', LAYER_ID, onMouseLeave);
	clickWired = true;
}

/** One `LineString` per non-active live slot; NaN coord pairs skipped, sub-2-coord slots dropped. Pure/exported
 * for tests (V5–V7). */
export function buildSlotFeatures(slots: ReplaySlot[], activeSlotId: number): GeoJSON.Feature[] {
	const features: GeoJSON.Feature[] = [];
	for (const slot of slots) {
		if (slot.slotId === activeSlotId) continue;
		const coordinates = slotCoords(slot);
		if (coordinates.length < 2) continue;
		features.push({
			type: 'Feature',
			properties: { slotId: slot.slotId, color: colorForSlot(slot.slotId) },
			geometry: { type: 'LineString', coordinates },
		});
	}
	return features;
}

export function renderSlotOverlay(slots: ReplaySlot[], activeSlotId: number): void {
	const map = getMaplibreMap();
	if (!map) return;
	lastArgs = [slots, activeSlotId];

	const data: GeoJSON.FeatureCollection = { type: 'FeatureCollection', features: buildSlotFeatures(slots, activeSlotId) };
	const existing = map.getSource(SOURCE_ID) as maplibregl.GeoJSONSource | undefined;
	if (existing) {
		existing.setData(data);
		return;
	}
	// Adding a source/layer needs a live style. isStyleLoaded() LIES — it's false while tiles/DEM are merely
	// in flight (memory maplibre-isstyleloaded-lies; stranded the overview twice, fixed in b12cbd1). Entering
	// a multi-slot session while the Details style still loads would leave non-active lines invisible until the
	// next user action. Instead arm a one-shot 'idle' (fires only once style+sources settle — the safe signal)
	// that replays the latest args. The flag coalesces repeated skips onto one listener.
	if (!map.isStyleLoaded()) {
		if (!pendingIdleRetry) {
			pendingIdleRetry = true;
			map.once('idle', () => {
				pendingIdleRetry = false;
				if (lastArgs) renderSlotOverlay(...lastArgs);
			});
		}
		return;
	}
	map.addSource(SOURCE_ID, { type: 'geojson', data });
	map.addLayer({
		id: LAYER_ID,
		type: 'line',
		source: SOURCE_ID,
		layout: { 'line-cap': 'round', 'line-join': 'round' },
		paint: { 'line-color': ['get', 'color'], 'line-width': 2.5, 'line-opacity': 0.75 },
	}, map.getLayer(MAIN_TRACK_LAYER) ? MAIN_TRACK_LAYER : undefined);
}

export function clearSlotOverlay(): void {
	const map = getMaplibreMap();
	lastArgs = null;          // an armed once('idle') retry then no-ops
	pendingIdleRetry = false;
	if (!map) return;
	if (map.getLayer(LAYER_ID)) map.removeLayer(LAYER_ID);
	if (map.getSource(SOURCE_ID)) map.removeSource(SOURCE_ID);
	if (clickWired) {
		map.off('click', LAYER_ID, onSlotClick);
		map.off('mouseenter', LAYER_ID, onMouseEnter);
		map.off('mouseleave', LAYER_ID, onMouseLeave);
		clickWired = false;
	}
	deps = null;
}

function onSlotClick(e: maplibregl.MapLayerMouseEvent): void {
	if (!deps || deps.isToolArmed()) return; // D-A4-10: never combine a switch with an armed mutation
	const slotId = e.features?.[0]?.properties?.slotId;
	if (typeof slotId === 'number') deps.setActiveSlot(slotId);
}

function onMouseEnter(e: maplibregl.MapLayerMouseEvent): void {
	e.target.getCanvas().style.cursor = 'pointer';
}

function onMouseLeave(e: maplibregl.MapLayerMouseEvent): void {
	e.target.getCanvas().style.cursor = '';
}

function slotCoords(slot: ReplaySlot): [number, number][] {
	const lat = slot.track.channels.lat;
	const lng = slot.track.channels.lng;
	const coords: [number, number][] = [];
	if (lat && lng) {
		for (let i = 0; i < slot.track.count; i++) {
			const la = lat[i] ?? NaN, ln = lng[i] ?? NaN;
			if (!Number.isNaN(la) && !Number.isNaN(ln)) coords.push([ln, la]);
		}
	}
	return coords;
}
