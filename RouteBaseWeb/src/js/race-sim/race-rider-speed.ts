/**
 * One rider's speed against his completion of the course — the series the race speed chart draws.
 *
 * ## There is no recorded speed channel in a race
 *
 * `RaceRealTrackDto` carries lat/lng/elapsedSeconds/chainageMeters and nothing else, so every speed
 * here is DERIVED. What differs between the two rider kinds is what it is derived from, and that is
 * the distinction worth keeping:
 *
 *   measured  a real rider — his own GPS, projected to chainage server-side. A fact about a ride.
 *   modelled  a simulated rider — the pacing model's arrival times. A fact about the model.
 *
 * `isMeasured` says which, so a caller never presents a model output as a measurement.
 *
 * ## Why a bare finite difference, and not `core/speed-window.centredMeanKmh`
 *
 * That function is the right answer for a live "km/h" readout at ONE instant. Applying it per point
 * and then handing the result to `buildSeriesViews` would run a centred window and then a Gaussian
 * over the same data — two stacked kernels, which is the exact bug `speed-window.ts` was written to
 * remove. So the raw channel here is the minimal zero-phase estimate (a centred difference across
 * the neighbouring samples) and ALL smoothing belongs to `core/chart-series-modes.ts`.
 *
 * ## Completion % never goes backwards
 *
 * A real rider's projected chainage jitters on a detour, and a line chart plotted against a
 * non-monotone x reads as a zig-zag rather than as a detour. x is therefore a running maximum —
 * "how far through the course he has got" — which is what completion means. The jitter is not
 * discarded silently: it lands in the speed channel as a standstill, never as a negative speed.
 *
 * Pure: no DOM, no settings read, no Chart.js. `windowSeconds` is passed in, because the caller is
 * the layer that knows about account settings.
 */

import { getAccountSettings } from '../core/account-settings';
import { buildSeriesViews, type SeriesViews } from '../core/chart-series-modes';
import type { BuiltRace } from './race-build';

export interface RiderSpeedSeries {
	readonly participantId: number;
	/** Index into `race.field` / `race.meta` AT THE TIME OF THE BUILD — see the warning below. */
	readonly fieldIndex: number;
	readonly name: string;
	readonly color: string;
	readonly views: SeriesViews;
	/** True when the speed came from a rider's own GPS; false when it is a pacing-model output. */
	readonly isMeasured: boolean;
}

export interface RiderSpeedOptions {
	/** Gaussian width for the smoothed view, in seconds. 0 disables smoothing. */
	readonly windowSeconds?: number;
	/** Decimation target — points actually drawn per series. */
	readonly target?: number;
	/** Time samples across the race, used only when a MODELLED rider is asked for. */
	readonly timeSteps?: number;
}

/** Matches the activity chart's default so the two read at the same resolution. */
const DEFAULT_TARGET = 600;
/** Enough to resolve a climb for a rider who finishes early and so gets only part of the grid. */
const DEFAULT_TIME_STEPS = 600;
/** A sample-indexed fallback kernel, used only if a track somehow has no usable time axis. */
const FALLBACK_SAMPLE_WINDOW = 9;

/**
 * Build the drawable views for the given field indices.
 *
 * ⚠ **It moves the field cursor** when any requested rider is modelled — `RaceField.advanceTo` is the
 * only way to sample a simulated rider, and it advances the whole field. The caller MUST re-render
 * afterwards to put the cursor back on the live clock. Same contract as `race-field-speed.ts`.
 *
 * ⚠ Indices are only valid for the `race` passed in. `buildRace` compacts the field, so a checkpoint
 * edit or a gun/net flip renumbers everything — callers key by `participantId` and re-resolve.
 */
export function computeRiderSpeedSeries(
	race: BuiltRace,
	totalMeters: number,
	fieldIndices: readonly number[],
	options: RiderSpeedOptions = {},
): RiderSpeedSeries[] {
	if (!(totalMeters > 0)) return [];

	const windowSeconds = options.windowSeconds ?? 0;
	const target = options.target ?? DEFAULT_TARGET;

	// Ask for each rider at most once, and in a stable order, so the chart's dataset order does not
	// depend on the order a caller happened to collect the set in.
	const wanted = [...new Set(fieldIndices)]
		.filter((i) => i >= 0 && i < race.field.size)
		.sort((a, b) => a - b);
	if (wanted.length === 0) return [];

	const modelled = wanted.filter((i) => race.participants[i]?.track == null);
	const sampled = modelled.length > 0
		? sampleModelledRiders(race, modelled, options.timeSteps ?? DEFAULT_TIME_STEPS)
		: new Map<number, RawChannel>();

	const out: RiderSpeedSeries[] = [];
	for (const index of wanted) {
		const participant = race.participants[index];
		const meta = race.meta[index];
		if (!participant || !meta) continue;

		const track = participant.track;
		const raw = track ? measuredChannel(track, totalMeters) : sampled.get(index);
		// Fewer than two usable samples cannot make a line. Dropping him is right: an empty dataset
		// would put his name in the legend next to nothing at all.
		if (!raw || raw.xs.length < 2) continue;

		out.push({
			participantId: participant.participantId,
			fieldIndex: index,
			name: meta.name,
			color: meta.color,
			isMeasured: track != null,
			views: buildSeriesViews({
				values: raw.values,
				xs: raw.xs,
				seconds: raw.seconds,
				windowSeconds,
				sampleWindow: FALLBACK_SAMPLE_WINDOW,
				bins: getAccountSettings().charts.speedBinCount,
				target,
			}),
		});
	}
	return out;
}

