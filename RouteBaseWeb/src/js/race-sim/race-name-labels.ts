/**
 * Who gets a name on the map, and the toolbar switch that turns names off.
 *
 * The names ride a SYMBOL layer, and symbol layers run text collision detection every frame — the cost is
 * superlinear in label count, which is why race-markers keeps them in their own small source. So the named
 * set is small BY CONSTRUCTION here, not capped after the fact. Exactly three things earn a name:
 *
 *   real riders  — few by nature, and theirs is the only line that is TRUE rather than modelled
 *   emphasised   — hand-picked, a handful at a time, by the person watching
 *   the followed — one
 *
 * The other 400-odd simulated riders stay anonymous until one of those three makes them interesting.
 *
 * Its own module rather than four lines inside the view because the policy is pure and the view is not:
 * this is decidable from four values and testable without a map.
 */

import { devicePrefKeys, getBool, setBool } from '../core/device-prefs';

/**
 * How many emphasised riders still count as "watch THESE".
 *
 * Above it, emphasis keeps its size and its draw order but stops earning the two things that are only
 * affordable for a handful: a NAME on the map, and a PINNED row in the follow sidebar. Emphasise with no
 * filter takes the whole field, so without a ceiling both become 424 — 424 labels is exactly the
 * symbol-collision drowning this module exists to prevent, and 424 pinned rows would push the field list
 * off the screen entirely.
 *
 * Not a silent truncation of a list somebody asked for: past a dozen, "emphasise" has stopped meaning
 * "watch these" and started meaning "paint this group", and a group wants neither. Real riders and the
 * followed rider are unaffected either way.
 *
 * Shared with race-replay-overlay's pinned list so the two cannot answer the question differently.
 */
export const MAX_EMPHASIS_HIGHLIGHTS = 12;

/**
 * The riders whose call-out the owner switched OFF, as field indices.
 *
 * Lives here rather than in each caller because it is the other half of the policy above: "who are we
 * calling out" and "who did the user tell us to stop calling out" are one question, and two copies of
 * the second would let the map and the speed chart disagree about it.
 */
export function calloutOffIndices(participants: readonly { readonly calloutOff?: boolean }[]): Set<number> {
	const set = new Set<number>();
	participants.forEach((p, i) => { if (p.calloutOff) set.add(i); });
	return set;
}

/**
 * The riders to label, as field indices.
 *
 * `namesOn` false returns NOTHING — including the followed rider. A toggle that quietly kept one label
 * would be the more "helpful" reading and the wrong one: the follow halo and the locked camera already
 * say who is being watched, so the exception would buy nothing and cost the toggle its meaning.
 *
 * `calloutOff` is the per-rider opt-out, and it beats BOTH earning rules — a real rider is called out by
 * virtue of being real, so without it there is no way to quiet one short of deleting him from the race.
 * The FOLLOWED rider is exempt: following him is a deliberate act, and it is undone by looking away.
 */
export function labelledIndices(
	isReal: readonly boolean[],
	emphasised: ReadonlySet<number>,
	followIndex: number | null,
	namesOn: boolean,
	calloutOff: ReadonlySet<number> = new Set(),
): number[] {
	if (!namesOn) return [];

	const nameEmphasised = emphasised.size <= MAX_EMPHASIS_HIGHLIGHTS;
	const named: number[] = [];
	for (let i = 0; i < isReal.length; i++) {
		if (calloutOff.has(i)) continue;
		if (isReal[i] || (nameEmphasised && emphasised.has(i))) named.push(i);
	}
	// Appended, not sorted in: the followed rider is usually already here (he is real, or you emphasised
	// him), and the label pool is rebuilt from this order — a stable order means fewer pool churns.
	if (followIndex != null && !named.includes(followIndex)) named.push(followIndex);
	return named;
}

export interface NameToggleOptions {
	readonly buttonId: string;
	/** Which map this remembers for — the device-pref key, same shape as the display-mode one beside it. */
	readonly prefKey: string;
	/** The toggle flipped. Re-derive the labels and flush; nothing else changes. */
	readonly onChange: (namesOn: boolean) => void;
}

export interface NameToggle {
	/** Read it when building the label set — this is the toggle's only state. */
	readonly namesOn: boolean;
	destroy(): void;
}

/**
 * Wire the toolbar's Names button. A DEVICE preference (core/device-prefs' own tier rule: an on/off on
 * one screen is something you would be annoyed to INHERIT, not to lose), so it never reaches the server.
 *
 * Names default to ON — that is what the map did before this switch existed, and a feature that silently
 * turns itself off on first load is indistinguishable from a broken one.
 */
export function initNameToggle(options: NameToggleOptions): NameToggle {
	const button = document.getElementById(options.buttonId) as HTMLButtonElement | null;
	const key = devicePrefKeys.mapNamesOn(options.prefKey);
	let namesOn = getBool(key, true);

	function paint(): void {
		if (!button) return;
		button.classList.toggle('is-active', namesOn);
		// aria-pressed, not aria-checked: this is a toggle BUTTON, and a screen reader announcing
		// "Names, pressed" is the whole state — the icon-only phone rendering has no other cue.
		button.setAttribute('aria-pressed', namesOn ? 'true' : 'false');
		button.title = namesOn ? 'Hide rider names' : 'Show names above real and emphasised riders';
	}

	const onClick = (): void => {
		namesOn = !namesOn;
		setBool(key, namesOn);
		paint();
		options.onChange(namesOn);
	};

	button?.addEventListener('click', onClick);
	paint();   // the stored state, applied before the first paint — no flash of the wrong label set

	return {
		get namesOn(): boolean { return namesOn; },
		destroy(): void { button?.removeEventListener('click', onClick); },
	};
}
