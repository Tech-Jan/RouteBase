/**
 * The race speed chart — every drawn rider's speed against his completion of the course, over the course
 * itself.
 *
 * ## It carries the course now, and that is what made it the primary surface
 *
 * The terrain (tinted per section) and a rule at every checkpoint are drawn behind the riders by
 * `race-speed-backdrop`'s plugin; the field's own p10–p90 aggregate is three datasets on this chart's own
 * km/h axis; the replay playhead is a line on the overlay canvas. Those all used to live on the hand-drawn
 * strip in the map dock, which is the FULLSCREEN replay surface now — one picture instead of two you had
 * to line up by eye. Checkpoint placement came with them (`race-speed-checkpoints.ts`), which is legitimate
 * because x here is `chainage / totalMeters * 100`: a linear reparametrisation of chainage, so a click is
 * still one unambiguous place on a course that passes the same ground seven times.
 *
 * Chart.js, not the hand-drawn canvas strip, and the reason is zoom: `core/canvas-strip.ts` has no
 * zoom concept at all, while the drag/pinch/wheel stack in `core/chart-zoom-pan.ts` +
 * `core/chart-gesture-preview.ts` is already shared by the segment, activity and statistics charts.
 * This module reuses that stack; it does NOT reuse `segment-stats/`, which is a different feature
 * slice and reaches its charts through a page singleton.
 *
 * ## Measured and modelled do not look alike
 *
 * A real rider's line comes from his own GPS; a simulated rider's comes from the pacing model. Those
 * are different kinds of claim, so they get different marks — solid vs dashed, and the legend says
 * "simulated" in words. Identity is never carried by colour alone.
 *
 * ## The hover is NOT a Chart.js tooltip
 *
 * Chart.js's tooltip fires inside its event → update → render cycle, so every mousemove repaints
 * every rider's line and the box visibly flickers. The page-wide answer, which this chart adopts
 * wholesale, is: `events: ["mouseout"]` to shut that cycle off, an overlay-canvas crosshair
 * (`core/chart-overlay`) that never touches the chart, a floating `ChartReadoutBox` above the bar
 * that clamps itself inside the host near the edges, and `core/chart-x-scrub` driving both from a
 * throttled mouse+touch listener — which is what makes it work under a finger.
 *
 * Chart.js is a CDN global (see `vite.config.esm.ts` externals), so it is referenced, never imported.
 */

import { getAccountSettings } from '../core/account-settings';
import { lttbDecimate, nearestIndexForX } from '../core/chart-decimation';
import { scaleInternals } from '../core/chart-internals';
import { GesturePreviewGroup, attachWheelZoom } from '../core/chart-gesture-preview';
import {
	clearVerticalCrosshair, destroyChartOverlay, drawVerticalCrosshair, setOverlayPlayhead,
} from '../core/chart-overlay';
import { withAlpha } from '../core/chart-line-select';
import { ChartReadoutBox, type ReadoutRow } from '../core/chart-readout-box';
import { PROFILE_TRACE_COLOR } from '../core/profile-chart';
import { robustYBounds } from '../core/chart-robust-scale';
import { lineForMode, readoutDecimalsForMode, SERIES_MODES, type SeriesMode } from '../core/chart-series-modes';
import { attachChartXScrub, detachChartXScrub } from '../core/chart-x-scrub';
import { finalizeChart, xZoomConfig } from '../core/chart-zoom-pan';
import { cssVar } from '../core/css-vars';
import type { ChartJsInstance } from '../core/core-types';
import type { RaceCheckpointDto, RaceCourseDto } from './race-api';
import type { FieldSpeedProfile } from './race-field-speed';
import type { RiderSpeedSeries } from './race-rider-speed';
import { createRaceSpeedBackdrop, legAt, metresOf, percentOf } from './race-speed-backdrop';

/** Chart.js is a CDN global, so its types are reached type-only (the pattern in segment-stats-charts). */
type LinePlugins = NonNullable<import('chart.js').ChartOptions<'line'>['plugins']>;

/** One drawn rider, indexed for the readout: his own x grid, so each line is read at ITS nearest point. */
interface RiderLookup {
	readonly label: string;
	readonly color: string;
	readonly xs: number[];
	readonly ys: number[];
}

/** The axis is a percentage: its extent is the definition, not something measured from the data. */
const X_MIN = 0;
const X_MAX = 100;

