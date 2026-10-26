/**
 * The course, drawn BEHIND the race speed chart's rider lines: the terrain, tinted per section, with a
 * rule at every checkpoint.
 *
 * ## Why the speed chart carries the course at all
 *
 * It used to be two pictures — a hand-drawn strip in the map dock with the terrain and the checkpoints,
 * and this chart with the riders — and reading "he lost it on the Hinterzarten climb" meant lining up an
 * x-position across them by eye. The strip is the fullscreen replay surface now; the course context lives
 * here, which is also what makes checkpoint placement possible on this chart (race-speed-checkpoints.ts).
 *
 * ## Why a plugin and not a dataset
 *
 * A dataset WAS available: Chart.js is 4.5.1, where `segment.backgroundColor` is scriptable, so this could
 * have been one filled line on a hidden second y-axis — the `yBg` pattern `core/chart-factory` uses for the
 * activity elevation backdrop. It is not, for one reason: a segment is coloured between two CONSECUTIVE
 * POINTS, so a section boundary would snap to the nearest grid node — up to `stepMeters` off — and visibly
 * disagree with the checkpoint rule drawn at the exact chainage, on the very surface where that boundary is
 * placed and dragged. Clipping per band puts the colour change where the checkpoint actually is.
 *
 * That is also what `core/profile-chart.paintTerrain` does for the strip, so the two surfaces cut a section
 * at the same place; and painting in `beforeDatasetsDraw` is what
 * `segment-stats-charts-plugins.makeElevBackgroundPlugin` does for the segment speed chart. The geometry —
 * full plot height, 8 % grace, faint fill under a top edge — is deliberately those two charts', not a
 * fourth opinion; see TERRAIN_GRACE.
 *
 * ## The axis is percent, and that is not a second definition of position
 *
 * x is `chainage / totalMeters * 100`, which is exactly how `race-rider-speed` places a rider. One
 * conversion pair (`percentOf` / `metresOf`) is exported and every consumer uses it, so the terrain, the
 * checkpoints, the riders and the playhead cannot end up on four slightly different axes.
 */

import { lttbDecimate } from '../core/chart-decimation';
import { scaleInternals } from '../core/chart-internals';
import { strokeCrosshairLine } from '../core/chart-overlay';
import { cssVar } from '../core/css-vars';
import type { ChartJsInstance } from '../core/core-types';
import type { RaceCheckpointDto, RaceCourseDto } from './race-api';
import { legSpans, OUT_OF_RACE_COLOR, outOfRaceSpans, type RaceLegSpan } from './race-profile';

/**
 * Headroom above and below the terrain's own extent, as a fraction of it — the elevation then uses the
 * WHOLE plot height, minus this at each end.
 *
 * 8 %, and the number is not a taste: it is what `chart-factory`'s hidden `yBg` axis passes as
 * `grace: "8%"` and what `segment-stats-charts-plugins`'s elevation backdrop computes by hand
 * ("matching the activity chart's yBg axis"). Three surfaces draw a course's elevation behind a speed
 * line; a fourth reading of how tall it should be would just make this one look wrong next to them —
 * which it did, at a bottom-42 % band that read as visibly squashed.
 */
const TERRAIN_GRACE = 0.08;
/**
 * A tinted leg is a wash, not a block: the rider lines have to stay readable straight through it.
 *
 * Near the 0.22 the other two use (`ELEV_BG_FILL`), and low BECAUSE the terrain is full height now — the
 * old 0.42 was affordable only against a band confined to the bottom of the plot. A little above theirs
 * because this fill also has to carry the leg's identity, which their flat grey does not.
 */
const TERRAIN_ALPHA = 0.26;
/** The silhouette's top edge. What makes a light fill read as a profile — the same trick as ELEV_BG_STROKE. */
const TERRAIN_EDGE_ALPHA = 0.55;
/** The grab handle at the foot of a checkpoint rule — half its width, and its height, in CSS px. */
const HANDLE_HALF_W = 5;
const HANDLE_H = 8;

export interface RaceSpeedBackdropDeps {
	/** The course grid — terrain samples, their spacing, and the length x is a percentage OF. */
	readonly course: RaceCourseDto;
	/** Read live on every paint: a drag moves a checkpoint locally, and the rule must follow the finger. */
	readonly checkpoints: () => readonly RaceCheckpointDto[];
	/** The checkpoint the Sections editor has focused, drawn brighter. Null = none. */
	readonly selected: () => number | null;
}

