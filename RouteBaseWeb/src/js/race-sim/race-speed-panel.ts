/**
 * The race speed panel — resolves the DOM, decides WHICH riders are drawn, and keeps the chart in
 * step with the race.
 *
 * It exists so `race-replay-view.ts` gains four lines rather than forty: everything about the panel
 * (its canvas, its empty state, its rider set, its settings read) lives here, and the view only says
 * "the race changed".
 *
 * ## Who gets a line
 *
 * Real riders, plus the emphasised ones, minus whoever the owner hid — asked of `labelledIndices`, the
 * SAME function that decides who gets a name label on the map. Reusing it is the point: two answers to
 * "which riders are we calling out" would drift, and the map and this chart would disagree about who is
 * worth watching.
 *
 * ## The chart is built even when nobody is drawn
 *
 * It used to be built lazily, on the first update that had a rider, and the empty state HID THE CANVAS.
 * Neither survives this panel owning the course: the terrain, the checkpoint rules and — for an owner —
 * click-to-place a section boundary all have to be there on a race nobody has highlighted yet, which is
 * exactly the state a race is in when its sections are first laid out. So the chart is built on the first
 * update, and "no rider lines yet" is a note in the panel head, not a curtain over the canvas.
 *
 * ## Not a per-frame surface — except the playhead
 *
 * `update` is for human-scale events: a rebuild, an emphasis change. `setPlayhead` IS called per frame and
 * is safe to, because it repaints the overlay canvas and never the chart (core/chart-overlay).
 */

import { setChartEmptyState } from '../core/chart-empty-state';
import { getSpeedSmoothingSeconds } from '../core/smoothing';
import type { RaceCheckpointDto, RaceCourseDto } from './race-api';
import type { BuiltRace } from './race-build';
import type { FieldSpeedProfile } from './race-field-speed';
import { calloutOffIndices, labelledIndices } from './race-name-labels';
import { computeRiderSpeedSeries, type RiderSpeedSeries } from './race-rider-speed';
import { createRaceSpeedChart, type RaceSpeedChart } from './race-speed-chart';
import { createRaceSpeedCheckpoints, type RaceSpeedCheckpoints } from './race-speed-checkpoints';
import { createSeriesModeControl, type SeriesModeControl } from '../core/series-mode-control';

/** Says what to DO about it, not just that it is empty — the panel is the only place that can. */
const EMPTY_MESSAGE =
	'Add a real rider, or highlight riders in the results table, to compare their speed here.';

export interface RaceSpeedPanelOptions {
	readonly canvasId?: string;
	/** The element wrapping the canvas — the empty-state message replaces the canvas inside it. */
	readonly hostId?: string;
	/** Where the "no rider lines yet" note goes. Absent = no note; the chart still draws the course. */
	readonly noteId?: string;
	/** The course. Without it the chart is a bare speed comparison and places no checkpoints. */
	readonly course?: RaceCourseDto | undefined;
	readonly checkpoints?: readonly RaceCheckpointDto[] | undefined;
	/** Placing and dragging are writes, so only the race's owner gets them. */
	readonly editable?: boolean;
	readonly onPlace?: ((chainageMeters: number) => void) | undefined;
	readonly onMove?: ((checkpointId: number, chainageMeters: number) => void) | undefined;
	readonly onSelect?: ((checkpointId: number) => void) | undefined;
	/** Pointer over the plot, in metres along the course — drives the dot on the map. Null on leave. */
	readonly onHover?: ((chainageMeters: number | null) => void) | undefined;
	/**
	 * Called after an update that MOVED THE FIELD CURSOR (a modelled rider was drawn, and sampling one
	 * means walking the field). The view must re-render to put the cursor back on the live clock —
	 * without it, a paused replay would leave every marker parked wherever the walk finished.
	 */
	readonly onFieldWalked?: () => void;
}

export interface RaceSpeedPanel {
	/** Re-derive the drawn set and repaint. Human-scale events only — see the header. */
	update(race: BuiltRace, totalMeters: number): void;
	/** The course's sections changed — a draft edit, a drag, an Apply. */
	setCheckpoints(checkpoints: readonly RaceCheckpointDto[]): void;
	/** Which boundary the Sections editor has focused. Null clears. */
	setSelected(checkpointId: number | null): void;
	/** Arm placement: only while armed does a click on an empty stretch place a checkpoint. */
	setPlaceable(armed: boolean): void;
	/** The field-speed aggregate — the same one the fullscreen strip traces. */
	setFieldProfile(profile: FieldSpeedProfile | null): void;
	/** The watched rider's chainage and, while one is followed, the leader's. Per-frame safe. */
	setPlayhead(chainageMeters: number | null, leadMeters?: number | null): void;
	destroy(): void;
}

