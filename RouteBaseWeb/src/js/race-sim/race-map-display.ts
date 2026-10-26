/**
 * Drives the race map from the results table's filter.
 *
 * The shape is lifted from the activities page (overview/activities-grid.ts `createMapDisplayController`),
 * which solves the same problem for tracks: a universe, a filtered subset, a three-way mode, and a short
 * coalesce so a burst of keystrokes in the filter row costs one map update rather than eight. That one is
 * not exported and is welded to the activities layers, so this is the same idea over `RaceMarkers`.
 *
 * Pure of MapLibre: it talks to a small sink interface, so the modes can be tested without a map.
 */

/**
 * What the table's filter does to the field.
 *
 *   all       — the map ignores the filter. Everyone, full strength.
 *   highlight — everyone stays drawn; the non-matching recede. The DEFAULT, because a replay is about
 *               gaps: hiding the field you are being compared against destroys the thing you came to see.
 *   hide      — only the matching riders are on the map.
 */
export type RaceMapMode = 'all' | 'highlight' | 'hide';

/** What the controller needs of the marker layer. Exactly the three seams, nothing else. */
export interface RaceMapSink {
	setDimmed(exceptIndices: readonly number[] | null): void;
	setVisible(indices: readonly number[] | null): void;
	flushNow(): void;
}

export interface RaceMapDisplayOptions {
	readonly mode?: RaceMapMode | undefined;
	/** Collapses a burst of filter keystrokes into one map update. Matches the activities page. */
	readonly coalesceMs?: number | undefined;
	readonly setTimeoutFn?: typeof setTimeout | undefined;
	readonly clearTimeoutFn?: typeof clearTimeout | undefined;
}

export interface RaceMapDisplay {
	/**
	 * The rows currently passing the filter, as FIELD INDICES (not participant ids — the markers are
	 * index-addressed). `filtering` is false when no column filter is active at all, which is different
	 * from "a filter that happens to match everyone": the former must not dim, the latter must.
	 */
	onFiltered(indices: readonly number[], filtering: boolean): void;
	setMode(mode: RaceMapMode): void;
	/**
	 * Push the current mode and filter at the sinks again.
	 *
	 * The map layer does not survive a style switch or a bfcache restore — it is destroyed and rebuilt
	 * from scratch — and a freshly built layer knows nothing of a filter the user applied ten seconds ago.
	 * Without this the whole field quietly came back into view, while the profile (which is NOT rebuilt)
	 * went on showing the filter. This controller holds the state; it must be able to re-assert it.
	 */
	reapply(): void;
	destroy(): void;
}

const DEFAULT_COALESCE_MS = 150;

export function createRaceMapDisplay(
	markers: RaceMapSink,
	options: RaceMapDisplayOptions = {},
): RaceMapDisplay {
	const coalesceMs = options.coalesceMs ?? DEFAULT_COALESCE_MS;
	const setTimeoutFn = options.setTimeoutFn ?? setTimeout;
	const clearTimeoutFn = options.clearTimeoutFn ?? clearTimeout;

	let mode: RaceMapMode = options.mode ?? 'highlight';
	let filtered: readonly number[] = [];
	let filtering = false;
	let timer: ReturnType<typeof setTimeout> | undefined;

	function apply(): void {
		// `hide` isolates; every other mode shows the whole field. Nulling the filter in the other modes
		// is not redundant — it undoes a previous `hide`.
		markers.setVisible(mode === 'hide' ? filtered : null);

		// Dim only when a filter is actually narrowing something. An unfiltered table must not paint the
		// entire field grey just because "everyone matches".
		markers.setDimmed(mode === 'highlight' && filtering ? filtered : null);

		markers.flushNow();
	}

	function schedule(): void {
		if (timer !== undefined) clearTimeoutFn(timer);
		timer = setTimeoutFn(() => {
			timer = undefined;
			apply();
		}, coalesceMs);
	}

	return {
		onFiltered(indices, isFiltering) {
			filtered = [...indices];
			filtering = isFiltering;
			schedule();
		},
		setMode(next) {
			if (next === mode) return;
			mode = next;
			// A mode flip is a deliberate click, not a keystroke — it applies at once. Any pending
			// coalesced filter update is folded into this one rather than firing again behind it.
			if (timer !== undefined) {
				clearTimeoutFn(timer);
				timer = undefined;
			}
			apply();
		},
		reapply() {
			apply();
		},
		destroy() {
			if (timer !== undefined) clearTimeoutFn(timer);
			timer = undefined;
		},
	};
}
