/**
 * The segment replay's elevation strip: the same animated profile the race replay uses
 * (`core/profile-chart.ts`), in the same pull-up drawer (`core/replay-chart-drawer.ts`), fed from
 * segment data instead of race data.
 *
 * ## Two segment kinds, two honest pictures
 *
 * **polyline** — the segment has its own reference GPS, so every effort is on ONE piece of ground and
 * "50%" means the same place for all of them. That earns the shared terrain backdrop, with one dot per
 * effort riding it; x is completion along the segment (0–100).
 *
 * **shapes** — a shape segment has **zero** `Segment_GPS` rows by construction
 * (`SegmentCrud.CreateShapeSegmentAsync`), so `segmentProgress` is null on every point and both the
 * charts and the replay fall back to each effort's OWN distance fraction. "50%" is then 50% of a
 * different ride per effort, and a shared backdrop would be a claim about position that is not true.
 * So each effort gets its own elevation line, plotted against elapsed time, with its own dot on it.
 *
 * The mode is chosen from the data, never from a flag: a polyline segment whose altitudes never made it
 * to the client falls back to per-effort lines exactly like a shapes segment would.
 *
 * In progress mode the terrain also carries the efforts' AGGREGATE speed as a trace (the median inside a
 * p10–p90 spread) — see `speedTrace` below for why that is a progress-mode-only picture.
 */

import { formatTime } from '../core/format';
import { buildTerrainBackdrop } from '../core/profile-terrain';
import {
	createProfileChart, PROFILE_TRACE_COLOR,
	type ProfileBackdrop, type ProfileChart, type ProfileSeries, type ProfileSeriesBackdrop,
	type ProfileTrace,
} from '../core/profile-chart';
import { createReplayChartDrawer, type ReplayChartDrawer } from '../core/replay-chart-drawer';
import { computeEffortSpeedProfile } from './segment-effort-speed';
import type { EffortDataPoint } from './segment-stats-effort-types';
import type { ReplayPointSample } from './segment-stats-replay-types';
import { must } from '../core/invariant';

/** Cap on the points drawn per effort line — a 3-hour ride has thousands and the strip is 800 px wide. */
const MAX_SERIES_POINTS = 600;

/** The page config the mode decision reads. */
export interface SegmentProfileSource {
	readonly kind?: string | null | undefined;
	readonly hasGpsData?: boolean | undefined;
	/** `[lat, lng]` per reference point, in order — plus `[2]` = altitude when the server had it. */
	readonly segmentCoords?: readonly number[][] | undefined;
}

/** The replay's efforts, as this strip needs them. */
export interface SegmentProfileEffort {
	readonly color: string;
	readonly points: readonly EffortDataPoint[];
	readonly elapsedTime: number;
	/**
	 * Completion percentage (0–100) per point, lockstep with `points` — ReplayEffort.progress. Optional
	 * because it only exists where the segment HAS a shared position axis, which is the same condition
	 * the speed aggregate needs; without it the strip simply draws no trace.
	 */
	readonly progress?: readonly number[] | undefined;
}

/** What the axis MEANS, which is the whole difference between the two segment kinds. */
export type SegmentProfileMode = 'progress' | 'time';

export interface SegmentProfilePlan {
	readonly backdrop: ProfileBackdrop;
	readonly mode: SegmentProfileMode;
}

