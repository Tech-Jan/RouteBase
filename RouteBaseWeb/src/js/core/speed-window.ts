/**
 * Speed Window — the live "km/h" a replay shows, derived from the DISTANCE channel.
 *
 * ## Why not just read the speed channel
 *
 * The stored speed is already `(distance[i] - distance[j]) / (time[i] - time[j])` over a trailing
 * 5 s window — Strava's velocity_smooth, applied at ingest by `Services/GeoUtils.SmoothSpeeds` on
 * every path (GPX/FIT/TCX and recordings). Averaging *that* again, as the old speed-readout did,
 * stacked a second 5 s boxcar on the first: ~10 s of effective kernel and ~5 s of group delay, which
 * is what made the label feel like it was reporting the previous corner. Differencing the distance
 * channel ourselves replaces that pass instead of adding to it.
 *
 * ## Why the window is CENTRED
 *
 * This is a replay of recorded data, so the future is known. A trailing window is a first-order
 * estimator of the speed at `t` and is systematically `W/2` late; a centred window is second-order
 * and has ZERO phase error, so the number agrees with what the marker is visibly doing. The price is
 * that it anticipates by `W/2` — the number starts falling just before a visible brake. Being early
 * by half a window beats being late by half a window for a label captioned "now".
 *
 * ## Why the width follows the playback rate
 *
 * The readability clock is WALL time, the truth clock is RIDE time, and the playback multiplier is
 * the exchange rate between them. A fixed 5 ride-second window is 5 wall-seconds at 1x but 7 ms at
 * 750x (the ladder tops out at `base * 10`, see core/speed-ladder.ts) — at which point a "5-second
 * average" is a single sample and the label is noise. `windowSecondsFor` keeps the reading worth
 * roughly a constant amount of what the viewer just watched.
 *
 * Pure and stateless: the value is a function of the playhead alone, so it survives seeks, scrubs,
 * frame-rate changes and virtual-scroll gaps without anything to reset or corrupt.
 */

/**
 * Monotone ride-time → cumulative-distance samples, read through accessors so no caller has to copy
 * its points into parallel arrays. SECONDS and KILOMETRES — effort points are in metres, so their
 * adapter divides; activity points are already km (see core/map-utils-shared normalizeTrackDistances).
 */
export interface DistanceSeries {
	readonly length: number;
	secondsAt(i: number): number;
	kmAt(i: number): number;
}

/** Wall-seconds of screen time one reading should summarise. */
const WINDOW_WALL_SECONDS = 2;
/** Never average less than the server's own velocity_smooth window — below it the label and the
 *  effort chart's speed channel would describe different quantities at 1x. Also the noise floor:
 *  0.1 m distance quantization plus GPS error is ~0.4 km/h over 5 s, under the label's deadband. */
const MIN_WINDOW_SECONDS = 5;
/** Past a minute this stops being "how fast is he going" and becomes a lap average. */
const MAX_WINDOW_SECONDS = 60;

/** Ride-seconds to average over at the given playback multiplier. */
export function windowSecondsFor(playbackRate: number): number {
	if (!Number.isFinite(playbackRate) || playbackRate <= 0) return MIN_WINDOW_SECONDS;
	const wanted = playbackRate * WINDOW_WALL_SECONDS;
	return Math.min(MAX_WINDOW_SECONDS, Math.max(MIN_WINDOW_SECONDS, wanted));
}

/**
 * Mean speed in km/h over the window CENTRED on `atSeconds`.
 *
 * Returns null when the series cannot answer (fewer than two points, no time span, non-finite data)
 * so the caller can fall back to the stored speed channel rather than show a NaN.
 *
 * At the ends the window is truncated, not slid, and the divisor is the span that actually exists —
 * so the start of an effort reads the mean over what has happened rather than the same distance
 * divided by a full window, and a constant-speed series still reads that constant everywhere.
 */
export function centredMeanKmh(
	series: DistanceSeries,
	atSeconds: number,
	windowSeconds: number,
): number | null {
	const n = series.length;
	if (n < 2 || !Number.isFinite(atSeconds) || !Number.isFinite(windowSeconds) || windowSeconds <= 0) {
		return null;
	}

	const first = series.secondsAt(0);
	const last = series.secondsAt(n - 1);
	if (!Number.isFinite(first) || !Number.isFinite(last) || last <= first) return null;

	// A playhead past either end reads that end — the row is finished, or has not started.
	const at = Math.min(last, Math.max(first, atSeconds));
	const half = windowSeconds / 2;
	const from = Math.max(first, at - half);
	const to = Math.min(last, at + half);
	const span = to - from;
	if (span <= 0) return null;

	const travelled = kmAtSeconds(series, to) - kmAtSeconds(series, from);
	if (!Number.isFinite(travelled)) return null;
	// A backwards step means the distance channel is not monotonic (mixed cumulative/haversine
	// sources), not that the rider reversed — read it as a standstill rather than a negative speed.
	return travelled > 0 ? (travelled / span) * 3600 : 0;
}

/** Cumulative km at a ride time, linearly interpolated between the bracketing samples. */
function kmAtSeconds(series: DistanceSeries, t: number): number {
	const n = series.length;

	// Largest index whose time is <= t. Plain binary search, no cursor: a 500-point effort is 9
	// steps, and a cursor would reintroduce exactly the cross-call state this module exists without.
	let lo = 0;
	let hi = n - 1;
	while (lo < hi) {
		const mid = (lo + hi + 1) >> 1;
		if (series.secondsAt(mid) <= t) lo = mid;
		else hi = mid - 1;
	}
	if (lo >= n - 1) return series.kmAt(n - 1);

	const t0 = series.secondsAt(lo);
	const t1 = series.secondsAt(lo + 1);
	const k0 = series.kmAt(lo);
	const dt = t1 - t0;
	if (!(dt > 0)) return k0; // duplicate timestamps — take the earlier sample rather than divide by 0
	const frac = Math.min(1, Math.max(0, (t - t0) / dt));
	return k0 + (series.kmAt(lo + 1) - k0) * frac;
}
