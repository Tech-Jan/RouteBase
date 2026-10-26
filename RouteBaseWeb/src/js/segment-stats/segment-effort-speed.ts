/**
 * The loaded efforts' AGGREGATE speed as a function of position on the segment — a median inside a
 * p10–p90 spread, in km/h. This is what the replay strip traces over the terrain: where everyone is
 * fast, and where the segment slows the whole set down.
 *
 * Only meaningful under a SHARED position axis. A shapes segment has no reference polyline, so "50%" is
 * 50% of a different ride per effort and binning them together would aggregate places that are not the
 * same place — the caller gates on that (see segment-replay-profile's progress/time modes).
 *
 * Pure: plain arrays in, plain bins out. No DOM, no replay engine, no chart.
 *
 * ## One sample per effort per bin, and why
 *
 * Effort points arrive at a roughly fixed CADENCE, so a slow effort files more points through a stretch
 * than a fast one and a naive pooled quantile drifts toward whoever was slowest there — the aggregate
 * would read slower than any rider actually rode. Each effort therefore contributes exactly one value
 * per bin: its own mean speed through that stretch. The quantiles are then across EFFORTS, which is what
 * "the p10 effort through this corner" is supposed to mean.
 */

import type { EffortDataPoint } from './segment-stats-effort-types';
import { must } from '../core/invariant';

/** Bins across the segment. 100 is ~1 per 8 px of the widest strip we draw. */
const DEFAULT_BINS = 100;

/** One effort, as the aggregate needs it: its points and where each one sits on the segment. */
export interface EffortSpeedSource {
	readonly points: readonly EffortDataPoint[];
	/** Completion percentage (0–100) per point, lockstep with `points` — ReplayEffort.progress. */
	readonly progress: readonly number[];
}

export interface EffortSpeedBin {
	/** Bin centre, as completion percentage (0–100) — the strip's own x-axis. */
	readonly progressPercent: number;
	readonly p10: number;
	readonly median: number;
	readonly p90: number;
	/** How many EFFORTS filed a speed here. 1 means the spread is a single effort, not a field. */
	readonly effortCount: number;
}

export interface EffortSpeedProfile {
	readonly bins: readonly EffortSpeedBin[];
	/** The tallest p90 — what a renderer scales its y-axis to. */
	readonly maxSpeedKmh: number;
}

export interface EffortSpeedOptions {
	readonly bins?: number;
}

export function computeEffortSpeedProfile(
	efforts: readonly EffortSpeedSource[],
	options: EffortSpeedOptions = {},
): EffortSpeedProfile {
	const binCount = Math.max(1, Math.floor(options.bins ?? DEFAULT_BINS));
	const buckets: number[][] = Array.from({ length: binCount }, () => []);

	for (const effort of efforts) {
		// Per-effort running mean, so this effort files ONE value per bin however many points it left there.
		const sum = new Float64Array(binCount);
		const count = new Int32Array(binCount);

		const n = Math.min(effort.points.length, effort.progress.length);
		for (let i = 0; i < n; i++) {
			const speed = must(effort.points[i], 'i < n (loop-bounded)').speed;
			const at = effort.progress[i];
			if (at === undefined || !Number.isFinite(at) || !Number.isFinite(speed) || speed <= 0) continue;

			let bin = Math.floor((at / 100) * binCount);
			if (bin < 0) bin = 0;
			else if (bin >= binCount) bin = binCount - 1;
			sum[bin] = (sum[bin] ?? 0) + speed;
			count[bin] = (count[bin] ?? 0) + 1;
		}

		for (let b = 0; b < binCount; b++) {
			const c = count[b] ?? 0;
			if (c > 0) buckets[b]?.push((sum[b] ?? 0) / c);
		}
	}

	const bins: EffortSpeedBin[] = [];
	let maxSpeedKmh = 0;
	const binWidth = 100 / binCount;
	for (let b = 0; b < binCount; b++) {
		const speeds = buckets[b];
		if (!speeds || speeds.length === 0) continue;
		speeds.sort((a, z) => a - z);
		const p90 = quantile(speeds, 0.9);
		if (p90 > maxSpeedKmh) maxSpeedKmh = p90;
		bins.push({
			progressPercent: (b + 0.5) * binWidth,
			p10: quantile(speeds, 0.1),
			median: quantile(speeds, 0.5),
			p90,
			effortCount: speeds.length,
		});
	}

	return { bins, maxSpeedKmh };
}

/** Linear-interpolated quantile of a pre-sorted ascending array. */
function quantile(sorted: readonly number[], q: number): number {
	if (sorted.length === 0) return 0;
	if (sorted.length === 1) return sorted[0] ?? 0;
	const pos = (sorted.length - 1) * q;
	const lo = Math.floor(pos);
	const hi = Math.ceil(pos);
	// ?? 0: lo/hi lie in [0, len-1] by construction — fallbacks unreachable.
	return (sorted[lo] ?? 0) + ((sorted[hi] ?? 0) - (sorted[lo] ?? 0)) * (pos - lo);
}
