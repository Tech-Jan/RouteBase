/**
 * Segment Statistics — Replay live-data math (pure, DOM-free).
 *
 * The per-frame arithmetic that feeds the replay effort sidebar, split out of
 * segment-stats-replay-ui.ts so it is unit-testable without a DOM or the live
 * replay engine. Every function takes plain data in and returns plain values
 * out; the UI controller reads the engine getters and does the DOM writes.
 *
 * The live speed is measured from each effort's DISTANCE channel rather than
 * re-averaging its speed channel (which the server already smoothed) — see
 * core/speed-window.ts for why, and for why the window is centred and scales
 * with the playback rate. The only stateful collaborator left is the row's
 * label latch, which is passed in, so this module stays testable without mocks.
 */
import type { ReplayEffort, ReplayPointSample } from './segment-stats-replay-types';
import { buildSpeedScale, type SpeedSample, type SpeedScale } from '../core/speed-scale';
import { centredMeanKmh, windowSecondsFor, type DistanceSeries } from '../core/speed-window';
import { createSpeedLabel, type SpeedLabel } from '../core/speed-label';
import { formatTime } from '../core/format';
import { must } from '../core/invariant';

/**
 * Build the fixed speed scale from every effort's points (time-weighted by
 * the gap between consecutive samples). Uses the full loaded effort set so the
 * scale is stable regardless of which efforts are checked on/off.
 */
export function buildSpeedScaleFromEfforts(efforts: ReplayEffort[]): SpeedScale {
	const samples: SpeedSample[] = [];
	for (const effort of efforts) {
		const pts = effort.points;
		if (!pts || pts.length < 2) continue;
		for (let p = 0; p < pts.length - 1; p++) {
			const a = must(pts[p], 'p < length-1 (loop-bounded)');
			const b = must(pts[p + 1], 'p+1 <= length-1 (loop-bounded)');
			const dt = b.time - a.time;
			samples.push({ speed: a.speed, weight: dt > 0 ? dt : 0 });
		}
	}
	return buildSpeedScale(samples);
}

/**
 * One effort's (ride seconds → cumulative km) samples, for the speed window.
 * Null when the effort is too short to measure across. Effort distance is in
 * METRES, hence the divide — core/speed-window works in km.
 */
export function buildEffortDistanceSeries(effort: ReplayEffort | undefined): DistanceSeries | null {
	const pts = effort?.points;
	if (!pts || pts.length < 2) return null;
	return {
		length: pts.length,
		secondsAt: (i) => pts[i]?.time ?? Number.NaN,
		kmAt: (i) => (pts[i]?.distance ?? Number.NaN) / 1000,
	};
}

/** The per-effort collaborators the sidebar needs, built once per effort list. */
export interface EffortLiveModel {
	speedScale: SpeedScale;
	/** One label latch per effort — the latches are per row, not shared. */
	labels: SpeedLabel[];
	/** One distance series per effort, lockstep with `labels`. */
	series: (DistanceSeries | null)[];
}

export function buildEffortLiveModel(efforts: ReplayEffort[]): EffortLiveModel {
	return {
		speedScale: buildSpeedScaleFromEfforts(efforts),
		labels: efforts.map(() => createSpeedLabel()),
		series: efforts.map((effort) => buildEffortDistanceSeries(effort)),
	};
}

/** Plain inputs for one effort row's live cell, read from the engine getters by the caller. */
export interface EffortLiveInput {
	/** Engine sample at the current playhead for this effort, or null when unavailable. */
	data: ReplayPointSample | null;
	/** True when this effort's marker has reached its last point. */
	finished: boolean;
	/** This effort is the ranked leader (index 0) — no gap is shown. */
	isLeader: boolean;
	/** The leader's current time (seconds), for the gap-vs-leader calc. */
	leaderTime: number;
	/** The fixed per-segment speed→width scale, or null before it is built. */
	speedScale: SpeedScale | null;
	/** This row's own label latch — one per effort, not shared. */
	label: SpeedLabel;
	/** This effort's distance samples; null falls the row back to the stored speed channel. */
	series: DistanceSeries | null;
	/** Playback multiplier — sets how many ride-seconds one reading covers. */
	playbackRate: number;
}

/** Display-ready values for one effort row; the caller writes these straight into the DOM. */
export interface EffortLiveRow {
	/** Measured live speed in km/h (0 when finished or unavailable). */
	speed: number;
	/** Speed label text, e.g. "25 km/h" or "-- km/h". Whole km/h — see core/speed-label. */
	speedText: string;
	/** Gap-vs-leader label, e.g. "+0:12"; "" for the leader or a negative gap. */
	gapText: string;
	/** Speed-bar fill percentage in [0, 100]. */
	barWidthPct: number;
}

/**
 * Compute the live speed label, gap-to-leader label and speed-bar width for one
 * effort row. Pure: same thresholds, comparison operators and short-circuit
 * order as the per-frame loop it was lifted from.
 */
export function computeEffortLiveRow(input: EffortLiveInput): EffortLiveRow {
	const { data, finished, isLeader, leaderTime, speedScale, label, series, playbackRate } = input;

	// A finished effort (marker reached the last point) is treated as speed 0 —
	// its last sampled speed is usually non-zero, which would otherwise leave
	// the bar/label stuck mid-replay.
	const rawSpeed = !finished && data && data.speed > 0 ? data.speed : 0;
	const elapsedTime = data ? data.time : 0;

	// Time gap vs leader (effort 0 is ranked fastest)
	let gapText: string;
	if (isLeader) {
		gapText = "";
	} else {
		const gap = elapsedTime - leaderTime;
		gapText = gap >= 0 ? "+" + formatTime(gap) : "";
	}

	// Finished or no sample: read "--" at once. The bar keeps its stopped contract — fill(0), no floor.
	// Nothing to reset: the measurement is a function of the playhead, so a re-entering rider cannot
	// carry a stale window, and the label latch holding its last number is correct if he resumes at
	// the same speed and clears the deadband on its own if he does not.
	if (rawSpeed <= 0) {
		const stoppedPct = speedScale && !speedScale.isEmpty ? speedScale.fill(0) : 0;
		return { speed: 0, speedText: "-- km/h", gapText, barWidthPct: stoppedPct };
	}

	// `elapsedTime` is this effort's OWN elapsed time in both sync modes (see getDataAtTime), which is
	// the axis its points are on. Falling back to the stored channel covers efforts whose points carry
	// no usable distance — that channel is the server's own 5 s distance mean, so it is a fair answer.
	const windowSeconds = windowSecondsFor(playbackRate);
	const valueKmh = (series && centredMeanKmh(series, elapsedTime, windowSeconds)) ?? rawSpeed;
	const speedText = label.read(valueKmh) + " km/h";

	// Speed bar — fixed per-segment time-weighted CDF scale (see speed-scale.ts).
	// Same speed → same width across all efforts and frames. Fed the unrounded measurement, not the
	// latched number, so it sweeps instead of stepping. A small floor keeps the slowest moment
	// visible rather than reading as an empty bar.
	let barWidthPct = 0;
	if (speedScale && !speedScale.isEmpty) {
		barWidthPct = speedScale.fill(valueKmh);
		if (barWidthPct < 2) barWidthPct = 2;
	}

	return { speed: valueKmh, speedText, gapText, barWidthPct };
}