/**
 * The field aggregate's ink — `PROFILE_TRACE_COLOR`, imported and not restated.
 *
 * The strip traces the SAME aggregate in it, and that constant's own header is explicit about why there
 * is only one of it: "colour follows the entity, not the chart it happens to be drawn on". A local hex
 * here would be a second definition of one entity's colour, free to drift from the surface it shares.
 */
const FIELD_COLOR = PROFILE_TRACE_COLOR;
/** Its p10–p90 spread: the same blue, thinned to a wash so a rider's line reads straight through it. */
const FIELD_BAND_FILL = withAlpha(FIELD_COLOR, 0.16);
/** Riders draw at 0; anything with a higher `order` goes underneath them. */
const FIELD_ORDER = 5;
/**
 * Points actually drawn per rider after a zoom re-decimation.
 *
 * The same number `race-rider-speed` decimates to up front (its DEFAULT_TARGET), so zooming in restores
 * the resolution the whole-course view has rather than inventing a different one.
 */
const DRAW_TARGET = 600;

export interface RaceSpeedChartOptions {
	readonly canvas: HTMLCanvasElement;
	readonly series: readonly RiderSpeedSeries[];
	/** Defaults to 'smoothed' — a dozen recorded lines over each other is noise, not comparison. */
	readonly mode?: SeriesMode;
	/**
	 * The course, drawn behind the riders as leg-tinted terrain with a rule per checkpoint. Omit and the
	 * chart is the bare speed comparison it used to be — which is what a page with no course would want.
	 */
	readonly course?: RaceCourseDto | undefined;
	readonly checkpoints?: readonly RaceCheckpointDto[] | undefined;
	/**
	 * Pointer moved over the plot, in METRES along the course — drives the shared hover (the dot on the
	 * map). Null on leave. Same signature as `RaceProfileOptions.onHover`, so both surfaces feed the one
	 * link. Needs a course: without one a pixel has no chainage to report.
	 */
	readonly onHover?: ((chainageMeters: number | null) => void) | undefined;
}

export interface RaceSpeedChart {
	/** Swap the drawn riders. Zoom survives — the axis is fixed, so a new set is just new datasets. */
	setSeries(series: readonly RiderSpeedSeries[]): void;
	/** Swap the view (recorded / smoothed). Same dataset rebuild, so the zoom survives that too. */
	setMode(next: SeriesMode): void;
	/** Redraw the course's sections and rules — a draft edit, a drag, an Apply. */
	setCheckpoints(checkpoints: readonly RaceCheckpointDto[]): void;
	/** Which boundary the Sections editor has focused; its rule draws solid and bright. Null clears. */
	setSelected(checkpointId: number | null): void;
	/** The field-speed aggregate, as a median line inside its p10–p90 spread. Null removes it. */
	setFieldProfile(profile: FieldSpeedProfile | null): void;
	/**
	 * The watched rider's position (metres along the course) and, while one is followed, the leader's.
	 * Per-FRAME safe: it repaints the overlay canvas only, never the chart.
	 */
	setPlayhead(chainageMeters: number | null, leadMeters?: number | null): void;
	/** The live chart, for the pointer layer that hit-tests checkpoint rules against its x-scale. */
	instance(): ChartJsInstance | null;
	destroy(): void;
}

