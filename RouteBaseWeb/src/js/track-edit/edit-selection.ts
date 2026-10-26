/**
 * edit-selection.ts — pure range-selection state machine (A3 §3). Clicks arrive as CURRENT-state ordinals
 * (invariant 2 — the chart/map click hook indexes the live preview array), so no distance round-trip and no
 * back-translation. First click anchors, second completes an inclusive [from..to] range (auto-swapped if the
 * user clicked end-before-start, mirroring the segment picker), a third click starts over. Every op apply /
 * undo / redo clears the selection (its ordinals reference the previous state).
 */

export interface SelectionState {
	anchor: number | null;
	from: number | null;
	to: number | null;
	complete: boolean;
}

export function emptySelection(): SelectionState {
	return { anchor: null, from: null, to: null, complete: false };
}

/** Advance the state machine by one click. No anchor / already-complete ⇒ start a fresh anchor. */
export function click(selection: SelectionState, index: number): SelectionState {
	if (selection.anchor === null || selection.complete) {
		return { anchor: index, from: null, to: null, complete: false };
	}
	const from = Math.min(selection.anchor, index);
	const to = Math.max(selection.anchor, index);
	return { anchor: selection.anchor, from, to, complete: true };
}

export function clear(): SelectionState {
	return emptySelection();
}

/** The completed inclusive range, or null while the selection is still being made. */
export function selectionRange(selection: SelectionState): { from: number; to: number } | null {
	return selection.complete && selection.from !== null && selection.to !== null
		? { from: selection.from, to: selection.to }
		: null;
}

/** Point count of a completed selection (0 while incomplete). */
export function selectionPointCount(selection: SelectionState): number {
	const range = selectionRange(selection);
	return range ? range.to - range.from + 1 : 0;
}

/** True only for a completed [0..count-1] span — the whole-track crop no-op guard (E2). */
export function isWholeTrack(selection: SelectionState, count: number): boolean {
	const range = selectionRange(selection);
	return range !== null && range.from === 0 && range.to === count - 1;
}

