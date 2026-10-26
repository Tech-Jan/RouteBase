/**
 * series-mode-control.ts — the corner button that cycles a chart through its series modes.
 *
 * One button rather than a pair: two states read fine as "what you are looking at", and a segmented
 * control would cost twice the width in a chart corner.
 *
 * The stored preference is painted onto the button BEFORE the first render. The markup cannot carry
 * the active state — it does not know what this browser last chose — so a control that skipped this
 * would sit there claiming "Smoothed" over a chart drawing something else.
 *
 * DOM-thin on purpose: it owns the button and the pref, and calls back with the mode. What that mode
 * does to a chart belongs to the chart.
 */

import { SERIES_MODES, type SeriesMode } from './chart-series-modes';
import { getAccountSettings } from './account-settings';
import { devicePrefKeys, getEnum, setEnum } from './device-prefs';

/** What each mode is called, the icon that stands for it, and what the button promises next. */
const LABELS: Record<SeriesMode, { label: string; icon: string; title: string }> = {
	// Not "Raw": speed is already a 5 s distance/time average applied at import, so there is no
	// rawer series to show and a button offering one would be lying.
	recorded: {
		label: 'Recorded', icon: 'bi-activity',
		title: 'The recorded data, every sample — click for the smoothed view',
	},
	smoothBin: {
		label: 'Smoothed', icon: 'bi-graph-up',
		title: 'Smoothed and evened out for reading — click for the recorded data',
	},
};

export interface SeriesModeControlOptions {
	/** The button element. Absent markup = no control, not a crash. */
	readonly button: HTMLElement | null;
	/** Preference key suffix — one per chart, so two charts remember separately. */
	readonly prefKey: string;
	/** Modes to offer, in click order. Trim it where a chart cannot honour them all. */
	readonly modes?: readonly SeriesMode[];
	/** Called with the restored mode on wire-up, then on every change. */
	readonly onChange: (mode: SeriesMode) => void;
}

export interface SeriesModeControl {
	readonly mode: SeriesMode;
	destroy(): void;
}

/** The account-level "what should a speed chart open on", constrained to what this chart offers. */
function defaultSeriesMode(modes: readonly SeriesMode[]): SeriesMode {
	const configured = getAccountSettings().charts.defaultSpeedView as SeriesMode;
	if (modes.includes(configured)) return configured;
	return modes.includes('smoothBin') ? 'smoothBin' : (modes[0] ?? 'smoothBin');
}

export function createSeriesModeControl(options: SeriesModeControlOptions): SeriesModeControl | null {
	const { button, prefKey, onChange } = options;
	if (!button) return null;
	// Rebound after the guard: `paint` is a hoisted declaration, so TS will not carry the narrowing
	// into it from the early return above.
	const btn: HTMLElement = button;

	const modes = options.modes?.length ? options.modes : SERIES_MODES;
	const key = devicePrefKeys.chartSeriesMode(prefKey);
	// Two tiers, deliberately: the ACCOUNT setting says what a chart opens on, and the per-chart button
	// writes a DEVICE preference that overrides it. Pressing a button on one screen must not change
	// what the other screen opens on, and an account default must not fight a viewer who has chosen.
	let mode: SeriesMode = getEnum<SeriesMode>(key, modes, defaultSeriesMode(modes));

	// Built ONCE and then mutated in place. Rebuilding them per click detaches the very element the
	// click landed on, and a detached node's closest() finds nothing — which broke the collapsible
	// card's "ignore clicks on buttons in my header" guard and made switching views fold the chart.
	const icon = document.createElement('i');
	const text = document.createElement('span');
	text.className = 'chart-mode-label';
	btn.replaceChildren(icon, text);

	function paint(): void {
		const meta = LABELS[mode];
		// Icon plus label; CSS hides the label on narrow screens, so the icon has to carry the meaning
		// on its own there — which is also why the title stays a full sentence.
		icon.className = `bi ${meta.icon}`;
		text.textContent = meta.label;
		btn.title = meta.title;
		// Pressed = "not the default view", so the button reads as "on" exactly when it is changing what
		// you see. With several modes the highlight cannot say WHICH — the label is what carries that,
		// and the class is decoration.
		const isDefault = mode === defaultSeriesMode(modes);
		btn.setAttribute('aria-pressed', String(!isDefault));
		btn.classList.toggle('is-active', !isDefault);
		btn.classList.toggle('active', !isDefault); // the page button classes key off `.active`
	}

	function onClick(): void {
		const at = modes.indexOf(mode);
		mode = modes[(at + 1) % modes.length] ?? mode;
		setEnum(key, mode);
		paint();
		onChange(mode);
	}

	paint();
	btn.addEventListener('click', onClick);
	onChange(mode); // the chart opens on the restored mode, not on a default it would then contradict

	return {
		get mode(): SeriesMode { return mode; },
		destroy(): void { btn.removeEventListener('click', onClick); },
	};
}
