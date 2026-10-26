/**
 * The field — every participant's position at a moment of the race.
 *
 * Pure: no DOM, no map, no fetch. The marker layer reads the flat output arrays and does nothing else.
 *
 * ONE interface, two sources, and the difference is the whole design:
 *
 *   THIN (simulated, hundreds of them) — shares the course grid and owns only a Float32Array of arrival
 *   times. Position is the COURSE interpolated at the modelled chainage. ~16 KB per rider.
 *
 *   RICH (real: you, a friend, single digits of them) — owns its own GPS. Position is ITS OWN track, so a
 *   wider line, a wrong turn or a detour is VISIBLE. Chainage is carried alongside (derived server-side by
 *   SegmentChainage) and used only for ranking and gaps — it never moves the marker. Projecting a real
 *   rider onto the course would erase the very thing he was added to show.
 *
 * Real riders are few and rich; simulated are many and thin. That is what makes 500 markers affordable:
 * the expensive representation is only ever used by the cheap-in-count participants.
 *
 * Lookup is a CURSOR, not a binary search. The playhead almost always moves forward by one frame, so each
 * participant resumes from where he was: O(1) amortised per rider per frame instead of O(log n). A seek
 * backwards rewinds the cursor (`reset`).
 */

import type { CourseProfile } from './pacing-model';

/**
 * The full shared course — the pacing model's profile plus the geometry to draw it.
 * Mirrors the C# `RaceCourseGrid` DTO; the two change together.
 */
export interface CourseGrid extends CourseProfile {
	readonly lat: Float64Array | number[];
	readonly lng: Float64Array | number[];
	readonly elevation: Float64Array | number[];
}

/** Where a participant is, and how far along. */
export interface Sample {
	lat: number;
	lng: number;
	/** Metres along the course — for ranking, gaps and checkpoint crossings. Never for drawing. */
	chainage: number;
	/** True once past his final time (he has crossed the line, or abandoned). */
	finished: boolean;
}

export interface ParticipantSource {
	/** Position + chainage at elapsed time `t`, written into `out` (no allocation per frame). */
	sampleAt(t: number, out: Sample): void;
	/** Rewind the cursor. Called on seek-backwards. */
	reset(): void;
	/** Elapsed seconds at which this participant stops moving. */
	readonly endSeconds: number;
}

/**
 * The one cursor walk both participant kinds do.
 *
 * A rider is a monotone key array (`arrivals` for a simulated rider, his GPS `time` for a real one) plus
 * positions to interpolate between. The bracket-find, the clamps at either end, the stopped-clock rule
 * and the lat/lng lerp are identical; only where CHAINAGE comes from differs, so that is the parameter.
 *
 * `chainageAt(i, f)` is the chainage at fraction `f` between node `i` and `i + 1`, and is called with
 * `f === 0` for the two end clamps.
 */
function createCursorSampler(
	key: ArrayLike<number>,
	lat: ArrayLike<number>,
	lng: ArrayLike<number>,
	chainageAt: (index: number, f: number) => number,
	/** How much of `key` is this rider's. Shorter than the array for a rider who stopped early. */
	count: number = key.length,
	/**
	 * Where his ride STARTS in `key`. Non-zero for a simulated rider whose race begins after the grid's
	 * first node — the start line may sit well into the recorded ride. Everything below it is somebody
	 * else's roll-out, and the pacing model leaves those slots zero-filled, which the end clamps would
	 * otherwise read as "he was already there at t=0".
	 */
	first = 0,
): ParticipantSource {
	const n = count;
	let cursor = first;

	return {
		// ?? NaN throughout sampleAt: the cursor walk keeps indices in [first, n-1] — fallbacks unreachable,
		// and NaN comparisons behave exactly as the undefined ones did (both false).
		endSeconds: key[n - 1] ?? NaN,
		reset() {
			cursor = first;
		},
		sampleAt(t, out) {
			if (t <= (key[first] ?? NaN)) {
				cursor = first;
				out.lat = lat[first] ?? NaN;
				out.lng = lng[first] ?? NaN;
				out.chainage = chainageAt(first, 0);
				out.finished = false;
				return;
			}
			if (t >= (key[n - 1] ?? NaN)) {
				cursor = n - 1;
				out.lat = lat[n - 1] ?? NaN;
				out.lng = lng[n - 1] ?? NaN;
				out.chainage = chainageAt(n - 1, 0);
				out.finished = true;
				return;
			}
			// Cursor walk. A seek backwards is the only case that rewinds.
			if ((key[cursor] ?? NaN) > t) cursor = first;
			while (cursor < n - 2 && (key[cursor + 1] ?? NaN) <= t) cursor++;

			const t0 = key[cursor] ?? NaN;
			const t1 = key[cursor + 1] ?? NaN;
			const span = t1 - t0;
			// Equal keys mean a stationary stretch (a stopped clock) — hold, don't divide by zero.
			const f = span <= 0 ? 0 : (t - t0) / span;

			out.lat = (lat[cursor] ?? NaN) + f * ((lat[cursor + 1] ?? NaN) - (lat[cursor] ?? NaN));
			out.lng = (lng[cursor] ?? NaN) + f * ((lng[cursor + 1] ?? NaN) - (lng[cursor] ?? NaN));
			out.chainage = chainageAt(cursor, f);
			out.finished = false;
		},
	};
}

