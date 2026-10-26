/**
 * The activity replay's elevation strip: the ride's own profile under the map, with the replay marker
 * riding it — the same animated strip the race and segment replays use (`core/profile-chart`), in the
 * same pull-up dock (`core/replay-chart-drawer`).
 *
 * The simplest of the three adapters, because an activity is ONE track: one terrain line, one dot, and an
 * x-axis that is simply how far through the ride you are. There is no field to filter, nothing to place
 * and nothing to decide about shared ground — so this is a mount and a per-frame write, nothing more.
 *
 * It mounts only when there is a profile worth showing: a ride whose altitudes are missing, or flat to
 * within a metre (a trainer session, a track with no barometer), gets no drawer rather than a flat line
 * along the bottom of a panel that then has to be closed by hand.
 */

import {
	createProfileChart, PROFILE_TRACE_COLOR, type ProfileChart, type ProfileTrace,
} from '../core/profile-chart';
import { axisValueAtIndex, buildTerrainBackdrop } from '../core/profile-terrain';
import { createReplayChartDrawer, type ReplayChartDrawer } from '../core/replay-chart-drawer';
import { getAccountSettings } from '../core/account-settings';
import { gaussianSmooth, gaussianSmoothTime, getSpeedSmoothingSeconds } from '../core/smoothing';
import { trackSeconds } from '../core/gps-data';
import type { GpsPoint } from '../core/core-types';

/** Below this much total climb+descent range there is no shape to look at. */
const MIN_RANGE_METRES = 1;

/** Sample-indexed speed window, used only when a track carries no timestamps to measure seconds against. */
const FALLBACK_SPEED_WINDOW_SAMPLES = 21;

/**
 * The ride's altitude as the strip should DRAW it: the same Gaussian window the details-page elevation
 * chart applies, so the two draw one shape rather than two. Elevation stays on the sample-indexed
 * kernel deliberately — it arrives already conditioned by the server (spike rejection plus a curvature
 * smoother over a fixed ARC LENGTH, see GpsNormalizationService), so a metres-based pass has already
 * happened and this one is only cosmetic.
 *
 * Smoothing is skipped on an incomplete signal, and that is not a nicety: `gaussianSmooth` tolerates a
 * missing sample as `|| 0`, which would turn a hole into a plausible NUMBER — and buildTerrainBackdrop
 * refuses a line with holes precisely because it cannot draw one honestly. Hand it the gap intact.
 */
function drawnElevation(points: readonly GpsPoint[]): number[] {
	const raw = points.map((p) => p.altitude);
	if (!raw.every((a) => Number.isFinite(a))) return raw;
	return gaussianSmooth(raw, getAccountSettings().charts.elevationSmoothingWindow);
}

/**
 * The ride's speed as a trace over the terrain, on the SAME axis the backdrop was built from.
 *
 * `axisAt` is the axis value of every source point, so the trace needs no resampling of its own — it is
 * plotted against the very positions the terrain was resampled from, and the two cannot drift apart.
 *
 * Null when the track carries no usable speed (an import with the channel missing): no line beats a flat
 * blue zero, which would read as a ride spent standing still.
 */
function buildSpeedTrace(points: readonly GpsPoint[], axisAt: Float64Array): ProfileTrace | null {
	const raw = points.map((p) => p.speed);
	if (raw.length < 2 || !raw.some((s) => Number.isFinite(s) && s > 0)) return null;

	// Over a window of SECONDS, at the same setting the details-page chart uses, so the strip and the
	// chart above it describe the same quantity. The hand-rolled hole pre-clean this used to need is
	// gone: gaussianSmoothTime skips a missing sample instead of reading it as `|| 0`.
	const seconds = trackSeconds(points);
	if (seconds) return { x: axisAt, y: gaussianSmoothTime(raw, seconds, getSpeedSmoothingSeconds()), color: PROFILE_TRACE_COLOR };

	// No time axis (a track with no timestamps): fall back to the sample-indexed kernel rather than
	// draw an unsmoothed trace, which over a 120 px strip reads as a band of fuzz rather than a line.
	const clean = raw.map((s) => (Number.isFinite(s) && s > 0 ? s : 0));
	return { x: axisAt, y: gaussianSmooth(clean, FALLBACK_SPEED_WINDOW_SAMPLES), color: PROFILE_TRACE_COLOR };
}

