/**
 * Race pacing model — turns a finisher's checkpoint times into a plausible ride over the terrain.
 *
 * Pure: no DOM, no fetch, no map. Unit-testable without mocks.
 *
 * Validated against a real ride: given ONLY the finish time, the
 * synthetic marker stays a median 317 m from where the rider actually was — versus 1029 m for the
 * constant-pace spread the app does today (replay-engine-base.ts computeCumulativeTime).
 *
 * Three rules the spike bought, encoded here so a caller cannot get them wrong:
 *
 *   1. Weights are per SEGMENT, not per node. n weights over n-1 gaps is an off-by-one that leaves the
 *      finish time short of the number it is supposed to hit exactly.
 *   2. The cost curve is ISOTONIC (monotone in grade). Raw binned medians oscillate on the descent
 *      (one technical switchback bin reads slower than the 20%-steeper bins either side), and an
 *      oscillating descent curve flings riders down the mountain. The descent speed CEILING then
 *      emerges by itself: the steep bins pool into a plateau instead of extrapolating ever faster.
 *   3. Fatigue is applied ONLY to riders with no intermediate splits. Fatigue and checkpoints are
 *      substitutes — a checkpoint MEASURES the fade that fatigue merely guesses at, and stacking both
 *      makes the fit worse. `simulateArrivals` enforces this; it is not left to the caller.
 */

/**
 * What the pacing model needs of the course: chainage and gradient. Deliberately NOT the coordinates —
 * the model decides WHEN a rider is somewhere, never WHERE that somewhere is. `CourseGrid`
 * (race-field.ts) adds the geometry for the renderer.
 */
import { must } from '../core/invariant';

export interface CourseProfile {
	/** Uniform chainage: `distance[i] === i * stepMeters`. */
	readonly distance: Float64Array | number[];
	/** Grade in PERCENT at each node. */
	readonly gradePercent: Float64Array | number[];
	readonly stepMeters: number;
}

/** A known (chainage, elapsed) anchor. Start and Finish are ordinary anchors. */
export interface Anchor {
	/** Node index into the course grid. */
	readonly node: number;
	/** Elapsed seconds from the race start. */
	readonly elapsedSeconds: number;
}

/** Seconds-per-metre by grade bin. Monotone non-decreasing in grade, by construction. */
export interface CostCurve {
	/** Bin width in grade-percent. */
	readonly binPercent: number;
	/** Lowest bin index present. */
	readonly minBin: number;
	/** cost[i] is the seconds-per-metre of bin (minBin + i). */
	readonly cost: Float64Array;
}

export interface PacingOptions {
	/**
	 * Fade over the course: cost *= 1 + k·(d/D). Applied ONLY when there are no intermediate anchors
	 * (rule 3). Default from the spike sweep.
	 */
	readonly fatigueK?: number;
	/**
	 * Hill sensitivity. `cost_a(g) = flat · (cost(g)/flat)^alpha` — alpha < 1 flattens a strong rider's
	 * response to gradient, alpha > 1 exaggerates a weak one's. Preserves exactness (it reshapes weights
	 * before normalization). NOT yet validated against real data — ships at 1 (off).
	 */
	readonly abilityAlpha?: number;
	/** Per-rider jitter as a fraction of segment cost (0 = none). Deterministic given `seed`. */
	readonly noise?: number;
	/** PRNG seed, so a replay is identical every time it is watched. */
	readonly seed?: number;
}

/** Spike sweep optimum on the reference ride (median drift 950 m → 317 m). */
export const DEFAULT_FATIGUE_K = 0.35;

const DEFAULT_BIN_PERCENT = 1.0;

/** Deterministic PRNG — a race must replay identically every time. */
function mulberry32(seed: number): () => number {
	let a = seed >>> 0;
	return () => {
		a = (a + 0x6d2b79f5) >>> 0;
		let t = a;
		t = Math.imul(t ^ (t >>> 15), t | 1);
		t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
		return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
	};
}

/**
 * Pool-adjacent-violators: nearest monotone non-decreasing fit, weighted by sample count.
 * Cost must not fall as the road tilts up; anything else is noise, and on the descent side it is the
 * difference between a plateau and a rider doing 90 km/h down a switchback.
 */