/** The three lockstep channels `buildSeriesViews` consumes: x (completion %), y (km/h), t (seconds). */
interface RawChannel {
	readonly xs: number[];
	readonly values: number[];
	readonly seconds: number[];
}

/**
 * A real rider, straight off his own track.
 *
 * The speed at a sample is the centred difference across its neighbours — zero phase, and the
 * smallest kernel that is not a forward difference (which would report every acceleration half a
 * sample late). The ends fall back to the one-sided difference that exists.
 */
function measuredChannel(
	track: { readonly elapsedSeconds: readonly number[]; readonly chainageMeters: readonly number[] },
	totalMeters: number,
): RawChannel | null {
	const n = Math.min(track.elapsedSeconds.length, track.chainageMeters.length);
	if (n < 2) return null;

	const xs: number[] = [];
	const values: number[] = [];
	const seconds: number[] = [];
	let reached = 0;

	for (let i = 0; i < n; i++) {
		const t = track.elapsedSeconds[i];
		const chainage = track.chainageMeters[i];
		if (!Number.isFinite(t) || !Number.isFinite(chainage)) continue;

		// Running maximum: completion is how far through the course he has got, and that does not
		// go backwards. Detour jitter stays in the speed channel instead.
		if ((chainage as number) > reached) reached = chainage as number;

		const lo = Math.max(0, i - 1);
		const hi = Math.min(n - 1, i + 1);
		const dt = (track.elapsedSeconds[hi] ?? Number.NaN) - (track.elapsedSeconds[lo] ?? Number.NaN);
		const dm = (track.chainageMeters[hi] ?? Number.NaN) - (track.chainageMeters[lo] ?? Number.NaN);

		xs.push(Math.min(100, (reached / totalMeters) * 100));
		seconds.push(t as number);
		// A backwards step is a projection artefact, not a reversal — read it as a standstill. A
		// non-finite or zero span leaves the sample as a gap, which the chart skips rather than
		// drawing a spike through.
		values.push(dt > 0 ? Math.max(0, (dm / dt) * 3.6) : Number.NaN);
	}

	return xs.length >= 2 ? { xs, values, seconds } : null;
}

/**
 * Simulated riders, sampled off the field.
 *
 * One walk serves all of them: `advanceTo` moves every rider anyway, so asking for a second modelled
 * rider is free. A rider is dropped from the walk once he is finished, so his line ends where his
 * race did rather than running flat along the bottom to the last finisher's time.
 */
function sampleModelledRiders(
	race: BuiltRace,
	indices: readonly number[],
	timeSteps: number,
): Map<number, RawChannel> {
	const out = new Map<number, RawChannel>();
	const endSeconds = race.field.endSeconds;
	const steps = Math.max(2, Math.floor(timeSteps));
	if (!(endSeconds > 0)) return out;

	for (const i of indices) out.set(i, { xs: [], values: [], seconds: [] });

	const dt = endSeconds / steps;
	const totalMeters = race.grid.distance[race.grid.distance.length - 1] ?? 0;
	if (!(totalMeters > 0)) return out;

	const prev = new Map<number, number>();
	race.field.advanceTo(0);
	for (const i of indices) prev.set(i, race.field.chainages[i] ?? 0);

	const done = new Set<number>();
	for (let k = 1; k <= steps; k++) {
		race.field.advanceTo(k * dt);
		for (const i of indices) {
			if (done.has(i)) continue;
			const channel = out.get(i);
			const before = prev.get(i);
			if (!channel || before == null) continue;

			const chainage = race.field.chainages[i] ?? Number.NaN;
			if (Number.isFinite(chainage)) {
				channel.xs.push(Math.min(100, Math.max(0, (chainage / totalMeters) * 100)));
				channel.values.push(Math.max(0, ((chainage - before) / dt) * 3.6));
				channel.seconds.push(k * dt);
				prev.set(i, chainage);
			}
			// Recorded AFTER the step that carried him over the line, so the finish itself is drawn.
			if (race.field.finished[i]) done.add(i);
		}
		if (done.size === indices.length) break;
	}

	return out;
}