/** Ids owned by _ActivityDetailBody.cshtml. Absent markup = no strip, not a crash. */
const IDS = {
	dock: 'activityReplayDock',
	handle: 'activityReplayChartsHandle',
	content: 'activityReplayCharts',
	canvas: 'activityReplayProfile',
} as const;

export interface ActivityReplayProfileOptions {
	/** The replayed track, in order. Only lat/lng/altitude are read. */
	readonly points: readonly GpsPoint[];
	/** The drawer moved, so the map's box changed. */
	readonly onResize?: () => void;
}

export interface ActivityReplayProfile {
	/**
	 * Move the dot to a FRACTIONAL track index — not the engine's progress percentage.
	 *
	 * The strip's x-axis is distance travelled; the engine's percentage is a sample fraction, which
	 * under time-mode playback is elapsed time. Handing the percentage straight to the chart (as this
	 * did) draws the marker wherever the rider's pace put it — ahead on the climbs, behind on the
	 * descents — so a summit reached early in the ride appears late on the strip. The index is the one
	 * value both spaces can be derived from, so the conversion happens here, against the very
	 * distances the backdrop was built from.
	 */
	tick(fracIndex: number, force?: boolean): void;
	destroy(): void;
}

export function createActivityReplayProfile(
	options: ActivityReplayProfileOptions,
): ActivityReplayProfile | null {
	const host = document.getElementById(IDS.dock);
	const handle = document.getElementById(IDS.handle);
	const content = document.getElementById(IDS.content);
	const canvas = document.getElementById(IDS.canvas) as HTMLCanvasElement | null;
	if (!host || !handle || !content || !canvas) return null;

	const elevation = drawnElevation(options.points);
	const backdrop = buildTerrainBackdrop(
		// ?? NaN: lockstep with points by construction, and a NaN here is refused below exactly as the
		// missing altitude it stands for would be.
		options.points.map((p, i) => [p.lat, p.lng, elevation[i] ?? NaN]));
	if (!backdrop) return null;

	// A ride with no relief has no profile — better no drawer than an empty one.
	let min = Infinity;
	let max = -Infinity;
	for (let i = 0; i < backdrop.elevation.length; i++) {
		const e = backdrop.elevation[i];
		if (e === undefined) continue; // dense ArrayLike — dead branch
		if (e < min) min = e;
		if (e > max) max = e;
	}
	if (!(max - min >= MIN_RANGE_METRES)) return null;

	// One dot. Written in place every frame and handed to the chart ONCE — it reads the array live.
	const values = new Float64Array(1);
	const finished = new Uint8Array(1);

	const chart: ProfileChart = createProfileChart({
		canvas,
		backdrop,
		formatHover: (value, y, speed) => {
			const parts = [`${Math.round(value)}%`];
			if (y != null) parts.push(`${Math.round(y)} m`);
			if (speed != null) parts.push(`${Math.round(speed)} km/h`);
			return parts.join(' · ');
		},
	});
	chart.setDecorations({ bands: [], markers: [] });
	chart.setTrace(buildSpeedTrace(options.points, backdrop.axisAt));
	chart.setField({ values, finished, colors: ['#22b8cf'] });

	const drawer: ReplayChartDrawer = createReplayChartDrawer({
		hostEl: host,
		contentEl: content,
		handleEl: handle,
		storageKey: 'activityReplayChartsDrawer',
		defaultHeightPx: 120,
		minHeightPx: 70,
		onResize() {
			options.onResize?.();
			chart.tick(true);
		},
	});

	// The grip is markup-hidden until here: everything above can decline to mount.
	handle.hidden = false;

	const lastIndex = Math.max(0, backdrop.axisAt.length - 1);

	return {
		tick(fracIndex, force = false) {
			const axisPct = axisValueAtIndex(backdrop.axisAt, fracIndex);
			values[0] = Math.min(100, Math.max(0, axisPct));
			// Finished is an INDEX question, not an axis one: the last few points of a ride can share a
			// position (a stationary finish), which pins axisPct at 100 while the replay is still running.
			finished[0] = fracIndex >= lastIndex ? 1 : 0;
			// One rider, so the playhead IS the dot — it reads as a position on the profile rather than
			// as a second thing to track.
			chart.setPlayhead(values[0]);
			chart.tick(force);
		},
		destroy() {
			handle.hidden = true;
			drawer.destroy();
			chart.destroy();
		},
	};
}