function pava(values: number[], weights: number[]): number[] {
	const blocks: Array<{ value: number; weight: number; count: number }> = values.map((v, i) => ({
		value: v,
		weight: must(weights[i], 'weights lockstep with values'),
		count: 1,
	}));
	let i = 0;
	while (i < blocks.length - 1) {
		const a = must(blocks[i], 'i < length-1 (loop-bounded)');
		const b = must(blocks[i + 1], 'i+1 <= length-1 (loop-bounded)');
		if (a.value <= b.value + 1e-12) {
			i++;
			continue;
		}
		const weight = a.weight + b.weight;
		blocks.splice(i, 2, {
			value: (a.value * a.weight + b.value * b.weight) / weight,
			weight,
			count: a.count + b.count,
		});
		if (i > 0) i--;
	}
	const out: number[] = [];
	for (const block of blocks) {
		for (let k = 0; k < block.count; k++) out.push(block.value);
	}
	return out;
}

function median(values: number[]): number {
	const s = [...values].sort((x, y) => x - y);
	const mid = s.length >> 1;
	return s.length % 2
		? must(s[mid], 'median of non-empty list')
		: (must(s[mid - 1], 'median of non-empty list') + must(s[mid], 'median of non-empty list')) / 2;
}

/**
 * Fit the grade→cost curve from a REFERENCE EFFORT on the course (a real ride, ideally the owner's).
 * The synthetic field then inherits that rider's terrain response, which is what makes it look real.
 *
 * Bins + isotonic smoothing on purpose: a per-node lookup would reproduce the reference ride by
 * memorising it, and would tell us nothing about anyone else.
 */
export function fitCostCurve(
	samples: ReadonlyArray<{ gradePercent: number; secondsPerMetre: number }>,
	binPercent: number = DEFAULT_BIN_PERCENT,
): CostCurve {
	if (samples.length === 0) throw new Error("fitCostCurve: no samples");

	const bins = new Map<number, number[]>();
	for (const s of samples) {
		if (!Number.isFinite(s.secondsPerMetre) || s.secondsPerMetre <= 0) continue;
		const b = Math.floor(s.gradePercent / binPercent);
		const bucket = bins.get(b);
		if (bucket) bucket.push(s.secondsPerMetre);
		else bins.set(b, [s.secondsPerMetre]);
	}
	// A bin needs a few samples before its median means anything.
	const solid = [...bins.entries()].filter(([, v]) => v.length >= 3);
	if (solid.length === 0) throw new Error("fitCostCurve: no bin has enough samples");

	const minBin = Math.min(...solid.map(([b]) => b));
	const maxBin = Math.max(...solid.map(([b]) => b));
	const raw = new Map(solid.map(([b, v]) => [b, median(v)]));

	// Fill gaps so the isotonic pass sees a contiguous domain; interpolated bins carry ~no weight.
	const values: number[] = [];
	const weights: number[] = [];
	for (let b = minBin; b <= maxBin; b++) {
		const known = raw.get(b);
		if (known !== undefined) {
			values.push(known);
			weights.push(must(bins.get(b), 'raw keys come from bins').length);
			continue;
		}
		let left = b - 1;
		while (left >= minBin && !raw.has(left)) left--;
		let right = b + 1;
		while (right <= maxBin && !raw.has(right)) right++;
		const lv = must(raw.get(left), 'gap bounded left by a solid bin');
		const rv = must(raw.get(right), 'gap bounded right by a solid bin');
		const t = (b - left) / (right - left);
		values.push(lv + t * (rv - lv));
		weights.push(1);
	}

	return { binPercent, minBin, cost: Float64Array.from(pava(values, weights)) };
}

/** Seconds per metre at a grade. Clamped outside the fitted range — this IS the descent speed ceiling. */
export function costAt(curve: CostCurve, gradePercent: number): number {
	const { binPercent, minBin, cost } = curve;
	const b = gradePercent / binPercent - minBin;
	// ?? NaN: fitCostCurve never returns an empty curve, so these fallbacks are unreachable.
	if (b <= 0) return cost[0] ?? NaN;
	if (b >= cost.length - 1) return cost[cost.length - 1] ?? NaN;
	const i = Math.floor(b);
	return (cost[i] ?? NaN) + (b - i) * ((cost[i + 1] ?? NaN) - (cost[i] ?? NaN));
}

/**
 * THE model. Produces each node's arrival time, hitting every anchor EXACTLY.
 *
 * Inside each checkpoint interval: `Δt_i = (t1 - t0) · w_i / Σw`, where `w_i` is the segment's
 * terrain cost. The normalization is what makes the anchor times exact — no solver, no iteration —
 * and every realism layer reshapes `w` BEFORE it, so exactness is never at risk.
 *
 * @returns arrival seconds per node (length = grid node count).
 */