/**
 * Metres along the course → percent of it. The chart's x-axis, in one place.
 *
 * NOT `core/profile-axis` — that pair is pixel↔axis for a hand-drawn strip (it takes plotLeft/plotWidth),
 * and on a Chart.js canvas the pixel half belongs to the x-scale's own `getPixelForValue`. What is left
 * is the UNIT conversion, which the strip never needed because its axis is already metres.
 */
export function percentOf(metres: number, totalMeters: number): number {
	if (!(totalMeters > 0)) return 0;
	return Math.min(100, Math.max(0, (metres / totalMeters) * 100));
}

/** Percent of the course → metres along it. The inverse of `percentOf`, for a pointer x. */
export function metresOf(percent: number, totalMeters: number): number {
	if (!(totalMeters > 0)) return 0;
	return Math.min(totalMeters, Math.max(0, (percent / 100) * totalMeters));
}

/**
 * The bands the terrain is painted in: the out-of-race stretches first, then one per section.
 *
 * Both lists come from `race-profile`'s pure functions rather than being re-derived, so the strip and this
 * chart cannot disagree about which stretch is which colour. Out-of-race spans are NOT decoration — a
 * stretch with no band is not filled at all, so without them, moving the Start inward would erase that end
 * of the terrain instead of greying it.
 */
export function backdropBands(
	checkpoints: readonly RaceCheckpointDto[], totalMeters: number,
): RaceLegSpan[] {
	if (checkpoints.length === 0) {
		return [{ fromMetres: 0, toMetres: totalMeters, color: null, label: null }];
	}
	return [...outOfRaceSpans(checkpoints, totalMeters), ...legSpans(checkpoints)];
}

export interface RaceSpeedBackdrop {
	/** The Chart.js plugin object — pass it in the chart's `plugins` array. */
	readonly plugin: object;
	/** Pixel x of a checkpoint on the LIVE chart, or null when it is scrolled out of the plot. */
	pixelXOf(chart: ChartJsInstance | null, chainageMeters: number): number | null;
}

