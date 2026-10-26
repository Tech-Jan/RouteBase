/**
 * The race's view of the shared profile strip: the course's terrain, the legs, the checkpoints — and
 * the field, RIDING it.
 *
 * The drawing lives in `core/profile-chart.ts`; everything race-shaped lives here. That split is the
 * whole file: legs become `ProfileBand`s, checkpoints become `ProfileMarker`s, and the place/drag
 * gestures become a `ProfileInteraction` over `snapCheckpoint`. The chart owns pixels, this owns rules.
 *
 * **Checkpoints are placed HERE and not on the map, and that is a correctness decision, not a layout
 * one.** The profile's x-axis IS chainage, so a click at 8.5 km means 8.5 km — full stop. On the map it
 * would not: the Ski to Sea course passes the Mt Baker ski area SEVEN times, so one pixel there is seven
 * different chainages and no amount of snapping can tell them apart. (Measured: projecting the course's
 * own waypoints by nearest-point put the start checkpoint at 5.147 km instead of 0.)
 */

import type { RaceCheckpointDto, RaceCourseDto } from './race-api';
import { must } from '../core/invariant';
import { checkpointBoundsFor, roleOf, snapCheckpoint } from './race-clock';
import {
	createProfileChart, PROFILE_TRACE_COLOR,
	type ProfileBand, type ProfileChart, type ProfileMarker, type ProfileTrace,
} from '../core/profile-chart';
import type { FieldSpeedProfile } from './race-field-speed';

/** One leg as it is drawn: the span between two consecutive checkpoints, in the leg's colour. */
export interface RaceLegSpan {
	readonly fromMetres: number;
	readonly toMetres: number;
	readonly color: string | null;
	readonly label: string | null;
}

/**
 * The colour of ride that is NOT race. Uncoloured on purpose — no leg colour may be mistaken for it — and
 * shared with the map (race-course-line.ts) so the two surfaces cannot disagree about what is out of play.
 */
export const OUT_OF_RACE_COLOR = '#8b8d98';

/**
 * The stretches of ride outside the race: before the first checkpoint, after the last.
 *
 * The course grid is the whole recorded activity and the race is a WINDOW on it — the roll-out to the line,
 * the cooldown past it, a wrong turn at the end. Empty until someone moves an endpoint, which is why this
 * returns a list rather than a pair.
 *
 * `minSpanMeters` drops a span too thin to draw (the map wants a whole grid step; the profile, a metre).
 */
export function outOfRaceSpans(
	checkpoints: readonly RaceCheckpointDto[], totalMeters: number, minSpanMeters = 1,
): RaceLegSpan[] {
	if (checkpoints.length === 0) return [];
	const chainages = checkpoints.map((c) => c.chainageMeters);
	const start = Math.min(...chainages);
	const finish = Math.max(...chainages);
	const spans: RaceLegSpan[] = [];
	if (start > minSpanMeters) {
		spans.push({ fromMetres: 0, toMetres: start, color: OUT_OF_RACE_COLOR, label: null });
	}
	if (finish < totalMeters - minSpanMeters) {
		spans.push({ fromMetres: finish, toMetres: totalMeters, color: OUT_OF_RACE_COLOR, label: null });
	}
	return spans;
}

/**
 * The legs, in order. A leg is coloured by the checkpoint it FOLLOWS (RaceCheckpoint.legColor), so the
 * span from checkpoint i to i+1 wears checkpoint i's colour.
 */
export function legSpans(checkpoints: readonly RaceCheckpointDto[]): RaceLegSpan[] {
	const ordered = [...checkpoints].sort((a, b) => a.seq - b.seq);
	const spans: RaceLegSpan[] = [];
	for (let i = 0; i < ordered.length - 1; i++) {
		const from = must(ordered[i], 'i < length-1 (loop-bounded)');
		const to = must(ordered[i + 1], 'i+1 <= length-1 (loop-bounded)');
		spans.push({
			fromMetres: from.chainageMeters,
			toMetres: to.chainageMeters,
			color: from.legColor,
			label: from.legName,
		});
	}
	return spans;
}

/**
 * The field-speed aggregate as a trace over the course terrain: the median line inside its p10–p90
 * spread, on the profile's own chainage axis. This used to be a second canvas stacked underneath; it is
 * one strip now, because "where the course climbs, the field slows" is one picture and reading it off
 * two charts meant carrying an x-position between them by eye.
 *
 * Bins are SPARSE — a stretch nobody rode files no bin — so the x positions travel with the values
 * rather than being implied by a step.
 */
