/**
 * Checkpoint placement rules. Pure — no map, no DOM.
 *
 * Split out from the page so the thing that is easy to get wrong (where a click on the elevation
 * profile is allowed to land) can be tested without a browser. The clock formatting and the
 * distance↔pixel maths that used to live here are shared now — `core/format.ts` and
 * `core/profile-axis.ts` — but this stays race-side: it is the only `RaceCheckpointDto` consumer.
 *
 * These mirror `Services/Races/RaceCheckpointBounds.cs`, which is the AUTHORITY — the server re-checks
 * every write. What lives here only stops the gesture from offering a move the server would reject.
 */

import type { RaceCheckpointDto } from './race-api';

/** Two checkpoints closer than this give the pacing model a zero-length interval. Mirrors the C# constant. */
export const MIN_GAP_METRES = 50;

/** Which of the three a checkpoint is. The extremes are the start and finish lines. */
export type CheckpointRole = 'start' | 'intermediate' | 'finish';

/** The legal chainage range for one checkpoint, inclusive at both ends. */
export interface ChainageBounds {
	readonly lo: number;
	readonly hi: number;
}

/** A checkpoint's role, by chainage extremes — the same rule the server decides it by. */
export function roleOf(checkpoint: RaceCheckpointDto, all: readonly RaceCheckpointDto[]): CheckpointRole {
	const chainages = all.map((c) => c.chainageMeters);
	if (checkpoint.chainageMeters <= Math.min(...chainages)) return 'start';
	if (checkpoint.chainageMeters >= Math.max(...chainages)) return 'finish';
	return 'intermediate';
}

/**
 * Where `role` may sit, given every OTHER checkpoint and the course grid's length.
 *
 * The endpoints range over the whole ride: the grid is the recorded activity and the race is a window on
 * it, so the start line may be well after the first GPS point and the finish well before the last.
 */
export function checkpointBoundsFor(
	role: CheckpointRole, otherChainages: readonly number[], totalMeters: number,
): ChainageBounds {
	if (otherChainages.length === 0) return { lo: 0, hi: totalMeters };
	if (role === 'start') return { lo: 0, hi: Math.min(...otherChainages) - MIN_GAP_METRES };
	if (role === 'finish') return { lo: Math.max(...otherChainages) + MIN_GAP_METRES, hi: totalMeters };
	// An intermediate may cross its neighbours — the server renumbers seq to follow chainage.
	return { lo: Math.min(...otherChainages) + MIN_GAP_METRES, hi: Math.max(...otherChainages) - MIN_GAP_METRES };
}

/**
 * Snap a proposed checkpoint to a sensible place: inside its bounds, and not on top of an existing
 * checkpoint (two at the same chainage would give the pacing model a zero-length interval to normalize — a
 * divide-by-zero waiting to happen).
 *
 * Returns null when there is nowhere sensible to put it — which, during a drag, is what freezes the
 * marker at the last legal spot instead of letting it follow the pointer off the course.
 */
export function snapCheckpoint(
	chainage: number, existing: readonly RaceCheckpointDto[], bounds: ChainageBounds,
	minGapMetres = MIN_GAP_METRES,
): number | null {
	if (!Number.isFinite(chainage) || chainage < bounds.lo || chainage > bounds.hi) return null;
	for (const c of existing) {
		if (Math.abs(c.chainageMeters - chainage) < minGapMetres) return null;
	}
	return chainage;
}