export function createRaceSpeedBackdrop(deps: RaceSpeedBackdropDeps): RaceSpeedBackdrop {
	const { course } = deps;
	const total = course.totalMeters;

	function pixelXOf(chart: ChartJsInstance | null, chainageMeters: number): number | null {
		const area = chart?.chartArea;
		const xScale = chart?.scales?.x ? scaleInternals(chart.scales.x) : undefined;
		if (!area || typeof xScale?.getPixelForValue !== 'function') return null;
		const x = xScale.getPixelForValue(percentOf(chainageMeters, total));
		if (!Number.isFinite(x) || x < area.left || x > area.right) return null;
		return x;
	}

	/**
	 * The terrain at full resolution, in {metres, elevation} — built once, because the course does not
	 * change under this chart. A non-finite sample is dropped rather than interpolated: a hole in the
	 * terrain draws a cliff that is not there (the rule `core/profile-terrain` states for the strips).
	 */
	const samples: { x: number; y: number }[] = [];
	let lo = Infinity;
	let hi = -Infinity;
	for (let i = 0; i < course.elevation.length; i++) {
		const v = course.elevation[i];
		if (v == null || !Number.isFinite(v)) continue;
		samples.push({ x: i * course.stepMeters, y: v });
		if (v < lo) lo = v;
		if (v > hi) hi = v;
	}
	// A dead-flat course has no shape to show; a floor makes it a thin band rather than a divide-by-zero.
	const span = hi - lo > 1 ? hi - lo : 1;

	/**
	 * The decimated terrain, cached per (plot width × visible window).
	 *
	 * A 70 km course at a 10 m step is 7 000 nodes against a ~1 200 px plot — six line ops per pixel, and
	 * paid again for EVERY band on EVERY repaint. LTTB (`core/chart-decimation`) is the codebase's answer
	 * to this exact question and the one the activity elevation chart already uses; a plain stride was
	 * measured to cut summits that fall between two strides, which is why `profile-terrain.ts` says the
	 * strip disagreed with the details page until it stopped doing that. Same decimator, same picture.
	 *
	 * Keyed on the WINDOW as well as the width, because decimating the whole course to plot-width points
	 * and then zooming 10× would show a tenth of the detail the data has. `chart-series-modes.ts:23` and
	 * `series-binning.ts:21` both state the rule this follows: the zoom re-decimation slices the series by
	 * index and "slices the elevation backdrop by the same window".
	 */
	let decimated: { x: number; y: number }[] = [];
	let cachedKey = '';

	/** First index with x >= value, by binary search. `samples` is sorted by construction. */
	function lowerBound(value: number): number {
		let loIdx = 0;
		let hiIdx = samples.length - 1;
		while (loIdx < hiIdx) {
			const mid = (loIdx + hiIdx) >>> 1;
			if ((samples[mid]?.x ?? 0) < value) loIdx = mid + 1;
			else hiIdx = mid;
		}
		return loIdx;
	}

	function terrainFor(columns: number, fromMetres: number, toMetres: number): { x: number; y: number }[] {
		const key = `${columns}|${Math.round(fromMetres)}|${Math.round(toMetres)}`;
		if (key === cachedKey) return decimated;
		// One sample either side of the window, so the path enters and leaves the plot rather than
		// starting at the first visible node and leaving a wedge of blank at each edge.
		const from = Math.max(0, lowerBound(fromMetres) - 1);
		const to = Math.min(samples.length - 1, lowerBound(toMetres) + 1);
		const slice = samples.slice(from, to + 1);
		decimated = slice.length > columns ? lttbDecimate(slice, columns) : slice;
		cachedKey = key;
		return decimated;
	}

	/**
	 * The terrain silhouette as a path over the plot, plus the y of its top edge for the stroke.
	 *
	 * Full plot height, graced at both ends — see TERRAIN_GRACE. Null when there is nothing to draw.
	 */
	function terrainPath(
		g: CanvasRenderingContext2D,
		area: { left: number; right: number; top: number; bottom: number },
		xAt: (metres: number) => number,
		visible: { from: number; to: number },
	): { x: number; y: number }[] | null {
		if (samples.length < 2) return null;

		// The same shape as segment-stats' elevation backdrop: grace the extent, then map it across the
		// WHOLE plot height rather than a band at the bottom.
		const grace = span * TERRAIN_GRACE;
		const base = lo - grace;
		const range = (hi + grace) - base || 1;
		const bottom = area.bottom;
		const plotH = area.bottom - area.top;
		const yAt = (v: number): number => bottom - ((v - base) / range) * plotH;

		// One node per pixel column; LTTB keeps the first and last, so the path spans the whole window.
		const columns = Math.max(3, Math.round(area.right - area.left));
		const points = terrainFor(columns, visible.from, visible.to)
			.map((p) => ({ x: xAt(p.x), y: yAt(p.y) }));
		if (points.length < 2) return null;

		g.beginPath();
		g.moveTo(points[0]?.x ?? 0, bottom);
		for (const p of points) g.lineTo(p.x, p.y);
		g.lineTo(points[points.length - 1]?.x ?? 0, bottom);
		g.closePath();
		return points;
	}

	const plugin = {
		id: 'raceSpeedBackdrop',
		beforeDatasetsDraw: (chart: ChartJsInstance) => {
			const area = chart.chartArea;
			const xScale = chart.scales?.x ? scaleInternals(chart.scales.x) : undefined;
			const pixelFor = xScale?.getPixelForValue;
			if (!area || typeof pixelFor !== 'function') return;
			const g = chart.ctx;
			if (!g) return;

			const xAt = (metres: number): number => pixelFor.call(xScale, percentOf(metres, total));
			// The visible stretch, in metres — what the terrain is re-decimated for, so zooming in buys
			// detail instead of magnifying the same points. (Named `visible`, not `window`: shadowing the
			// global is what the no-restricted-syntax gate exists to stop.)
			const visible = {
				from: metresOf(xScale?.min ?? 0, total),
				to: metresOf(xScale?.max ?? 100, total),
			};
			const checkpoints = deps.checkpoints();
			// The same custom properties (and fallbacks) core/profile-chart resolves — one course, two
			// surfaces, and a leg must not be a different grey on each.
			const untinted = cssVar('--profile-untinted', 'rgba(148, 163, 184, 0.30)');

			// ── terrain, one clipped fill per band ──────────────────────────────────────────────
			for (const band of backdropBands(checkpoints, total)) {
				g.save();
				g.beginPath();
				const x0 = xAt(band.fromMetres);
				const x1 = xAt(band.toMetres);
				// Clip to the band AND to the plot, so a zoomed-out band cannot paint over the axes.
				g.rect(
					Math.max(area.left, Math.min(x0, x1)),
					area.top,
					Math.max(0, Math.min(area.right, Math.max(x0, x1)) - Math.max(area.left, Math.min(x0, x1))),
					area.bottom - area.top,
				);
				g.clip();
				const points = terrainPath(g, area, xAt, visible);
				if (points) {
					const color = band.color ?? untinted;
					g.fillStyle = color;
					g.globalAlpha = band.color ? TERRAIN_ALPHA : 1;
					g.fill();
					// The top edge. A full-height fill has to be faint to keep the rider lines readable, and
					// a faint fill only reads as a PROFILE once its ridge line is drawn — the same pairing
					// segment-stats uses (ELEV_BG_FILL under ELEV_BG_STROKE).
					g.beginPath();
					g.moveTo(points[0]?.x ?? 0, points[0]?.y ?? 0);
					for (const p of points) g.lineTo(p.x, p.y);
					g.globalAlpha = band.color ? TERRAIN_EDGE_ALPHA : 1;
					g.lineWidth = 1;
					g.strokeStyle = color;
					g.stroke();
				}
				g.restore();
			}

			// ── checkpoint rules ────────────────────────────────────────────────────────────────
			const selected = deps.selected();
			const ruleColor = cssVar('--profile-marker', 'rgba(226, 232, 240, 0.5)');
			for (const checkpoint of checkpoints) {
				const x = pixelXOf(chart, checkpoint.chainageMeters);
				if (x == null) continue;
				const isSelected = checkpoint.checkpointId === selected;
				const color = isSelected ? cssVar('--profile-marker-strong', '#ffffff') : ruleColor;
				// THE shared vertical stroke (chart-overlay), which the Statistics guide and the segment
				// progress line also draw through — it owns its own save/restore and resets the dash, so a
				// pattern cannot leak into the handle below it.
				strokeCrosshairLine(g, x, area.top, area.bottom, {
					color,
					width: isSelected ? 2 : 1,
					...(isSelected ? {} : { dash: [5, 4] }),
				});
				// The grab handle: the drag affordance, and the reason a rule is hittable on a touch screen.
				g.save();
				g.fillStyle = color;
				g.fillRect(x - HANDLE_HALF_W, area.bottom - HANDLE_H, HANDLE_HALF_W * 2, HANDLE_H);
				g.restore();
			}
		},
	};

	return { plugin, pixelXOf };
}