export function speedTrace(profile: FieldSpeedProfile | null): ProfileTrace | null {
	if (!profile || profile.bins.length < 2) return null;
	const bins = profile.bins;
	const x = new Float64Array(bins.length);
	const y = new Float64Array(bins.length);
	const low = new Float64Array(bins.length);
	const high = new Float64Array(bins.length);
	for (let i = 0; i < bins.length; i++) {
		const b = must(bins[i], 'i < bins.length (loop-bounded)');
		x[i] = b.distanceMeters;
		y[i] = b.median;
		low[i] = b.p10;
		high[i] = b.p90;
	}
	return { x, y, band: { low, high }, color: PROFILE_TRACE_COLOR };
}

/** The field, as the profile needs it. Index-aligned with the race field and the markers. */
export interface RaceProfileField {
	/** Metres along the course, per rider. Read every frame — pass the live array, do not copy it. */
	readonly chainages: ArrayLike<number>;
	/** 1 when the rider has finished. Finished riders fade rather than pile up on the line. */
	readonly finished: ArrayLike<number>;
	readonly colors: readonly string[];
}

export interface RaceProfileOptions {
	readonly canvas: HTMLCanvasElement;
	readonly course: RaceCourseDto;
	/** Placing and dragging are writes, so only the race's owner gets them. */
	readonly editable: boolean;
	readonly onPlace?: ((chainageMeters: number) => void) | undefined;
	readonly onMove?: ((checkpointId: number, chainageMeters: number) => void) | undefined;
	readonly onSelect?: ((checkpointId: number) => void) | undefined;
	/** Pointer moved over the strip (metres along the course) — drives the shared hover. Null on leave. */
	readonly onHover?: ((chainageMeters: number | null) => void) | undefined;
	readonly now?: (() => number) | undefined;
}

export interface RaceProfile {
	setCheckpoints(checkpoints: readonly RaceCheckpointDto[]): void;
	/** The field-speed aggregate, drawn over the terrain as a median line inside its spread band. */
	setSpeedProfile(profile: FieldSpeedProfile | null): void;
	/** The field to animate. Call once; the arrays are read live thereafter. */
	setField(field: RaceProfileField | null): void;
	/** Repaint riders (the results table painted a group). Index → colour. */
	setColors(colors: ReadonlyMap<number, string>): void;
	/** Which riders the results table's filter MATCHED — see ProfileChart.setFilter for the contract. */
	setFilter(filter: { bright?: ReadonlySet<number> | null; visible?: ReadonlySet<number> | null }): void;
	/** Which riders are emphasised — bigger dots, drawn on top. Null clears. */
	setEmphasis(indices: ReadonlySet<number> | null): void;
	/** Where the WATCHED rider is, in metres — the leader, or whoever is followed. Null hides the playhead. */
	setPlayhead(chainageMeters: number | null): void;
	/** The leader's golden rule, drawn only while the playhead is a followed rider. Null clears it. */
	setLeadLine(chainageMeters: number | null): void;
	/** Draw the hover line here (metres) — called when the other chart or the map is hovered. Null clears. */
	setHover(chainageMeters: number | null): void;
	/** Arm placement: only while armed does a click on an empty stretch place a checkpoint. */
	setPlaceable(armed: boolean): void;
	setSelected(checkpointId: number | null): void;
	/** Redraw the moving half. `force` bypasses the 30 Hz cap — a seek must not lag the playhead. */
	tick(force?: boolean): void;
	destroy(): void;
}