/** One elevation line per effort, against elapsed time. Efforts with no usable altitude are dropped. */
export function buildEffortSeries(efforts: readonly SegmentProfileEffort[]): ProfileSeriesBackdrop | null {
	const series: ProfileSeries[] = [];
	let axisMax = 0;

	for (const effort of efforts) {
		const usable = effort.points.filter((p) => p.altitude != null && Number.isFinite(p.altitude));
		if (usable.length < 2) continue;

		// Stride decimation, always keeping the last point so the line ends where the effort does.
		const stride = Math.max(1, Math.ceil(usable.length / MAX_SERIES_POINTS));
		const x: number[] = [];
		const y: number[] = [];
		for (let i = 0; i < usable.length; i += stride) {
			const u = must(usable[i], 'i < length (loop-bounded)');
			x.push(u.time);
			y.push(u.altitude as number);
		}
		const last = must(usable.at(-1), 'length >= 2 (guarded above)');
		if (x[x.length - 1] !== last.time) {
			x.push(last.time);
			y.push(last.altitude as number);
		}

		series.push({ x, y, color: effort.color });
		axisMax = Math.max(axisMax, effort.elapsedTime, last.time);
	}

	if (series.length === 0 || axisMax <= 0) return null;
	return { kind: 'series', series, axisMax };
}

/**
 * The efforts' aggregate speed, as a trace over the shared terrain: the median inside its p10–p90
 * spread, on the strip's own 0–100 completion axis.
 *
 * **Progress mode only, and that is a data claim rather than a layout preference.** In time mode there is
 * no shared ground — each effort rides its own line and "50%" is a different place for each — so a
 * quantile across efforts at one x would aggregate positions that have nothing to do with each other.
 * The second reason is the picture: a series backdrop is already N stroked lines, and a second set of
 * lines on a second scale over them is the dual-axis chart this strip exists to avoid.
 */
function speedTrace(efforts: readonly SegmentProfileEffort[]): ProfileTrace | null {
	const sources = efforts
		.filter((e): e is SegmentProfileEffort & { progress: readonly number[] } => !!e.progress)
		.map((e) => ({ points: e.points, progress: e.progress }));
	if (sources.length === 0) return null;

	const bins = computeEffortSpeedProfile(sources).bins;
	if (bins.length < 2) return null;

	const x = new Float64Array(bins.length);
	const y = new Float64Array(bins.length);
	const low = new Float64Array(bins.length);
	const high = new Float64Array(bins.length);
	for (let i = 0; i < bins.length; i++) {
		const b = must(bins[i], 'i < bins.length (loop-bounded)');
		x[i] = b.progressPercent;
		y[i] = b.median;
		low[i] = b.p10;
		high[i] = b.p90;
	}
	return { x, y, band: { low, high }, color: PROFILE_TRACE_COLOR };
}

/**
 * Which picture this segment gets. Terrain when the segment really is one shared piece of ground AND
 * the client has its heights; per-effort lines otherwise. Null when there is nothing to draw at all.
 */
export function planSegmentProfile(
	source: SegmentProfileSource, efforts: readonly SegmentProfileEffort[],
): SegmentProfilePlan | null {
	if (source.kind === 'polyline' && source.hasGpsData) {
		const terrain = buildTerrainBackdrop(source.segmentCoords ?? []);
		if (terrain) return { backdrop: terrain, mode: 'progress' };
	}
	const series = buildEffortSeries(efforts);
	return series ? { backdrop: series, mode: 'time' } : null;
}

export interface SegmentReplayProfileOptions {
	readonly source: SegmentProfileSource;
	readonly efforts: readonly SegmentProfileEffort[];
	/**
	 * One effort, sampled at the replay's CURRENT moment — `replayApi.getEffortDataAtCurrentTime`.
	 * Not a function of time on purpose: the engine advances each effort's cursor as it runs, so "at
	 * this instant" is the only question it can answer correctly.
	 */
	readonly sample: (effortIdx: number) => ReplayPointSample | null;
	/**
	 * The followed effort, or -1 for nobody — read live each tick, so the playhead rides whoever the
	 * viewer picked. Only progress mode can use it; in time mode the clock IS the axis.
	 */
	readonly follow?: () => number;
	/** The drawer moved, so the map's box changed. */
	readonly onResize?: () => void;
}

export interface SegmentReplayProfile {
	/** Move the dots to this moment of the replay clock. */
	tick(currentTime: number, force?: boolean): void;
	destroy(): void;
}

