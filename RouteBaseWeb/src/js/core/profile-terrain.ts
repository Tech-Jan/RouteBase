/**
 * Elevation → a `terrain` backdrop for `core/profile-chart`.
 *
 * Shared because two replays build the same picture from the same kind of data — the segment's reference
 * polyline and an activity's own GPS — and neither feature may import the other's modules. The output axis
 * is completion (0–100) rather than metres, because that is what both replays report their position in.
 */

import { haversineDistance } from './geo';
import { must } from './invariant';
import type { ProfileTerrainBackdrop } from './profile-chart';

/**
 * Terrain resolution, in nodes across the whole strip.
 *
 * The ceiling is what a pixel column can SHOW, not what the animation can afford: the terrain is a static
 * path painted once into an offscreen canvas and blitted every frame (`core/canvas-strip`), so nodes cost
 * a repaint — on resize — and never a frame. 2000 is ~1 per CSS pixel of the widest strip we draw; past
 * that the extra nodes land on the same column.
 *
 * The floor is what a SPARSE track needs: the dot's height is a nearest-node read (`profile-chart`'s
 * terrainAt), so a 20-point import still wants a fine grid to ride, even though it has no detail to add.
 */
const MIN_TERRAIN_SAMPLES = 240;
const MAX_TERRAIN_SAMPLES = 2000;

/**
 * The backdrop plus the map back to the source track.
 *
 * The axis is DISTANCE, but a replay engine counts in samples (and, in time mode, effectively in
 * elapsed time). Those two only coincide on a track whose points are equidistant, which no recorded
 * ride is — so a playhead placed with a sample fraction lands wherever the rider's pace happened to
 * put it, ahead of the climbs and behind the descents. `axisAt` is what closes that gap: the axis
 * value of every source point, from the same cumulative distances the resampling above used, so a
 * caller can convert its own index into this chart's axis exactly rather than approximately.
 */
export interface TrackTerrainBackdrop extends ProfileTerrainBackdrop {
	/** Axis value (0–100) at each SOURCE point. Monotonic, starts at 0, ends at 100. */
	readonly axisAt: Float64Array;
}

/**
 * A track's own elevation, resampled onto a uniform 0–100 completion axis.
 *
 * Resolution follows the SOURCE by default — one node per point, between the floor and the ceiling above.
 * A fixed count under-drew the long rides it mattered most on: uniform resampling takes the interpolated
 * value at each node, so a summit falling between two of them is simply cut, and the strip disagreed with
 * the details-page elevation chart (which is LTTB-decimated, and so keeps extremes).
 *
 * Returns null unless EVERY point carries an altitude: a terrain line with holes in it draws cliffs that
 * are not there, and a caller that falls back to something honest beats one that draws a lie.
 */
export function buildTerrainBackdrop(
	coords: readonly number[][],
	samples = Math.min(MAX_TERRAIN_SAMPLES, Math.max(MIN_TERRAIN_SAMPLES, coords.length)),
): TrackTerrainBackdrop | null {
	const n = coords.length;
	if (n < 2) return null;
	for (const c of coords) {
		if (c.length < 3 || !Number.isFinite(c[0]) || !Number.isFinite(c[1]) || !Number.isFinite(c[2])) return null;
	}

	// Cumulative ground distance — the axis the elevation is a function of.
	// ?? NaN throughout the kernel: cum is dense by construction and every coords row/cell is
	// validated finite above, so the fallbacks are unreachable (and NaN would propagate, not lie).
	const cum = new Float64Array(n);
	for (let i = 1; i < n; i++) {
		const a = must(coords[i - 1], 'i-1 >= 0 (loop-bounded)');
		const b = must(coords[i], 'i < n (loop-bounded)');
		cum[i] = (cum[i - 1] ?? NaN) + haversineDistance(a[0] ?? NaN, a[1] ?? NaN, b[0] ?? NaN, b[1] ?? NaN);
	}
	const total = cum[n - 1];
	if (total === undefined || !(total > 0)) return null;

	// Resample to a uniform grid so the chart can index by axis value instead of searching.
	const elevation = new Float64Array(samples);
	let node = 0;
	for (let s = 0; s < samples; s++) {
		const target = (s / (samples - 1)) * total;
		while (node < n - 2 && (cum[node + 1] ?? NaN) < target) node++;
		const span = (cum[node + 1] ?? NaN) - (cum[node] ?? NaN);
		const frac = span > 0 ? (target - (cum[node] ?? NaN)) / span : 0;
		const cn = must(coords[node], 'node <= n-2 (loop-bounded)');
		const cn1 = must(coords[node + 1], 'node+1 <= n-1 (loop-bounded)');
		elevation[s] = (cn[2] ?? NaN) + ((cn1[2] ?? NaN) - (cn[2] ?? NaN)) * Math.max(0, Math.min(1, frac));
	}

	// Same cum/total the resampling just used, expressed on the axis — so index→axis is exact by
	// construction rather than a second, subtly different distance pass.
	const axisAt = new Float64Array(n);
	for (let i = 0; i < n; i++) axisAt[i] = ((cum[i] ?? NaN) / total) * 100;

	return { kind: 'terrain', elevation, step: 100 / (samples - 1), axisMax: 100, axisAt };
}

/**
 * The axis value at a FRACTIONAL source index, interpolated — the conversion a replay playhead needs
 * to sit on a distance axis while the engine counts in samples. Out-of-range indices clamp to the
 * ends, so a finished replay pins at 100 rather than running off the strip.
 */
export function axisValueAtIndex(axisAt: ArrayLike<number>, fracIndex: number): number {
	const n = axisAt.length;
	if (n === 0) return 0;

	// ?? NaN: n >= 1 past the guard and i is clamped below n-1 — in-bounds by construction.
	const clamped = Math.min(n - 1, Math.max(0, fracIndex));
	const i = Math.floor(clamped);
	if (i >= n - 1) return axisAt[n - 1] ?? NaN;

	return (axisAt[i] ?? NaN) + ((axisAt[i + 1] ?? NaN) - (axisAt[i] ?? NaN)) * (clamped - i);
}