/** Null when the canvas cannot give a 2d context — a hidden or detached element, not an error. */
export function createRaceSpeedChart(options: RaceSpeedChartOptions): RaceSpeedChart | null {
	const context = options.canvas.getContext('2d');
	if (!context) return null;

	const course = options.course ?? null;
	const total = course?.totalMeters ?? 0;
	let checkpoints: readonly RaceCheckpointDto[] = options.checkpoints ?? [];
	let selected: number | null = null;
	let fieldProfile: FieldSpeedProfile | null = null;

	// Read live by the plugin on every paint, so a drag repaints the rule without rebuilding anything.
	const backdrop = course
		? createRaceSpeedBackdrop({ course, checkpoints: () => checkpoints, selected: () => selected })
		: null;

	// Validate against the declared set rather than coercing: a two-way ternary silently collapsed
	// every mode added later onto 'smoothed'.
	const wanted = options.mode;
	let mode: SeriesMode = wanted && SERIES_MODES.includes(wanted) ? wanted : 'smoothBin';
	const ink = cssVar('--profile-hover-text', '#e2e8f0');
	const gridColor = cssVar('--profile-untinted', 'rgba(148, 163, 184, 0.20)');

	let current: readonly RiderSpeedSeries[] = options.series;
	let lookup: RiderLookup[] = lookupFor(current, mode);

	/**
	 * The field aggregate as three datasets on the RIDERS' OWN y-axis — it is km/h, the same unit, so it
	 * is an honest series rather than a backdrop. p10 first, then p90 filled back to it, then the median
	 * stroked on top; all three sit under the riders (`order`), because a rider is what you came to read.
	 */
	function fieldDatasets(): unknown[] {
		const bins = fieldProfile?.bins ?? [];
		if (bins.length < 2 || !(total > 0)) return [];
		const at = (pick: (b: typeof bins[number]) => number): { x: number; y: number }[] =>
			bins.map((b) => ({ x: percentOf(b.distanceMeters, total), y: pick(b) }));
		const shared = { pointRadius: 0, parsing: false, normalized: true, order: FIELD_ORDER, tension: 0 };
		return [
			// label:'' is what the legend filter keys on — the spread is not a third entry in the key.
			{ ...shared, label: '', data: at((b) => b.p10), borderColor: 'transparent', backgroundColor: FIELD_BAND_FILL },
			{ ...shared, label: '', data: at((b) => b.p90), borderColor: 'transparent', backgroundColor: FIELD_BAND_FILL, fill: '-1' },
			{
				...shared, label: 'Field (median)', data: at((b) => b.median),
				borderColor: FIELD_COLOR, backgroundColor: FIELD_COLOR, borderWidth: 1.5,
			},
		];
	}

	/** The field first so it paints under the riders even where a renderer ignores `order`. */
	function allDatasets(): unknown[] {
		return [...fieldDatasets(), ...datasetsFor(current, mode)];
	}

	/**
	 * Re-decimate every rider for the visible x-window, so zooming in buys DETAIL instead of magnifying
	 * the same points.
	 *
	 * The pattern, and most of the shape, is `chart-factory._redecimateForZoom`: binary-search each
	 * series' own x grid for the window, ask `SeriesViews.rebuildRange` to re-derive the range-dependent
	 * (binned) view over just that slice, then LTTB the result back down to the draw target. Both
	 * `chart-series-modes.ts` and `series-binning.ts` document this as the contract their one-shared-x-grid
	 * design exists to serve — this chart simply never used it, and its lines were pinned at the 600
	 * points `computeRiderSpeedSeries` produced however far in you zoomed.
	 *
	 * Per rider and not once for all, because the series are independently decimated: rider A's index 40
	 * and rider B's index 40 are not the same place on the course. The terrain re-decimates itself off the
	 * same scale window (race-speed-backdrop), so backdrop and lines sharpen together — a crisp profile
	 * under soft rider lines would be the wrong way round.
	 */
	function redecimateForZoom(live: ChartJsInstance, range?: { min: number; max: number }): void {
		// `range` when given: a preview gesture's apply hook runs BEFORE zoomScale moves the scale, so
		// reading the live scale there would re-decimate for the window we are leaving (the trap
		// chart-factory's identical parameter exists for).
		const xScale = live.scales?.x ? scaleInternals(live.scales.x) : undefined;
		const min = range ? range.min : (xScale?.min ?? X_MIN);
		const max = range ? range.max : (xScale?.max ?? X_MAX);
		if (!(max > min)) return;

		const offset = fieldDatasets().length;   // the field's datasets sit before the riders
		const next: RiderLookup[] = [];
		current.forEach((riderSeries, i) => {
			const full = lineForMode(riderSeries.views, mode).full;
			const dataset = live.data.datasets[offset + i];
			if (!dataset || full.length === 0) return;

			// One point either side of the window, so the line enters and leaves the plot rather than
			// starting at the first visible sample.
			const from = Math.max(0, lowerBoundX(full, min) - 1);
			const to = Math.min(full.length - 1, lowerBoundX(full, max) + 1);

			// The binned view is re-derived over the visible slice, so ~the same number of bins stays on
			// screen at every zoom; `recorded` has no range dependence and returns null.
			const rebuilt = riderSeries.views.rebuildRange(mode, from, to);
			const slice = rebuilt ?? full.slice(from, to + 1);
			const drawn = slice.length > DRAW_TARGET ? lttbDecimate(slice, DRAW_TARGET) : slice;
			dataset.data = drawn as never;
			next.push({
				label: riderSeries.isMeasured ? riderSeries.name : `${riderSeries.name} (sim)`,
				color: riderSeries.color,
				xs: drawn.map((p) => p.x),
				ys: drawn.map((p) => p.y),
			});
		});
		// The readout binary-searches this, so it has to describe what is DRAWN — otherwise the number
		// under the cursor keeps coming off the old, coarser line.
		if (next.length === current.length) lookup = next;
	}

	/** First index with x >= value. The views' x grids are ascending by construction. */
	function lowerBoundX(points: readonly { x: number }[], value: number): number {
		let loIdx = 0;
		let hiIdx = points.length - 1;
		while (loIdx < hiIdx) {
			const mid = (loIdx + hiIdx) >>> 1;
			if ((points[mid]?.x ?? 0) < value) loIdx = mid + 1;
			else hiIdx = mid;
		}
		return loIdx;
	}

	/** A legend of one name is furniture. The field counts as a name, so one rider + the field earns it. */
	function legendWanted(): boolean {
		return current.length + (fieldProfile && fieldProfile.bins.length >= 2 ? 1 : 0) > 1;
	}

	/**
	 * The field median as one more readout row — the number a rider's own is read AGAINST.
	 *
	 * A `RiderLookup` and not a special case in `readoutRows`: it is the same shape (a labelled, coloured
	 * line with its own x grid, read at its own nearest point), so it reads the same way and needs no
	 * second code path.
	 */
	function fieldLookup(): RiderLookup[] {
		const bins = fieldProfile?.bins ?? [];
		if (bins.length < 2 || !(total > 0)) return [];
		return [{
			label: 'Field (median)',
			color: FIELD_COLOR,
			xs: bins.map((b) => percentOf(b.distanceMeters, total)),
			ys: bins.map((b) => b.median),
		}];
	}

	/** Robust bounds over everything drawn on the y-axis — the spread's p90 is part of the picture. */
	function yBounds(): { min?: number; max?: number } {
		const lines = current.map((r) => lineForMode(r.views, mode).dec);
		const bins = fieldProfile?.bins ?? [];
		if (bins.length >= 2 && total > 0) {
			lines.push(bins.map((b) => ({ x: percentOf(b.distanceMeters, total), y: b.p10 })));
			lines.push(bins.map((b) => ({ x: percentOf(b.distanceMeters, total), y: b.p90 })));
		}
		const bounds = robustYBounds(lines, { beginAtZero: true });
		return bounds ? { min: bounds.min, max: bounds.max } : {};
	}

	let chart: ChartJsInstance | null = new Chart(context, {
		type: 'line',
		data: { datasets: allDatasets() as never },
		// The course backdrop is a plugin, not a dataset: it is metres against a km/h axis, and it needs a
		// fill clipped per section. See race-speed-backdrop's header.
		plugins: backdrop ? [backdrop.plugin as never] : [],
		options: {
			responsive: true,
			maintainAspectRatio: false,
			animation: false,
			interaction: { mode: 'index', axis: 'x', intersect: false },
			// The hover path is ours, not Chart.js's — see the header. Leaving its native events on is
			// what makes a multi-line chart flicker: each one repaints every rider.
			events: ['mouseout'] as (keyof HTMLElementEventMap)[],
			plugins: ({
				legend: {
					display: legendWanted(),
					position: 'bottom',
					// The spread's two edges are one claim with the median, not three: they carry no label
					// and the filter is what keeps them out of the legend without a second dataset list.
					labels: {
						boxWidth: 12, usePointStyle: true, color: ink,
						filter: (item: { text?: string }) => (item.text ?? '') !== '',
					},
				},
				title: { display: false },
				tooltip: { enabled: false },
				// The plugin stays configured so resetZoom/zoomScale state exists, but its wheel handler
				// is off — wheel zoom goes through attachWheelZoom + the preview group below.
				zoom: xZoomConfig(redecimateForZoom, { wheel: false }),
			} as LinePlugins),
			scales: {
				x: {
					type: 'linear',
					min: X_MIN,
					max: X_MAX,
					grid: { color: gridColor, drawTicks: false },
					ticks: {
						align: 'inner',
						color: ink,
						callback: (v: string | number) => (typeof v === 'number' ? `${v.toFixed(0)}%` : String(v)),
					},
				},
				y: {
					type: 'linear',
					beginAtZero: true,
					grid: { color: gridColor, drawTicks: false },
					ticks: {
						padding: 2,
						color: ink,
						callback: (v: string | number) => (typeof v === 'number' ? `${v.toFixed(0)}` : String(v)),
					},
					...yBounds(),
				},
			},
		},
	}) as ChartJsInstance;

	// A single-chart group still earns its keep: a gesture draws a bitmap preview per frame and pays
	// for one real render at the end, instead of re-rendering every rider's line per wheel tick.
	//
	// beforeApply is the WHEEL half of the re-decimation: wheel zoom does not go through the zoom
	// plugin (wheel:false above), so the plugin's onZoom never fires for it — the group's apply hook is
	// where a wheel gesture lands. Same division of labour as chart-factory.
	const preview = new GesturePreviewGroup(() => [chart], {
		beforeApply: (c, range) => redecimateForZoom(c, range),
	});
	finalizeChart(options.canvas, chart, 'raceSpeed', null, preview);
	// The DATA extent, not the axis options — a zoomed apply mutates the axis, so reading it back
	// would make zoom-out clamp to wherever the user stopped (CG-02).
	attachWheelZoom(options.canvas, () => chart, preview, () => ({ min: X_MIN, max: X_MAX }));

	// The readout floats in the canvas's own wrapper, which the CSS makes position:relative. Same host
	// contract as the activity charts' .chart-container.
	const host = options.canvas.parentElement;
	const readout = host ? new ChartReadoutBox(host) : null;
	const decimals = readoutDecimalsForMode(mode);

	function hideHover(): void {
		clearVerticalCrosshair(chart);
		readout?.hide();
		// The dot on the map goes with the crosshair — leaving it behind would mark a place nobody is
		// pointing at any more.
		if (total > 0) options.onHover?.(null);
	}
	// The ✕ a coarse pointer gets: dismissing the box must take the crosshair with it.
	readout?.setOnClose(hideHover);

	attachChartXScrub(options.canvas, {
		getChart: () => chart,
		onScrub: (pixelX, live) => {
			const xValue = live.scales.x?.getValueForPixel(pixelX);
			if (xValue == null) return;
			// Drawn on the overlay canvas, so the chart itself is never re-rendered — no flicker.
			drawVerticalCrosshair(live, xValue);
			// Where that is on the ground. The strip owns the map dot; this is the door into it.
			if (total > 0) options.onHover?.(metresOf(xValue, total));
			readout?.show(
				hoverTitle(xValue),
				// The field LAST: it is the context a rider's number is read against, not the subject.
				readoutRows([...lookup, ...fieldLookup()], xValue, decimals),
				pixelX,
				live.chartArea.top,
			);
		},
		onLeave: hideHover,
	});

	/**
	 * Percent, and — once there is a course — the km and the section it names. The percentage alone is
	 * the one number nobody navigates by: the Sections editor, the results columns and the course itself
	 * are all in km and section names.
	 */
	function hoverTitle(xValue: number): string {
		const parts = [`${xValue.toFixed(1)} %`];
		if (total > 0) {
			const metres = (xValue / 100) * total;
			parts.push(`${(metres / 1000).toFixed(2)} km`);
			const leg = legAt(checkpoints, metres);
			if (leg?.label) parts.push(leg.label);
		}
		return parts.join(' · ');
	}

	/** Rebuild the datasets from `current` + `mode` and repaint without disturbing the zoom. */
	function redraw(): void {
		if (!chart) return;
		lookup = lookupFor(current, mode);
		chart.data.datasets = allDatasets() as never;
		const legend = chart.options.plugins?.legend;
		if (legend) legend.display = legendWanted();
		const scaleY = chart.options.scales?.y as Record<string, unknown> | undefined;
		if (scaleY) Object.assign(scaleY, yBounds());
		// A rider who was being read may not be in the new set — the open box would keep quoting him.
		hideHover();
		// allDatasets() rebuilds at WHOLE-COURSE resolution, so a rebuild while zoomed in (a mode switch,
		// an emphasis change) would silently coarsen every line back. Re-slice to the window first —
		// chart-factory re-decimates after its own mode swap for the same reason.
		const xScale = chart.scales?.x ? scaleInternals(chart.scales.x) : undefined;
		if ((xScale?.min ?? X_MIN) > X_MIN || (xScale?.max ?? X_MAX) < X_MAX) redecimateForZoom(chart);
		// 'none' keeps the current zoom window: the x-axis is a fixed 0–100 %, so a different set of
		// riders (or a different view of them) describes the stretch the user is already looking at.
		chart.update('none');
	}

	/**
	 * The decorations live in the plugin's closure, so a checkpoint change needs a REPAINT, not an update.
	 *
	 * `draw()` and not `update('none')`: no dataset changed, so re-running the update pipeline over every
	 * rider would be pure waste — and this is called once per pointermove while a boundary is being
	 * dragged, which is exactly where that waste becomes visible. `draw()` re-enters the plugin's
	 * beforeDatasetsDraw from the scales already laid out. Same call, same reason, as the segment charts'
	 * cross-synced hover (segment-stats-hover.setChartActive).
	 */
	function repaintBackdrop(): void {
		chart?.draw();
	}

	return {
		setSeries(series) {
			if (!chart) return;
			current = series;
			redraw();
		},
		setMode(next) {
			if (!chart || next === mode || !SERIES_MODES.includes(next)) return;
			mode = next;
			redraw();
		},
		setCheckpoints(next) {
			checkpoints = [...next].sort((a, b) => a.chainageMeters - b.chainageMeters);
			repaintBackdrop();
		},
		setSelected(checkpointId) {
			if (selected === checkpointId) return;
			selected = checkpointId;
			repaintBackdrop();
		},
		setFieldProfile(profile) {
			fieldProfile = profile;
			redraw();
		},
		setPlayhead(chainageMeters, leadMeters = null) {
			if (!chart || !(total > 0)) return;
			setOverlayPlayhead(
				chart,
				chainageMeters == null ? null : percentOf(chainageMeters, total),
				leadMeters == null ? null : percentOf(leadMeters, total),
			);
		},
		instance: () => chart,
		destroy() {
			detachChartXScrub(options.canvas);
			readout?.hide();
			// MUST precede chart.destroy(): the overlay's ResizeObserver watches the persistent canvas,
			// so its closure would otherwise hold the destroyed chart and its overlay canvas alive.
			destroyChartOverlay(chart);
			chart?.destroy();
			chart = null;
		},
	};
}