/** Ids owned by _SegmentReplayOverlay.cshtml. Absent markup = no strip, not a crash. */
const IDS = {
	/** The bottom dock: handle + transport bar + strip, as one bottom-anchored stack. */
	dock: 'segReplayDock',
	handle: 'segReplayChartsHandle',
	content: 'segReplayCharts',
	canvas: 'segReplayProfile',
} as const;

export function createSegmentReplayProfile(options: SegmentReplayProfileOptions): SegmentReplayProfile | null {
	const host = document.getElementById(IDS.dock);
	const handle = document.getElementById(IDS.handle);
	const content = document.getElementById(IDS.content);
	const canvas = document.getElementById(IDS.canvas) as HTMLCanvasElement | null;
	if (!host || !handle || !content || !canvas) return null;

	const plan = planSegmentProfile(options.source, options.efforts);
	if (!plan) return null;

	const { mode } = plan;
	const n = options.efforts.length;
	// Written in place every frame and handed to the chart ONCE — the chart reads them live.
	const values = new Float64Array(n);
	const heights = new Float64Array(n);
	const finished = new Uint8Array(n);

	const chart: ProfileChart = createProfileChart({
		canvas,
		backdrop: plan.backdrop,
		formatHover: mode === 'progress'
			? (value, y, speed) => {
				const parts = [`${Math.round(value)}%`];
				if (y != null) parts.push(`${Math.round(y)} m`);
				if (speed != null) parts.push(`${Math.round(speed)} km/h`);
				return parts.join(' · ');
			}
			: (value) => formatTime(value),
	});
	chart.setDecorations({ bands: [], markers: [] });
	chart.setTrace(mode === 'progress' ? speedTrace(options.efforts) : null);
	chart.setField({
		values,
		// Under a terrain backdrop the dots ride the shared ground; under series each carries its own.
		heights: mode === 'time' ? heights : null,
		finished,
		colors: options.efforts.map((e) => e.color),
	});

	const drawer: ReplayChartDrawer = createReplayChartDrawer({
		hostEl: host,
		contentEl: content,
		handleEl: handle,
		storageKey: 'segReplayChartsDrawer',
		defaultHeightPx: 120,
		minHeightPx: 70,
		onResize() {
			options.onResize?.();
			chart.tick(true);
		},
	});

	// The grip is markup-hidden until here: a segment with no usable elevation gets no strip, and a grip
	// that pulls up nothing is worse than no grip.
	handle.hidden = false;

	return {
		tick(currentTime, force = false) {
			let lead = 0;
			for (let i = 0; i < n; i++) {
				const s = options.sample(i);
				if (!s) continue;
				const v = mode === 'progress' ? s.progress : s.time;
				values[i] = v;
				heights[i] = s.altitude;
				finished[i] = mode === 'progress'
					? (s.progress >= 100 ? 1 : 0)
					: (s.time >= must(options.efforts[i], 'i < n (loop-bounded)').elapsedTime ? 1 : 0);
				if (v > lead) lead = v;
			}
			if (mode === 'progress') {
				// The playhead rides whoever is WATCHED — the leader, or the followed effort — and the leader
				// then stays on as a second golden rule, so the gap between them is the gap on the segment.
				const followIdx = options.follow?.() ?? -1;
				const followed = followIdx >= 0 && followIdx < n ? values[followIdx] : null;
				chart.setPlayhead(followed ?? lead);
				chart.setLeadLine(followed == null ? null : lead);
			} else {
				// Time mode: the clock is the axis, so every effort is at the same x and there is only one
				// place a rule could go.
				chart.setPlayhead(currentTime);
			}
			chart.tick(force);
		},
		destroy() {
			handle.hidden = true;
			drawer.destroy();
			chart.destroy();
			// The height property lives on the dock, which the overlay hides with itself — nothing to unset.
		},
	};
}
