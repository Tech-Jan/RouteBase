/**
 * edit-preview-render.ts — the preview→view bridge. `channelsToGpsPoints` (pure, I3a) converts a replay
 * slot's dequantized channel arrays into the `GpsPoint[]` the existing chart/map consumers expect, mirroring
 * the Razor config island (Details.cshtml) EXACTLY so a preview reads like view mode: distance m→km, speed
 * m/s→km/h, altitude passthrough, epoch+tzOffset→HH:mm:ss, NaN/absent → 0 (island `?? 0` parity). A fresh
 * array identity per call satisfies the MS-08 cache-invalidation contract. The DOM render pipeline that swaps
 * this into gps-data / charts / map is added in I3b.
 */

import type { GpsPoint } from '../core/core-types';
import type { ReplayOutcome, ReplaySlot, SlotTrack } from './apply-script';
import { setGpsData } from '../core/gps-data';
import { getCurrentColorMode, mapDispatcher } from '../core/map-dispatcher';
import { initSpeedChart, initElevationChart, initHeartRateChart, getSpeedChart, getElevationChart } from './activity-access';
import { clearSplitOverlay } from '../core/chart-overlay';
import { api as TRK } from '../maplibre/maplibre-track';
import { updateMaplibreTrackColors } from '../maplibre/maplibre-terrain';
import { renderSlotOverlay } from './slot-overlay';
import { shiftedUtcDate } from './edit-gap-labels';

/**
 * Convert a slot's channels to `GpsPoint[]`. Emits exactly the fields the config island emits (gpsId omitted);
 * `tzOffsetHours` is folded into the wall-clock time string like the island's `AddHours(...)`.
 */
export function channelsToGpsPoints(slot: SlotTrack, tzOffsetHours: number): GpsPoint[] {
	const { count, channels } = slot;
	const time = channels.time;
	const lat = channels.lat;
	const lng = channels.lng;
	const altitude = channels.altitude;
	const distance = channels.distance;
	const speed = channels.speed;
	const heartRate = channels.heartRate;
	const cadence = channels.cadence;

	const points: GpsPoint[] = new Array(count);
	for (let i = 0; i < count; i++) {
		points[i] = {
			lat: sample(lat, i),
			lng: sample(lng, i),
			altitude: sample(altitude, i),
			speed: sample(speed, i) * 3.6,          // m/s → km/h
			distance: sample(distance, i) / 1000,   // m → km
			time: time ? formatTime(time[i] ?? NaN, tzOffsetHours) : '',
			heartRate: sample(heartRate, i),
			cadence: sample(cadence, i),
		};
	}
	return points;
}

export interface PreviewRenderInput {
	/** The active slot (drawn on the main track layer). */
	slot: ReplaySlot;
	/** Full replay outcome — non-active slots go to the dimmed overlay. */
	outcome: ReplayOutcome;
	activeSlotId: number;
	tzOffsetHours: number;
}

/**
 * The §2.3 render pipeline: swap the active slot's points into the shared gps-data slot, then rebuild the
 * existing chart/map consumers wholesale (never fork them). Fresh array identity per call invalidates the
 * MS-08 coord/color caches. Selection highlights are cleared here (their ordinals reference the prior state).
 * Runs at button-rate only — never per frame.
 */
export function renderPreview(input: PreviewRenderInput): void {
	const points = channelsToGpsPoints(input.slot.track, input.tzOffsetHours);
	setGpsData(points);

	initSpeedChart();
	initElevationChart();
	initHeartRateChart(); // guards its own absent-canvas case (non-HR activities, E20)

	TRK.addTrackToMap(points, true); // skipFitBounds — keep the user's viewport
	const colorMode = getCurrentColorMode();
	if (colorMode && colorMode !== 'none') updateMaplibreTrackColors(colorMode, points);

	renderSlotOverlay(input.outcome.ok ? input.outcome.slots : [], input.activeSlotId);

	mapDispatcher.clearSplitHighlight();
	clearSplitOverlay(getSpeedChart());
	clearSplitOverlay(getElevationChart());
}

/** A present sample, or 0 for an absent channel / NaN gap (island `?? 0`). */
function sample(channel: Float64Array | undefined, i: number): number {
	if (!channel) return 0;
	const v = channel[i] ?? NaN;
	return Number.isNaN(v) ? 0 : v;
}

/** Epoch seconds + tz offset → "HH:mm:ss" (UTC getters read the offset-shifted wall clock); NaN → ''. */
function formatTime(epochSeconds: number, tzOffsetHours: number): string {
	if (Number.isNaN(epochSeconds)) return '';
	const shifted = shiftedUtcDate(epochSeconds, tzOffsetHours);
	const hh = String(shifted.getUTCHours()).padStart(2, '0');
	const mm = String(shifted.getUTCMinutes()).padStart(2, '0');
	const ss = String(shifted.getUTCSeconds()).padStart(2, '0');
	return `${hh}:${mm}:${ss}`;
}
