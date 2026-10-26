/**
 * Bucketing the profile's dots by their painted style — the pure half of `profile-chart`'s foreground.
 *
 * Its own module for two reasons: it is pure, so it is testable without a canvas; and it is the seam that
 * makes "this no longer runs every frame" a countable claim rather than a benchmark (PERF P3). Grouping
 * used to be rebuilt inside the 30 Hz frame path — one key string per dot per frame, ~508 riders, plus a
 * `split` per bucket — even though nothing it depends on changes while the replay runs.
 */

/** One draw batch: every dot sharing a fillStyle and an alpha, so a frame is a few state changes. */
export interface StyleGroup {
	readonly color: string;
	readonly isDim: boolean;
	readonly isFinished: boolean;
	/** Drawn bigger, and LAST — see the sort in groupByStyle. */
	readonly isEmphasised: boolean;
	readonly indices: number[];
}

/** Fallback dot colour when the caller's array is short or sparse. */
export const DEFAULT_DOT_COLOR = '#4dabf7';

/**
 * Buckets dot indices by (colour, dimmed, finished, emphasised), in DRAW order.
 *
 * Depends only on style inputs — never on `values`/`heights`, which are the parts that actually move —
 * so the result is safe to memoise across frames and MUST be invalidated when any of these change.
 * The style parts ride on the group rather than being encoded into the key and split back out, so the
 * key never escapes this function.
 */
export function groupByStyle(
	colors: readonly string[],
	finished: ArrayLike<number>,
	bright: ReadonlySet<number> | null,
	visible: ReadonlySet<number> | null,
	emphasised: ReadonlySet<number> | null,
	count: number,
): StyleGroup[] {
	const byKey = new Map<string, StyleGroup>();
	for (let i = 0; i < count; i++) {
		if (visible && !visible.has(i)) continue;
		const color = colors[i] ?? DEFAULT_DOT_COLOR;
		const isDim = !!bright && !bright.has(i);
		const isFinished = finished[i] === 1;
		// Dim wins: a rider the filter dropped recedes even if he is emphasised, exactly as on the map.
		const isEmphasised = !isDim && !!emphasised && emphasised.has(i);
		const key = `${color}|${isDim ? 'd' : ''}|${isFinished ? 'f' : ''}|${isEmphasised ? 'e' : ''}`;
		const group = byKey.get(key);
		if (group) group.indices.push(i);
		else byKey.set(key, { color, isDim, isFinished, isEmphasised, indices: [i] });
	}

	// Batch order IS z-order: canvas has no z-index, the last fill wins. Returning insertion order left
	// "who is on top" decided by which rider the loop met first — i.e. by participant id. Emphasised dots
	// land last and dimmed ones first, matching race-markers' circle-sort-key; if these two ever disagree
	// the map and the profile show a different rider in front of the same pile.
	return [...byKey.values()].sort((a, b) => rank(a) - rank(b));
}

function rank(group: StyleGroup): number {
	if (group.isDim) return 0;
	return group.isEmphasised ? 2 : 1;
}
