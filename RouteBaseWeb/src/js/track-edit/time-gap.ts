/**
 * time-gap.ts — the §2.2 gapSeconds formulas, defined ONCE (invariant 9). The UI calls these when it
 * records a `close`/`synthesize` op so the op carries an integer `gapSeconds`; the server replay then
 * applies it as pure integer arithmetic (never re-deriving geometry — that keeps TS↔C# replay bit-exact,
 * see A1 Decision D3). These functions are client-only and NOT parity-bound, so haversine/trig is fine.
 *
 * All inputs are dequantized values: time = absolute epoch seconds, distance = metres, lat/lng = degrees,
 * a missing sample = NaN. All results round away-from-zero to a non-negative integer.
 */

import { type ChannelMap, roundAwayFromZero, firstPresent, lastPresent } from './apply-script';
import { haversineDistance } from '../core/geo';

/** deleteRange close: the spacing the first deleted point had; head delete anchors to the original start. */
export function deleteRangeCloseGap(time: Float64Array, from: number): number {
	if (from === 0) return 0;
	// ?? NaN: an out-of-bounds typed-array read is undefined; NaN keeps the isFinite fallback honest.
	const gap = (time[from] ?? NaN) - (time[from - 1] ?? NaN);
	return Number.isFinite(gap) ? roundAwayFromZero(gap) : 0;
}

/** deleteRange synthesize: junction distance / average speed; falls back to close when it can't be computed. */
export function deleteRangeSynthesizeGap(
	time: Float64Array, distance: Float64Array | undefined, from: number, to: number,
): number {
	if (from === 0 || !distance) return deleteRangeCloseGap(time, from);
	const junctionMeters = (distance[to + 1] ?? NaN) - (distance[from - 1] ?? NaN);
	const speed = avgSpeed(time, distance);
	if (!Number.isFinite(junctionMeters) || !(speed > 0)) return deleteRangeCloseGap(time, from);
	return roundAwayFromZero(junctionMeters / speed);
}

/** append close: the target's last inter-point time delta if finite and > 0, else 1 second. */
export function appendCloseGap(targetTime: Float64Array): number {
	let last: number | null = null;
	let prev: number | null = null;
	for (let j = targetTime.length - 1; j >= 0; j--) {
		const v = targetTime[j] ?? NaN;
		if (Number.isNaN(v)) continue;
		if (last === null) { last = v; continue; }
		prev = v;
		break;
	}
	if (last === null || prev === null) return 1;
	const delta = last - prev;
	return Number.isFinite(delta) && delta > 0 ? roundAwayFromZero(delta) : 1;
}

/** append synthesize: straight-line junction distance / target average speed; falls back to append close. */
export function appendSynthesizeGap(target: ChannelMap, source: ChannelMap): number {
	const tt = target.time;
	if (!tt) return 1;
	const td = target.distance;
	const tlat = target.lat;
	const tlng = target.lng;
	const slat = source.lat;
	const slng = source.lng;
	if (!td || !tlat || !tlng || !slat || !slng) return appendCloseGap(tt);
	const endLat = lastPresent(tlat);
	const endLng = lastPresent(tlng);
	const startLat = firstPresent(slat);
	const startLng = firstPresent(slng);
	if (endLat === null || endLng === null || startLat === null || startLng === null) return appendCloseGap(tt);
	const speed = avgSpeed(tt, td);
	if (!(speed > 0)) return appendCloseGap(tt);
	// core/geo haversine (atan2 form) — numerically equivalent to the old local asin form at these distances,
	// and this output is rounded to whole seconds and NOT parity-bound (R-12).
	return roundAwayFromZero(haversineDistance(endLat, endLng, startLat, startLng) / speed);
}

// Average speed over present samples: (distLast − distFirst) / (tLast − tFirst).
function avgSpeed(time: Float64Array, distance: Float64Array): number {
	const tFirst = firstPresent(time);
	const tLast = lastPresent(time);
	const dFirst = firstPresent(distance);
	const dLast = lastPresent(distance);
	if (tFirst === null || tLast === null || dFirst === null || dLast === null) return NaN;
	const dt = tLast - tFirst;
	if (!(dt > 0)) return NaN;
	return (dLast - dFirst) / dt;
}
