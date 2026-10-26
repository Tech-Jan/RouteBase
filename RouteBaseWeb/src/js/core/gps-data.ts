/**
 * gps-data.ts — Single source of truth for the current GPS data array.
 *
 * Extracted from the old utils god-file (CB-01 dissolution); ID-08 folded the former
 * window.gpsData / window.gpsDataGlobal aliases into this module's state.
 */
import type { GpsPoint, GpsTrackWire } from './core-types';
import { must } from './invariant';

let _gpsData: GpsPoint[] = [];

/** Set the current GPS data (Details page bootstrap). */
export function setGpsData(points: GpsPoint[] | null | undefined): void {
	_gpsData = points ?? [];
}

/** Return the current GPS data array (never null). */
export function getGpsData(): GpsPoint[] {
	return _gpsData;
}

/**
 * The elapsed-seconds axis for a track, or null when it has no usable one.
 *
 * Null is a real answer, not a failure: a route with no timestamps (a drawn GPX, some imports) has no
 * time axis, and callers fall back to a sample-indexed window rather than invent one. Also null when
 * time runs backwards, which is not a time axis either and would break a forward window walk.
 */
export function trackSeconds(points: readonly GpsPoint[]): number[] | null {
	if (points.length < 2) return null;

	const out = new Array<number>(points.length);
	let usable = 0;
	for (let i = 0; i < points.length; i++) {
		const s = points[i]?._cumulativeTime;
		const v = typeof s === 'number' && Number.isFinite(s) ? s : Number.NaN;
		out[i] = v;
		if (!Number.isNaN(v)) usable++;
	}
	if (usable < 2) return null;

	let prev = Number.NaN;
	for (let i = 0; i < out.length; i++) {
		const v = out[i] ?? Number.NaN;
		if (Number.isNaN(v)) continue;
		if (!Number.isNaN(prev) && v < prev) return null;
		prev = v;
	}
	return out;
}

function formatLocalTime(epochSec: number | null, tzOffsetHours: number): string {
	if (epochSec == null) return '';
	const d = new Date((epochSec + tzOffsetHours * 3600) * 1000);
	const pad = (n: number) => String(n).padStart(2, '0');
	return `${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}:${pad(d.getUTCSeconds())}`;
}

/**
 * Reconstruct the per-point GpsPoint[] shape from the Details page's columnar wire format.
 * `tzOffsetHours` matches the activity's DateTimeTimezone, used to render `time` the same way
 * the server used to (local HH:mm:ss).
 */
export function expandGpsTrack(wire: GpsTrackWire, tzOffsetHours: number): GpsPoint[] {
	const n = wire.lat.length;
	const points: GpsPoint[] = new Array(n);
	// Elapsed seconds, kept as a NUMBER alongside the display string. The wire has always carried the
	// epoch per point; rendering it straight to "HH:mm:ss" threw the only time axis the page has away,
	// which is why chart smoothing had to count in samples. Absent where the track has no timestamps —
	// consumers fall back rather than invent one.
	const t0 = wire.t.find((v) => v != null) ?? null;
	for (let i = 0; i < n; i++) {
		// Optional channels are omitted by the server rather than sent as a column of zeros (gpsId where
		// the segment panel is off; heartRate/cadence where the ride recorded neither). `?? 0` restores
		// the shape every consumer already expects, so nothing downstream has to know they can be absent.
		points[i] = {
			gpsId: wire.gpsId?.[i],
			// must(): the wire columns are lockstep by contract (all length n) — a short column used
			// to leak undefined into a number slot; now it throws with the column named.
			lat: must(wire.lat[i], 'wire lat column lockstep'),
			lng: must(wire.lng[i], 'wire lng column lockstep'),
			altitude: must(wire.altitude[i], 'wire altitude column lockstep'),
			speed: must(wire.speed[i], 'wire speed column lockstep'),
			distance: must(wire.distance[i], 'wire distance column lockstep'),
			// t[i] may be null by contract (renders as ''); ?? null folds a hole into that same path,
			// which is what formatLocalTime did with undefined at runtime anyway.
			time: formatLocalTime(wire.t[i] ?? null, tzOffsetHours),
			_cumulativeTime: t0 != null && wire.t[i] != null ? (wire.t[i] as number) - t0 : undefined,
			heartRate: wire.heartRate?.[i] ?? 0,
			cadence: wire.cadence?.[i] ?? 0,
		};
	}
	return points;
}