/**
 * The checkpoint nearest `pixelX`, within `tolerancePx` — the hit-test both the drag layer and the
 * pan-yield predicate ask. Exported (rather than living inside the pointer layer) because those two
 * answers MUST be the same one: a press that yields the pan but then misses the drag is a dead gesture.
 */
export function checkpointAtPixel(
	chart: ChartJsInstance | null,
	checkpoints: readonly RaceCheckpointDto[],
	pixelX: number,
	totalMeters: number,
	tolerancePx: number,
): RaceCheckpointDto | null {
	const area = chart?.chartArea;
	const xScale = chart?.scales?.x ? scaleInternals(chart.scales.x) : undefined;
	if (!area || typeof xScale?.getPixelForValue !== 'function') return null;

	let best: RaceCheckpointDto | null = null;
	let bestDistance = tolerancePx;
	for (const checkpoint of checkpoints) {
		const x = xScale.getPixelForValue(percentOf(checkpoint.chainageMeters, totalMeters));
		if (!Number.isFinite(x) || x < area.left || x > area.right) continue;
		const distance = Math.abs(x - pixelX);
		if (distance <= bestDistance) {
			bestDistance = distance;
			best = checkpoint;
		}
	}
	return best;
}

/** The section a chainage falls in, by the same ordering the editor uses. Exported for the readout. */
export function legAt(
	checkpoints: readonly RaceCheckpointDto[], metres: number,
): RaceLegSpan | null {
	const spans = legSpans(checkpoints);
	for (const span of spans) {
		if (metres >= span.fromMetres && metres <= span.toMetres) return span;
	}
	return null;
}

/** The out-of-race grey, re-exported so a consumer needs one import for "the course's colours". */
export { OUT_OF_RACE_COLOR };
