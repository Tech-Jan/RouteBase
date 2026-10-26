/**
 * Speed Scale — fixed per-segment speed→bar-width mapping
 *
 * Builds a time-weighted CDF (histogram-equalised) mapping from a set of speed
 * samples. The fill value for a speed is the share of weight (≈ time) spent at
 * or below that speed, so the range where most time is spent gets the most bar
 * width, and speeds at/above the top of the distribution clamp to 100%.
 *
 * Pure and DOM-free. Callers supply `{ speed, weight }` samples so this module
 * stays agnostic about where the data comes from (segment efforts vs. activity
 * GPS track).
 */
import type { GpsPoint } from './core-types';
import { must } from './invariant';

export interface SpeedSample {
	/** Speed in km/h. */
	speed: number;
	/** Relative importance of this sample, e.g. seconds spent at this speed. */
	weight: number;
}

export interface SpeedScale {
	/** True when no usable samples were supplied; `fill` then returns 0. */
	readonly isEmpty: boolean;
	/** Map a speed (km/h) to a bar fill percentage in [0, 100]. */
	fill(speedKmh: number): number;
}

const EMPTY_SCALE: SpeedScale = {
	isEmpty: true,
	fill: () => 0,
};

/**
 * Build a fixed time-weighted CDF speed scale from the given samples.
 * Samples with non-finite or non-positive weight, or non-finite/negative speed,
 * are ignored. Returns an empty scale when nothing usable remains.
 */
export function buildSpeedScale(samples: ReadonlyArray<SpeedSample>): SpeedScale {
	// Keep only usable samples, then sort by speed ascending.
	const clean: SpeedSample[] = [];
	for (let i = 0; i < samples.length; i++) {
		const s = must(samples[i], 'i < length (loop-bounded)');
		if (!Number.isFinite(s.speed) || s.speed < 0) continue;
		if (!Number.isFinite(s.weight) || s.weight <= 0) continue;
		clean.push(s);
	}
	if (clean.length === 0) return EMPTY_SCALE;

	clean.sort((a, b) => a.speed - b.speed);

	const speeds = new Float64Array(clean.length);
	const cumWeight = new Float64Array(clean.length);
	let running = 0;
	for (let i = 0; i < clean.length; i++) {
		const c = must(clean[i], 'i < length (loop-bounded)');
		speeds[i] = c.speed;
		running += c.weight;
		cumWeight[i] = running;
	}
	const total = running;
	const lastIdx = clean.length - 1;

	return {
		isEmpty: false,
		// ?? NaN in the kernel: both arrays are dense with clean.length ≥ 1 entries and every index
		// sits in [0, lastIdx] — fallbacks unreachable, and NaN compares false exactly as undefined did.
		fill(speedKmh: number): number {
			if (!Number.isFinite(speedKmh)) return 0;
			if (speedKmh <= (speeds[0] ?? NaN)) {
				// Below the slowest sample → bottom of the scale.
				return speedKmh <= 0 ? 0 : ((cumWeight[0] ?? NaN) / total) * 100;
			}
			if (speedKmh >= (speeds[lastIdx] ?? NaN)) return 100;

			// Binary search for the rightmost index with speeds[idx] <= speedKmh.
			let lo = 0;
			let hi = lastIdx;
			while (lo < hi) {
				const mid = (lo + hi + 1) >> 1;
				if ((speeds[mid] ?? NaN) <= speedKmh) lo = mid;
				else hi = mid - 1;
			}
			return ((cumWeight[lo] ?? NaN) / total) * 100;
		},
	};
}

/**
 * Build a time-weighted speed scale directly from a GPS track. Each sample is
 * weighted by the elapsed time to the next point (timestamps are ISO strings on
 * GpsPoint, so they are diffed via Date.parse); when a timestamp is missing or
 * unparseable the sample falls back to uniform weight (≈ time-weighted at the
 * typical ~1 Hz sampling rate). Shared by the replay info bar and the
 * speed-coloured map track.
 */
export function buildGpsSpeedScale(points: ReadonlyArray<GpsPoint>): SpeedScale {
	const samples: SpeedSample[] = [];
	if (points && points.length > 1) {
		for (let i = 0; i < points.length - 1; i++) {
			const p = must(points[i], 'i < length-1 (loop-bounded)');
			const pn = must(points[i + 1], 'i+1 <= length-1 (loop-bounded)');
			const t0 = p.time ? Date.parse(p.time as string) : NaN;
			const t1 = pn.time ? Date.parse(pn.time as string) : NaN;
			const dt = t1 - t0;
			samples.push({
				speed: p.speed || 0,
				weight: Number.isFinite(dt) && dt > 0 ? dt : 1,
			});
		}
	}
	return buildSpeedScale(samples);
}
