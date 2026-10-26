/**
 * The field's speed as a function of distance — median and a p10–p90 spread band across every rider at
 * each point of the course, in km/h. This is the aggregate the speed strip draws beneath the elevation
 * profile: where the whole field rides fast, and where the course slows it down.
 *
 * Pure (no DOM, no map) but it WALKS the field: `computeFieldSpeedProfile` calls `field.advanceTo`, so the
 * caller MUST re-render (advance the field back to the live clock) afterwards to restore its cursor.
 *
 * Sampled over time rather than solved analytically, so the one code path serves both rider kinds: at
 * uniform time steps we read every rider's chainage and how far he moved since the previous step. Each
 * rider files ONE speed per distance bin — the moment he first enters it — so the aggregate is the typical
 * rider's speed through a stretch, not time-weighted toward whoever dawdled there longest. A finished rider
 * is skipped so the finish bin is not flooded with the zeros of a field standing on the line.
 */

import type { RaceField } from './race-field';

export interface FieldSpeedBin {
	/** Bin centre, metres along the course. */
	readonly distanceMeters: number;
	readonly p10: number;
	readonly median: number;
	readonly p90: number;
	/** How many (rider, step) samples landed here — a thin bin is a lightly-travelled stretch. */
	readonly count: number;
}

export interface FieldSpeedProfile {
	readonly bins: readonly FieldSpeedBin[];
	readonly totalMeters: number;
	/** The tallest p90 — the y-axis top the renderer scales to. */
	readonly maxSpeedKmh: number;
}

export interface FieldSpeedOptions {
	/** Distance bins across the course. Default 200. */
	readonly bins?: number;
	/** Time samples across the race. Default 400. */
	readonly timeSteps?: number;
}

export function computeFieldSpeedProfile(
	field: RaceField,
	totalMeters: number,
	options: FieldSpeedOptions = {},
): FieldSpeedProfile {
	const binCount = Math.max(1, Math.floor(options.bins ?? 200));
	const steps = Math.max(2, Math.floor(options.timeSteps ?? 400));
	const endSeconds = field.endSeconds;
	const n = field.size;

	if (endSeconds <= 0 || n === 0 || totalMeters <= 0) {
		return { bins: [], totalMeters, maxSpeedKmh: 0 };
	}

	const buckets: number[][] = Array.from({ length: binCount }, () => []);
	const dt = endSeconds / steps;
	const prevChain = new Float32Array(n);
	const prevFinished = new Uint8Array(n);
	// The last bin each rider has already filed a speed into — so he contributes one sample per bin, not one
	// per time step (which would weight the aggregate toward whoever was slowest there).
	const lastBin = new Int32Array(n).fill(-1);

	field.advanceTo(0);
	prevChain.set(field.chainages);
	prevFinished.set(field.finished);

	for (let k = 1; k <= steps; k++) {
		field.advanceTo(k * dt);
		for (let i = 0; i < n; i++) {
			if (prevFinished[i]) continue;   // he had already stopped before this step
			// ?? NaN / ?.: dense lockstep arrays sized n / binCount — fallbacks unreachable.
			let bin = Math.floor(((prevChain[i] ?? NaN) / totalMeters) * binCount);
			if (bin < 0) bin = 0;
			else if (bin >= binCount) bin = binCount - 1;
			if (bin !== lastBin[i]) {
				const covered = (field.chainages[i] ?? NaN) - (prevChain[i] ?? NaN);
				// A real rider's projected chainage can jitter backwards on a detour — not negative speed.
				buckets[bin]?.push(covered > 0 ? (covered / dt) * 3.6 : 0);
				lastBin[i] = bin;
			}
		}
		prevChain.set(field.chainages);
		prevFinished.set(field.finished);
	}

	const bins: FieldSpeedBin[] = [];
	let maxSpeedKmh = 0;
	const binWidth = totalMeters / binCount;
	for (let b = 0; b < binCount; b++) {
		const speeds = buckets[b];
		if (!speeds || speeds.length === 0) continue;
		speeds.sort((a, z) => a - z);
		const p90 = quantile(speeds, 0.9);
		if (p90 > maxSpeedKmh) maxSpeedKmh = p90;
		bins.push({
			distanceMeters: (b + 0.5) * binWidth,
			p10: quantile(speeds, 0.1),
			median: quantile(speeds, 0.5),
			p90,
			count: speeds.length,
		});
	}

	return { bins, totalMeters, maxSpeedKmh };
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