/**
 * A simulated rider: the shared course grid plus his own arrival times (from the pacing model).
 * `arrivals` must be non-decreasing and the same length as the grid.
 *
 * His chainage is the GRID's — he is on the course by construction, so it is free.
 *
 * `lastNode` is where HIS race ended, which is not the end of the COURSE for a rider who abandoned or whose
 * results file simply stops (87 Ski to Sea teams have no finish time). The pacing model holds his arrivals
 * flat past his last anchor, so without this the sampler's end-clamp would find the whole tail "reached" and
 * park him on the FINISH LINE — a rider who never got there, shown crossing it.
 *
 * `firstNode` is the mirror image: where the START LINE is on the grid. The grid is the whole recorded ride,
 * so a race may begin after a roll-out — and `simulateArrivals` leaves every node below the first anchor at
 * zero, which reads as "arrived at t=0". Without this the whole field would spawn at the ride's first GPS
 * point and be teleported to the line on the next frame.
 */
export function createThinSource(
	grid: CourseGrid, arrivals: Float32Array,
	lastNode: number = arrivals.length - 1, firstNode = 0,
): ParticipantSource {
	const { lat, lng } = gridPositions(grid);
	return createCursorSampler(
		arrivals, lat, lng,
		(i, f) => (grid.distance[i] ?? NaN) + f * grid.stepMeters,
		lastNode + 1,
		firstNode,
	);
}

/**
 * A real rider: his OWN GPS. `time` is elapsed seconds (non-decreasing); `chainage` is his projected
 * distance along the course, carried only so he can be ranked against the field.
 *
 * His chainage is a channel of its own: he may have taken a detour, so it does NOT follow from position.
 */
export function createRichSource(
	lat: Float64Array | number[],
	lng: Float64Array | number[],
	time: Float32Array | number[],
	chainage: Float32Array | number[],
): ParticipantSource {
	const n = time.length;
	if (n < 2) throw new Error('createRichSource: a real participant needs at least 2 points');

	return createCursorSampler(
		time, lat, lng,
		(i, f) => (i >= n - 1 ? chainage[n - 1] ?? NaN : (chainage[i] ?? NaN) + f * ((chainage[i + 1] ?? NaN) - (chainage[i] ?? NaN))),
	);
}

/** Cache the grid's positions as plain arrays once — every thin rider reads the same two. */
const gridCache = new WeakMap<CourseGrid, { lat: ArrayLike<number>; lng: ArrayLike<number> }>();
function gridPositions(grid: CourseGrid): { lat: ArrayLike<number>; lng: ArrayLike<number> } {
	let cached = gridCache.get(grid);
	if (!cached) {
		cached = { lat: grid.lat, lng: grid.lng };
		gridCache.set(grid, cached);
	}
	return cached;
}

/**
 * The whole field at one instant. Output lives in flat, reused arrays so a frame costs no allocation —
 * the marker layer writes `lats[i]`/`lngs[i]` straight into its GeoJSON coordinates.
 */
export class RaceField {
	readonly lats: Float64Array;
	readonly lngs: Float64Array;
	readonly chainages: Float32Array;
	readonly finished: Uint8Array;

	/**
	 * Each rider's own end time — the elapsed second at which he stops moving, on whatever basis the field
	 * was built with (gun or net), so the values are comparable across riders.
	 *
	 * Static for the whole replay, unlike the arrays above. Exposed because it is the only thing that can
	 * order a field that has already finished: once everyone is parked on the line, chainage says nothing.
	 */
	readonly ends: Float64Array;

	private readonly sources: ParticipantSource[];
	private readonly scratch: Sample = { lat: 0, lng: 0, chainage: 0, finished: false };
	private lastTime = -1;

	constructor(sources: ParticipantSource[]) {
		this.sources = sources;
		this.lats = new Float64Array(sources.length);
		this.lngs = new Float64Array(sources.length);
		this.chainages = new Float32Array(sources.length);
		this.finished = new Uint8Array(sources.length);
		this.ends = Float64Array.from(sources, (s) => s.endSeconds);
	}

	get size(): number {
		return this.sources.length;
	}

	/** The moment the last participant stops moving. */
	get endSeconds(): number {
		let max = 0;
		for (const s of this.sources) if (s.endSeconds > max) max = s.endSeconds;
		return max;
	}

	/** Advance (or seek) the whole field to elapsed time `t`. */
	advanceTo(t: number): void {
		// Only a backwards jump costs anything: rewind every cursor, then walk forward as usual.
		if (t < this.lastTime) for (const s of this.sources) s.reset();
		this.lastTime = t;

		const out = this.scratch;
		for (let i = 0; i < this.sources.length; i++) {
			this.sources[i]?.sampleAt(t, out);
			this.lats[i] = out.lat;
			this.lngs[i] = out.lng;
			this.chainages[i] = out.chainage;
			this.finished[i] = out.finished ? 1 : 0;
		}
	}

	/**
	 * Participant indices ordered by race position (furthest along first) — the leaderboard.
	 * Allocates, so call it on a UI tick, not on every frame.
	 */
	order(): number[] {
		const idx = Array.from({ length: this.sources.length }, (_, i) => i);
		idx.sort((a, b) => (this.chainages[b] ?? NaN) - (this.chainages[a] ?? NaN));
		return idx;
	}

	/**
	 * The furthest-along rider's chainage in metres — exactly `chainages[order()[0]]` — in O(P) with no
	 * allocation. The charts' playhead needs only this maximum, so it must NOT pay for a full order() sort
	 * on every frame (that is a per-frame P·log P cost the rework had removed elsewhere). Null on an empty
	 * field, matching order()'s empty result.
	 */
	leadChainage(): number | null {
		const n = this.sources.length;
		if (n === 0) return null;
		let max = this.chainages[0] ?? NaN;
		for (let i = 1; i < n; i++) {
			const c = this.chainages[i] ?? NaN;
			if (c > max) max = c;
		}
		return max;
	}
}