export function createRaceProfile(options: RaceProfileOptions): RaceProfile {
	const { course, editable } = options;

	/** Copies, not the caller's objects: a drag moves one locally before the server hears about it. */
	let checkpoints: RaceCheckpointDto[] = [];
	let selected: number | null = null;
	// Placement is armed by the Sections editor's "Add checkpoint" button — a click places only while armed.
	let placeable = false;
	/** Where a drag started, so a drag that never found a legal spot writes nothing on release. */
	let dragOrigin: number | null = null;

	/** Where this checkpoint may be dragged to, against every other one and the grid's length. */
	function boundsOf(checkpointId: number): ReturnType<typeof checkpointBoundsFor> {
		const target = checkpoints.find((c) => c.checkpointId === checkpointId);
		const others = checkpoints.filter((c) => c.checkpointId !== checkpointId).map((c) => c.chainageMeters);
		return checkpointBoundsFor(
			target ? roleOf(target, checkpoints) : 'intermediate', others, course.totalMeters);
	}

	// The out-of-race spans are bands too, and they are not decoration: paintTerrain fills the profile
	// ONCE PER BAND, clipped to that band's span, so a stretch with no band is not drawn at all. Without
	// these, moving an endpoint inward would erase that end of the terrain rather than dim it.
	function bands(): ProfileBand[] {
		return [...outOfRaceSpans(checkpoints, course.totalMeters), ...legSpans(checkpoints)]
			.map((s) => ({ fromValue: s.fromMetres, toValue: s.toMetres, color: s.color }));
	}

	function markers(): ProfileMarker[] {
		return checkpoints.map((c) => ({
			id: c.checkpointId,
			atValue: c.chainageMeters,
			emphasised: c.checkpointId === selected,
		}));
	}

	function pushDecorations(): void {
		chart.setDecorations({ bands: bands(), markers: markers() });
	}

	const chart: ProfileChart = createProfileChart({
		canvas: options.canvas,
		// A race always has ONE course everyone rides — the shared-terrain backdrop, and the three fields
		// of it the chart reads come straight off RaceCourseDto.
		backdrop: {
			kind: 'terrain',
			elevation: course.elevation,
			step: course.stepMeters,
			axisMax: course.totalMeters,
		},
		onHover: options.onHover,
		now: options.now,
		// Chainage · elevation · field speed. The speed term is dropped rather than zeroed where the
		// aggregate has no bin — no rider was ever measured there.
		formatHover: (metres, elevation, speed) => {
			const parts = [`${(metres / 1000).toFixed(2)} km`];
			if (elevation != null) parts.push(`${Math.round(elevation)} m`);
			if (speed != null) parts.push(`${Math.round(speed)} km/h`);
			return parts.join(' · ');
		},
		interaction: editable ? {
			onPointerDownAt(metres, checkpointId) {
				if (checkpointId != null) {
					const hit = checkpoints.find((c) => c.checkpointId === checkpointId);
					selected = checkpointId;
					options.onSelect?.(checkpointId);
					pushDecorations();
					// Start and Finish drag too — they are where the user says the lines were, not the ends of
					// whatever GPS the ride contains. boundsOf keeps each one extremal and on the grid.
					if (!hit) return null;
					dragOrigin = hit.chainageMeters;
					return checkpointId;
				}

				// Placement is gated: a click on an empty stretch does nothing unless the editor armed it.
				if (!placeable) return null;
				// A new checkpoint is an intermediate: strictly inside the two lines, and snapCheckpoint refuses
				// a spot on top of an existing one — two at one chainage is a zero-length interval, which is a
				// divide-by-zero in the pacing model.
				const chainages = checkpoints.map((c) => c.chainageMeters);
				const snapped = snapCheckpoint(
					metres, checkpoints, checkpointBoundsFor('intermediate', chainages, course.totalMeters));
				if (snapped != null) options.onPlace?.(snapped);
				return null;
			},
			onDragTo(checkpointId, metres) {
				const others = checkpoints.filter((c) => c.checkpointId !== checkpointId);
				const snapped = snapCheckpoint(metres, others, boundsOf(checkpointId));
				if (snapped == null) return;

				const target = checkpoints.find((c) => c.checkpointId === checkpointId);
				if (!target) return;

				// Move it locally so the drag feels live; the server is told once, on release.
				(target as { chainageMeters: number }).chainageMeters = snapped;
				pushDecorations();
			},
			onDragEnd(checkpointId) {
				const target = checkpoints.find((c) => c.checkpointId === checkpointId);
				const from = dragOrigin;
				dragOrigin = null;
				// A drag whose every position was refused (all inside the min-gap) moved nothing — say nothing.
				if (target && target.chainageMeters !== from) options.onMove?.(checkpointId, target.chainageMeters);
			},
		} : undefined,
	});

	return {
		setCheckpoints(next) {
			checkpoints = next.map((c) => ({ ...c })).sort((a, b) => a.seq - b.seq);
			pushDecorations();
		},
		setSpeedProfile: (next) => chart.setTrace(speedTrace(next)),
		setField(next) {
			chart.setField(next ? { values: next.chainages, finished: next.finished, colors: next.colors } : null);
		},
		setColors: (next) => chart.setColors(next),
		setFilter: (filter) => chart.setFilter(filter),
		setEmphasis: (indices) => chart.setEmphasis(indices),
		setPlayhead: (metres) => chart.setPlayhead(metres),
		setLeadLine: (metres) => chart.setLeadLine(metres),
		setHover: (metres) => chart.setHover(metres),
		setPlaceable(armed) {
			placeable = armed;
			// A crosshair says "click to place"; the default cursor says a click here does nothing.
			chart.setCursor(armed ? 'crosshair' : 'default');
		},
		setSelected(checkpointId) {
			selected = checkpointId;
			pushDecorations();
		},
		tick: (force = false) => chart.tick(force),
		destroy: () => chart.destroy(),
	};
}
