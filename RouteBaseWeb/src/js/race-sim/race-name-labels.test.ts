import { afterEach, describe, expect, it } from 'vitest';
import { MAX_EMPHASIS_HIGHLIGHTS, initNameToggle, labelledIndices } from './race-name-labels';

/**
 * The label policy is the guard on a SYMBOL layer whose cost is superlinear in label count — so what
 * these pin is mostly what does NOT get a name. A regression here does not look like a bug; it looks like
 * a map that has become slow.
 */
describe('labelledIndices', () => {
	const noEmphasis = new Set<number>();

	it('names the real riders and nobody else — the simulated field is anonymous', () => {
		const isReal = [false, true, false, false, true];

		expect(labelledIndices(isReal, noEmphasis, null, true)).toEqual([1, 4]);
	});

	it('names an emphasised rider too — that is half of "watch THESE"', () => {
		const isReal = [false, true, false, false];

		expect(labelledIndices(isReal, new Set([2]), null, true)).toEqual([1, 2]);
	});

	it('does not name an emphasised rider twice when he is also real', () => {
		const isReal = [true, false];

		expect(labelledIndices(isReal, new Set([0]), null, true)).toEqual([0]);
	});

	it('names whoever is followed, even when he is neither real nor emphasised', () => {
		const isReal = [false, true, false];

		expect(labelledIndices(isReal, noEmphasis, 2, true)).toEqual([1, 2]);
	});

	it('does not duplicate the followed rider when he already has a name', () => {
		const isReal = [true, false];

		expect(labelledIndices(isReal, noEmphasis, 0, true)).toEqual([0]);
	});

	/**
	 * The toggle means what it says. Keeping the followed rider's label would be the more "helpful"
	 * reading and the wrong one — the halo and the locked camera already say who is watched.
	 */
	it('names NOBODY when the toggle is off, including the followed rider', () => {
		const isReal = [true, true, false];

		expect(labelledIndices(isReal, new Set([2]), 1, false)).toEqual([]);
	});

	it('an empty field names nobody rather than throwing', () => {
		expect(labelledIndices([], noEmphasis, null, true)).toEqual([]);
	});

	/**
	 * The guard on "Emphasise" pressed with no filter, which takes the whole field. 424 labels is the
	 * symbol-collision drowning this module exists to prevent.
	 */
	it('stops naming emphasised riders past the threshold — they stay big and in front', () => {
		const size = MAX_EMPHASIS_HIGHLIGHTS + 1;
		const isReal = Array.from({ length: size }, (_, i) => i === 0);
		const everyone = new Set(Array.from({ length: size }, (_, i) => i));

		// Only the real rider keeps his name; the emphasis itself is untouched (that is the markers' job).
		expect(labelledIndices(isReal, everyone, null, true)).toEqual([0]);
	});

	it('names every emphasised rider right up to the threshold', () => {
		const isReal = Array.from({ length: MAX_EMPHASIS_HIGHLIGHTS }, () => false);
		const emphasised = new Set(Array.from({ length: MAX_EMPHASIS_HIGHLIGHTS }, (_, i) => i));

		expect(labelledIndices(isReal, emphasised, null, true)).toHaveLength(MAX_EMPHASIS_HIGHLIGHTS);
	});

	/**
	 * The per-rider opt-out. Being real is what EARNS a label, so it is also what makes the opt-out
	 * necessary: without it the only way to quiet a second real rider is to delete him from the race.
	 */
	it('does not name a real rider whose call-out was hidden', () => {
		const isReal = [true, true, false];

		expect(labelledIndices(isReal, noEmphasis, null, true, new Set([1]))).toEqual([0]);
	});

	it('beats emphasis too — the switch means "stop calling him out", not "unless"', () => {
		const isReal = [false, false];

		expect(labelledIndices(isReal, new Set([0, 1]), null, true, new Set([0]))).toEqual([1]);
	});

	it('still names the rider being FOLLOWED — that is a deliberate act, undone by looking away', () => {
		const isReal = [true, false];

		expect(labelledIndices(isReal, noEmphasis, 0, true, new Set([0]))).toEqual([0]);
	});
});

describe('initNameToggle', () => {
	afterEach(() => {
		document.body.innerHTML = '';
		localStorage.clear();
	});

	function mount(): HTMLButtonElement {
		const button = document.createElement('button');
		button.id = 'namesBtn';
		document.body.appendChild(button);
		return button;
	}

	it('starts ON — the map named its real riders before this switch existed', () => {
		mount();

		const toggle = initNameToggle({ buttonId: 'namesBtn', prefKey: 'test', onChange: () => {} });

		expect(toggle.namesOn).toBe(true);
	});

	it('flips state, reports it, and marks the button pressed', () => {
		const button = mount();
		const seen: boolean[] = [];
		const toggle = initNameToggle({ buttonId: 'namesBtn', prefKey: 'test', onChange: (on) => seen.push(on) });

		button.click();

		expect(toggle.namesOn).toBe(false);
		expect(seen).toEqual([false]);
		expect(button.getAttribute('aria-pressed')).toBe('false');
		expect(button.classList.contains('is-active')).toBe(false);
	});

	it('remembers the choice for the next visit — a device pref, never the server', () => {
		const button = mount();
		initNameToggle({ buttonId: 'namesBtn', prefKey: 'test', onChange: () => {} }).destroy();
		button.click();   // listener removed by destroy(), so flip it through a fresh toggle instead

		const first = initNameToggle({ buttonId: 'namesBtn', prefKey: 'test', onChange: () => {} });
		button.click();
		expect(first.namesOn).toBe(false);

		const reopened = initNameToggle({ buttonId: 'namesBtn', prefKey: 'test', onChange: () => {} });
		expect(reopened.namesOn).toBe(false);
	});

	it('a missing button is not an error — the toggle is optional chrome', () => {
		const toggle = initNameToggle({ buttonId: 'notThere', prefKey: 'test', onChange: () => {} });

		expect(toggle.namesOn).toBe(true);
		expect(() => toggle.destroy()).not.toThrow();
	});
});
