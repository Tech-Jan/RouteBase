// @vitest-environment node
import { describe, it, expect } from 'vitest';
import { recomputeForcedTargets, type CheckboxState } from './reset-dialog-forces';

// A2: the reset-database dialog's force-dependency recompute, pure. The dialog fixture tests in
// keys-and-data.test.ts cover the DOM wiring; these pin the state machine itself.

const box = (checked: boolean, disabled: boolean): CheckboxState => ({ checked, disabled });

describe('recomputeForcedTargets', () => {
	it('force-checks + disables an unchecked target and reports the notice pair', () => {
		const { states, notices } = recomputeForcedTargets(
			[{ id: 'src', checked: true, forces: ['tgt'] }],
			{ tgt: box(false, false) },
		);
		expect(states.tgt).toEqual(box(true, true));
		expect(notices).toEqual([{ sourceId: 'src', targetId: 'tgt' }]);
	});

	it('keeps an already-checked target forced without a notice', () => {
		const { states, notices } = recomputeForcedTargets(
			[{ id: 'src', checked: true, forces: ['tgt'] }],
			{ tgt: box(true, true) },
		);
		expect(states.tgt).toEqual(box(true, true));
		expect(notices).toEqual([]);
	});

	it('un-forcing clears the target: checked+disabled returns to unchecked+enabled', () => {
		// The A2 bug: leaving the box checked let the user submit a wipe they never chose.
		const { states } = recomputeForcedTargets(
			[{ id: 'src', checked: false, forces: ['tgt'] }],
			{ tgt: box(true, true) },
		);
		expect(states.tgt).toEqual(box(false, false));
	});

	it('leaves a manually checked (enabled) target alone when its forcer is unchecked', () => {
		// Regression pin: clearing EVERY un-forced target on each recompute made it impossible to
		// select a force-target on its own (and broke the "Photos only" quick-select).
		const { states } = recomputeForcedTargets(
			[{ id: 'src', checked: false, forces: ['tgt'] }],
			{ tgt: box(true, false) },
		);
		expect(states.tgt).toEqual(box(true, false));
	});

	it('keeps a target forced while another checked source still forces it', () => {
		const { states } = recomputeForcedTargets(
			[
				{ id: 'a', checked: false, forces: ['tgt'] },
				{ id: 'b', checked: true, forces: ['tgt'] },
			],
			{ tgt: box(true, true) },
		);
		expect(states.tgt).toEqual(box(true, true));
	});

	it('ignores forces ids with no matching entry in current', () => {
		const { states, notices } = recomputeForcedTargets(
			[{ id: 'src', checked: true, forces: ['ghost', 'tgt'] }],
			{ tgt: box(false, false) },
		);
		expect(states.tgt).toEqual(box(true, true));
		expect(Object.keys(states)).toEqual(['tgt']);
		expect(notices).toEqual([{ sourceId: 'src', targetId: 'tgt' }]);
	});
});
