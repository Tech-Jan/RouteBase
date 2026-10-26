/**
 * smoothing.ts — Gaussian smoothing for chart series, in two flavours.
 *
 * `gaussianSmooth` works in SAMPLES (array indices) and is what elevation still uses.
 * `gaussianSmoothTime` works in SECONDS, and is what anything sampled by a GPS device should use —
 * see below.
 *
 * ## Why a sample window is the wrong unit for GPS data
 *
 * A sample-indexed kernel assumes a fixed sample rate. GPS tracks do not have one: a 1 Hz recording
 * and a smart-recording device that files a point every few seconds get the same *index* window and
 * therefore wildly different *physical* windows — the identical setting smooths two rides
 * differently, and the number in the settings form ("window size in GPS points") means nothing to
 * the person typing it. The time-domain pair below takes the window in seconds, so it describes the
 * same amount of riding on every device.
 *
 * ## Holes stay holes
 *
 * `gaussianSmooth` reads a missing sample as `data[idx] || 0`, which makes a dropout indistinguishable
 * from a standstill and drags the local mean toward zero — worst on heart rate, where a strap dropout
 * became 0 bpm smeared across the kernel. `gaussianSmoothTime` skips non-finite samples instead
 * (renormalising over the ones that are real) and returns NaN where the sample itself is missing, which
 * Chart.js draws as a gap. Inventing a plausible number for missing data is the one thing a chart of
 * recorded data must not do.
 */

import { getAccountSettings } from './account-settings';

/**
 * Gaussian-weighted moving-average smoothing — pure, no I/O.
 *
 * Edge cases preserved: `windowSize < 3` and empty data return the input
 * array by reference; asymmetric edge renormalization (`aSum`) at array ends;
 * `data[idx] || 0` NaN/undefined tolerance.
 */
export function gaussianSmooth(data: number[], windowSize = 11): number[] {
	if (!data || data.length === 0 || windowSize < 3) return data;

	const halfWindow = Math.floor(windowSize / 2);
	const sigma = windowSize / 4.0;

	// Build normalised Gaussian weights once
	const weights: number[] = [];
	let wSum = 0;
	for (let i = -halfWindow; i <= halfWindow; i++) {
		const w = Math.exp(-(i * i) / (2 * sigma * sigma));
		weights.push(w);
		wSum += w;
	}
	const normWeights = weights.map((w) => w / wSum);

	const smoothed = new Array<number>(data.length);
	for (let i = 0; i < data.length; i++) {
		let val = 0,
			aSum = 0;
		for (let j = -halfWindow; j <= halfWindow; j++) {
			const idx = i + j;
			if (idx >= 0 && idx < data.length) {
				// ?? NaN: j+halfWindow ∈ [0, 2*halfWindow] indexes the dense kernel — unreachable,
				// and NaN would propagate exactly as undefined arithmetic did.
				const w = normWeights[j + halfWindow] ?? NaN;
				val += (data[idx] || 0) * w;
				aSum += w;
			}
		}
		smoothed[i] = aSum > 0 ? val / aSum : data[i] || 0;
	}
	return smoothed;
}

/**
 * The configured speed-smoothing window, in seconds. Zero means no smoothing, which the chart's
 * "Recorded" mode also expresses — the setting is the AMOUNT, the chart control picks the mode.
 *
 * Read at call time, never captured at module scope (see core/account-settings).
 */
export function getSpeedSmoothingSeconds(): number {
	return getAccountSettings().charts.speedSmoothingSeconds;
}

// ---------------------------------------------------------------------------
// Time domain. `seconds` is lockstep with `values` and monotonically non-decreasing; the kernel spans
// ±windowSeconds/2 around each point, so `windowSeconds` is the FULL width and sigma = window/4 puts
// the truncation at ±2σ (the same shape gaussianSmooth uses).
// ---------------------------------------------------------------------------

/**
 * Zero-phase Gaussian smoothing over a window in SECONDS.
 *
 * Zero-phase because the kernel is symmetric about the point: a chart of recorded data knows the
 * future, so there is no reason to accept the half-window lag a trailing filter would impose.
 * Returns a copy of the input when the window is non-positive ("no smoothing" is a real setting).
 */
export function gaussianSmoothTime(
	values: readonly number[],
	seconds: readonly number[],
	windowSeconds: number,
): number[] {
	const n = Math.min(values.length, seconds.length);
	if (n === 0 || !(windowSeconds > 0)) return values.slice(0, n);

	const half = windowSeconds / 2;
	const twoSigmaSq = 2 * (windowSeconds / 4) ** 2;
	const out = new Array<number>(n);

	let lo = 0, hi = 0;
	for (let i = 0; i < n; i++) {
		const t = seconds[i] ?? Number.NaN;
		const v = values[i] ?? Number.NaN;
		// The sample itself is missing: that is a gap in the data, not a value to be interpolated.
		if (!Number.isFinite(t) || !Number.isFinite(v)) { out[i] = Number.NaN; continue; }

		while (lo < i && (seconds[lo] ?? Number.NaN) < t - half) lo++;
		if (hi < i) hi = i;
		while (hi + 1 < n && (seconds[hi + 1] ?? Number.NaN) <= t + half) hi++;

		let sum = 0, wSum = 0;
		for (let j = lo; j <= hi; j++) {
			const tj = seconds[j] ?? Number.NaN;
			const vj = values[j] ?? Number.NaN;
			if (!Number.isFinite(tj) || !Number.isFinite(vj)) continue; // a neighbour's hole must not vote
			const dt = tj - t;
			const w = Math.exp(-(dt * dt) / twoSigmaSq);
			sum += vj * w;
			wSum += w;
		}
		out[i] = wSum > 0 ? sum / wSum : v;
	}
	return out;
}