/** Null when the page does not render the panel — not every race surface has to. */
export function createRaceSpeedPanel(options: RaceSpeedPanelOptions = {}): RaceSpeedPanel | null {
	const element = document.getElementById(options.canvasId ?? 'raceSpeedChart');
	if (!(element instanceof HTMLCanvasElement)) return null;
	// Re-bound so the narrowing survives into the closures below (a captured union does not).
	const canvas: HTMLCanvasElement = element;
	const host = document.getElementById(options.hostId ?? 'raceSpeedPanel');
	const note = document.getElementById(options.noteId ?? 'raceSpeedNote');

	let chart: RaceSpeedChart | null = null;
	/** Built with the chart, so a race that never draws one never wires a control either. */
	let modeControl: SeriesModeControl | null = null;
	let pointer: RaceSpeedCheckpoints | null = null;
	let checkpoints: readonly RaceCheckpointDto[] = options.checkpoints ?? [];
	/** Held until the chart exists — every setter below may be called before the first update. */
	let pendingPlaceable = false;
	let pendingSelected: number | null = null;
	let fieldProfile: FieldSpeedProfile | null = null;

	/**
	 * Say that nobody is drawn — and WHERE to say it, which turns on whether the canvas still has
	 * something on it.
	 *
	 * With a course, the canvas is the course: terrain, section tints, the boundaries an owner clicks to
	 * place. Curtaining that off (which is what `setChartEmptyState` does — it hides the canvas) would
	 * take the whole editing surface away at exactly the moment a race has no highlighted riders, which is
	 * when its sections are being laid out. So the message goes beside the title instead.
	 *
	 * Without a course there is genuinely nothing on the canvas, and a blank chart with no explanation is
	 * the "silent blank chart" `core/chart-empty-state` exists to stop. That case keeps the overlay.
	 */
	function sayEmpty(isEmpty: boolean): void {
		if (options.course && note) {
			note.textContent = isEmpty ? EMPTY_MESSAGE : '';
			note.hidden = !isEmpty;
			return;
		}
		setChartEmptyState(host, EMPTY_MESSAGE, isEmpty);
	}

	/**
	 * Build on the first update, with whatever the race has — including nothing.
	 *
	 * Returns whether it built one, so the caller can skip the `setSeries` that would immediately
	 * re-push the series the chart was just constructed on (a wasted full redraw).
	 */
	function ensureChart(series: readonly RiderSpeedSeries[]): boolean {
		if (chart) return false;
		chart = createRaceSpeedChart({
			canvas,
			series,
			course: options.course,
			checkpoints,
			onHover: options.onHover,
			...(modeControl ? { mode: modeControl.mode } : {}),
		});
		if (!chart) return false;

		// Wired after the chart exists: the control calls back immediately with the restored mode, which
		// the chart was just built on, so the call is a no-op rather than a repaint.
		modeControl ??= createSeriesModeControl({
			button: document.getElementById('raceSpeedModeToggle'),
			prefKey: 'raceSpeed',
			onChange: (mode) => chart?.setMode(mode),
		});

		// Placement needs a course: without one there is no chainage for a pixel to mean. Selection is
		// wired for a viewer too — clicking a rule still highlights its Sections row on the share fork.
		if (options.course) {
			pointer = createRaceSpeedCheckpoints({
				canvas,
				chart: () => chart?.instance() ?? null,
				totalMeters: options.course.totalMeters,
				checkpoints: () => checkpoints,
				editable: options.editable === true,
				onPlace: options.onPlace,
				onMove: options.onMove,
				onSelect: options.onSelect,
				// A drag is live before the server hears about it: move the rule locally, repaint, and let
				// the editor's draft catch up on release.
				onLocalMove: (checkpointId, chainageMeters) => {
					checkpoints = checkpoints.map(
						(c) => (c.checkpointId === checkpointId ? { ...c, chainageMeters } : c));
					chart?.setCheckpoints(checkpoints);
				},
			});
			pointer.setPlaceable(pendingPlaceable);
		}
		chart.setSelected(pendingSelected);
		chart.setFieldProfile(fieldProfile);
		return true;
	}

	return {
		update(race, totalMeters) {
			const drawn = labelledIndices(
				race.meta.map((m) => m.isReal), emphasisSet(race), null, true,
				calloutOffIndices(race.participants));

			// Detected BEFORE the call, because the walk is the call's side effect: a modelled rider has
			// no track of his own, so the only way to sample him is to advance the whole field.
			const walksTheField = drawn.some((i) => race.participants[i]?.track == null);
			const series = drawn.length === 0 ? [] : computeRiderSpeedSeries(race, totalMeters, drawn, {
				windowSeconds: getSpeedSmoothingSeconds(),
			});
			if (walksTheField && drawn.length > 0) options.onFieldWalked?.();

			sayEmpty(series.length === 0);
			// A chart built on THIS series does not need to be told it again — that is a full redraw for
			// nothing, and it is the difference between one and two paints on page load.
			if (!ensureChart(series)) chart?.setSeries(series);
		},
		setCheckpoints(next) {
			checkpoints = [...next];
			chart?.setCheckpoints(checkpoints);
		},
		setSelected(checkpointId) {
			pendingSelected = checkpointId;
			chart?.setSelected(checkpointId);
		},
		setPlaceable(armed) {
			pendingPlaceable = armed;
			pointer?.setPlaceable(armed);
		},
		setFieldProfile(profile) {
			fieldProfile = profile;
			chart?.setFieldProfile(profile);
		},
		setPlayhead(chainageMeters, leadMeters = null) {
			chart?.setPlayhead(chainageMeters, leadMeters);
		},
		destroy() {
			pointer?.destroy();
			pointer = null;
			modeControl?.destroy();
			modeControl = null;
			chart?.destroy();
			chart = null;
		},
	};
}

/**
 * The emphasised riders as field indices.
 *
 * Read off the participant objects the payload owns — the same source `race-replay-view`'s own
 * emphasisSet reads, so there is no second copy of the flag to fall out of step with the first.
 */
function emphasisSet(race: BuiltRace): Set<number> {
	const set = new Set<number>();
	race.participants.forEach((p, i) => { if (p.emphasised === true) set.add(i); });
	return set;
}