/**
 * One readout row per rider, each read at the point of HIS OWN line nearest the hovered x.
 *
 * Per-rider rather than one shared index: the series are independently decimated, so rider A's
 * point 40 and rider B's point 40 are not the same place on the course.
 */
function readoutRows(lookup: readonly RiderLookup[], xValue: number, decimals: number): ReadoutRow[] {
	const rows: ReadoutRow[] = [];
	for (const rider of lookup) {
		if (rider.xs.length === 0) continue;
		const i = nearestIndexForX(rider.xs, xValue);
		const y = rider.ys[i];
		rows.push({
			color: rider.color,
			label: rider.label,
			// Fixed widths so the box does not reflow (and jitter) as the numbers change under the cursor.
			labelWidthCh: 14,
			cells: [{ num: y != null && Number.isFinite(y) ? y.toFixed(decimals) : '--', unit: 'km/h', numWidthCh: 5 }],
		});
	}
	return rows;
}

/** The x/y grids the readout binary-searches. Built with the datasets so the two cannot disagree. */
function lookupFor(series: readonly RiderSpeedSeries[], mode: SeriesMode): RiderLookup[] {
	return series.map((rider) => {
		const points = lineForMode(rider.views, mode).dec;
		return {
			label: rider.isMeasured ? rider.name : `${rider.name} (sim)`,
			color: rider.color,
			xs: points.map((p) => p.x),
			ys: points.map((p) => p.y),
		};
	});
}

/** One dataset per rider, in the order given — `computeRiderSpeedSeries` already made that stable. */
function datasetsFor(series: readonly RiderSpeedSeries[], mode: SeriesMode): unknown[] {
	const { tension, borderWidth } = getAccountSettings().charts;
	return series.map((rider) => ({
		label: rider.isMeasured ? rider.name : `${rider.name} (simulated)`,
		data: lineForMode(rider.views, mode).dec,
		borderColor: rider.color,
		backgroundColor: rider.color,
		// Dashed = modelled. A pacing-model output must not be able to pass for a measured ride.
		borderDash: rider.isMeasured ? undefined : [6, 4],
		borderWidth,
		tension,
		pointRadius: 0,
		// The points are pre-sorted and already decimated by buildSeriesViews, so Chart.js can skip
		// both its parsing pass and its own sorting.
		parsing: false,
		normalized: true,
		spanGaps: false,
	}));
}

