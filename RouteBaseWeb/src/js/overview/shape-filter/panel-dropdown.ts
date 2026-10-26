/**
 * shape-filter/panel-dropdown.ts — thin <select> helpers for the panel's context-sensitive rows.
 *
 * DOM only, no state. The panel builds a select once, then swaps its option set (zone vs gate roles)
 * and per-option disabled state (extent options greyed with a "why" tooltip) through these helpers —
 * so the same control serves several contexts without rebuilding the element.
 */

export interface SelectOption {
	value: string;
	label: string;
	/** Native tooltip shown on hover — used to explain a role or why an option is disabled. */
	title?: string;
}

/** A styled <select> that reports the picked value (not the change event) to `onChange`. */
export function buildSelect(className: string, onChange: (value: string) => void): HTMLSelectElement {
	const sel = document.createElement('select');
	sel.className = className;
	sel.addEventListener('change', () => onChange(sel.value));
	return sel;
}

/** Replace the whole option set (e.g. switching the role select between zone and gate roles). */
export function setSelectOptions(sel: HTMLSelectElement, options: readonly SelectOption[]): void {
	sel.replaceChildren();
	for (const o of options) {
		const opt = document.createElement('option');
		opt.value = o.value;
		opt.textContent = o.label;
		if (o.title) opt.title = o.title;
		sel.appendChild(opt);
	}
}

export function setSelectValue(sel: HTMLSelectElement, value: string): void {
	sel.value = value;
}

/** Grey out one option (keeping it visible) and set its tooltip explaining why. */
export function setOptionDisabled(sel: HTMLSelectElement, value: string, disabled: boolean, title = ''): void {
	const opt = Array.from(sel.options).find((o) => o.value === value);
	if (!opt) return;
	opt.disabled = disabled;
	opt.title = title;
}
