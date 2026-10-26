/**
 * Reset-database dialog — pure recomputation of the force-dependency checkboxes
 * (`data-forces="id,id"`), extracted from keys-and-data.ts so the safety-critical logic is
 * unit-testable without the dialog DOM (decision A2, 2026-07-20).
 *
 * Rules:
 *  - A target forced by ≥1 checked source is checked + disabled (it gets wiped with its forcer).
 *  - A target that loses its last checked forcer returns to unchecked + enabled — un-forcing must
 *    un-check, or the user could submit a wipe of a box they never chose.
 *  - Any other target is left untouched, so a manual user check survives recomputes. "Was forced"
 *    is derived from the checkbox state itself (checked AND disabled — only forcing produces that
 *    combination on a target), so there is no shadow state to drift.
 */

export interface ForcingSource {
	id: string;
	checked: boolean;
	/** Target checkbox ids this source force-selects while checked. */
	forces: string[];
}

export interface CheckboxState {
	checked: boolean;
	disabled: boolean;
}

export interface ForcedRecompute {
	/** New state per target id (every id that appears in some source's forces list and in `current`). */
	states: Record<string, CheckboxState>;
	/** Source→target pairs the recompute newly flipped on — drives the "X is required because Y" notice. */
	notices: Array<{ sourceId: string; targetId: string }>;
}

/** Recomputes every force-target's checked/disabled state from the sources' current checked state. */
export function recomputeForcedTargets(
	sources: ForcingSource[],
	current: Record<string, CheckboxState>,
): ForcedRecompute {
	const states: Record<string, CheckboxState> = {};
	for (const [id, s] of Object.entries(current)) states[id] = { ...s };

	const wasForced = new Set(
		Object.entries(current).filter(([, s]) => s.checked && s.disabled).map(([id]) => id),
	);

	const nowForced = new Set<string>();
	const notices: Array<{ sourceId: string; targetId: string }> = [];
	for (const src of sources) {
		if (!src.checked) continue;
		for (const targetId of src.forces) {
			const st = states[targetId];
			if (!st) continue; // forces list names an id not in the dialog — ignore
			if (!st.checked) notices.push({ sourceId: src.id, targetId });
			st.checked = true;
			st.disabled = true;
			nowForced.add(targetId);
		}
	}

	for (const id of wasForced) {
		if (!nowForced.has(id)) states[id] = { checked: false, disabled: false };
	}

	return { states, notices };
}