export function simulateArrivals(
	grid: CourseProfile,
	curve: CostCurve,
	anchors: ReadonlyArray<Anchor>,
	options: PacingOptions = {},
): Float32Array {
	const n = grid.distance.length;
	if (anchors.length < 2) throw new Error("simulateArrivals: need at least a start and a finish anchor");

	// Bounds first: an anchor past the last node would walk off the grade array and quietly fill the
	// whole ride with NaN, which surfaces much later as markers that simply never appear.
	for (const a of anchors) {
		if (!Number.isInteger(a.node) || a.node < 0 || a.node >= n)
			throw new Error(`simulateArrivals: anchor node ${a.node} is outside the course (0..${n - 1})`);
	}
	for (const a of anchors) {
		// `elapsedSeconds < previous` below CANNOT reject a NaN — every comparison with NaN is false — so a
		// NaN anchor would walk through it and do precisely what the note above warns of: `span = t1 - t0`
		// is NaN, and every arrival after it is NaN. Finiteness has to be tested on its own.
		if (!Number.isFinite(a.elapsedSeconds))
			throw new Error(`simulateArrivals: anchor time ${a.elapsedSeconds} is not finite`);
	}
	for (let i = 1; i < anchors.length; i++) {
		const cur = must(anchors[i], 'i < length (loop-bounded)');
		const prev = must(anchors[i - 1], 'i-1 >= 0 (loop-bounded)');
		if (cur.node <= prev.node)
			throw new Error("simulateArrivals: anchors must be strictly increasing in node");
		if (cur.elapsedSeconds < prev.elapsedSeconds)
			throw new Error("simulateArrivals: anchors must be non-decreasing in time");
	}

	// Rule 3: fatigue is a stand-in for splits we do not have. With intermediate anchors we have them.
	const hasIntermediates = anchors.length > 2;
	const fatigueK = hasIntermediates ? 0 : (options.fatigueK ?? DEFAULT_FATIGUE_K);
	const alpha = options.abilityAlpha ?? 1;
	const noise = options.noise ?? 0;
	const rand = mulberry32(options.seed ?? 0);

	const total = grid.distance[n - 1] || 1;
	const flatCost = costAt(curve, 0);

	const arrivals = new Float32Array(n);
	for (let a = 0; a < anchors.length - 1; a++) {
		const { node: i0, elapsedSeconds: t0 } = must(anchors[a], 'a < length-1 (loop-bounded)');
		const { node: i1, elapsedSeconds: t1 } = must(anchors[a + 1], 'a+1 <= length-1 (loop-bounded)');

		const weights = new Float64Array(i1 - i0);
		let sum = 0;
		for (let i = i0; i < i1; i++) {
			// Segment midpoint — the segment between node i and i+1, not the node itself.
			// ?? NaN: anchor bounds were validated against the grid above — fallbacks unreachable.
			const grade = ((grid.gradePercent[i] ?? NaN) + (grid.gradePercent[i + 1] ?? NaN)) / 2;
			let cost = costAt(curve, grade);

			// Ability: bend the response to gradient without moving the flat-ground cost.
			if (alpha !== 1 && flatCost > 0 && cost > 0) cost = flatCost * Math.pow(cost / flatCost, alpha);

			const mid = ((grid.distance[i] ?? NaN) + (grid.distance[i + 1] ?? NaN)) / 2;
			let w = cost * grid.stepMeters * (1 + fatigueK * (mid / total));
			if (noise > 0) w *= 1 + noise * (rand() * 2 - 1);

			weights[i - i0] = w;
			sum += w;
		}

		arrivals[i0] = t0;
		const span = t1 - t0;
		if (sum <= 0) {
			// Degenerate interval (zero-length or zero-cost) — spread it evenly rather than divide by zero.
			for (let i = i0; i < i1; i++) arrivals[i + 1] = t0 + (span * (i - i0 + 1)) / (i1 - i0);
			continue;
		}
		let acc = t0;
		for (let i = i0; i < i1; i++) {
			acc += (span * (weights[i - i0] ?? NaN)) / sum;
			arrivals[i + 1] = acc;
		}
	}

	// Anchors need not span the whole course (a DNF stops early); hold anything past the last one.
	const last = must(anchors[anchors.length - 1], 'anchors.length >= 2 (guarded above)');
	for (let i = last.node + 1; i < n; i++) arrivals[i] = last.elapsedSeconds;

	return arrivals;
}
