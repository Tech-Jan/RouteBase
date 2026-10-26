/**
 * Makes a plain <select> searchable: a text box filters its options as you type.
 *
 * Matching is pure and DOM-free so it can be unit-tested; the wiring rebuilds the option list from a
 * snapshot taken at attach time, so filtering never destroys options it later has to restore.
 */

import { must } from './invariant';

export interface SelectOption {
	value: string;
	label: string;
}

/** Case-insensitive AND-match: every whitespace-separated term must appear somewhere in the label. */
export function matchesSelectQuery(label: string, query: string): boolean {
	const terms = query.toLowerCase().split(/\s+/).filter(Boolean);
	if (terms.length === 0) return true;
	const haystack = label.toLowerCase();
	return terms.every((term) => haystack.includes(term));
}

export function filterSelectOptions(options: SelectOption[], query: string): SelectOption[] {
	return options.filter((option) => matchesSelectQuery(option.label, query));
}

function snapshot(select: HTMLSelectElement): { placeholder: SelectOption | null; options: SelectOption[] } {
	const all = Array.from(select.options).map((option) => ({
		value: option.value,
		label: (option.textContent ?? '').trim(),
	}));
	// A valueless first entry is the "Choose a …" prompt: it must survive every filter.
	const first = all[0];
	const placeholder = first !== undefined && first.value === '' ? first : null;
	return { placeholder, options: placeholder ? all.slice(1) : all };
}

export function attachSelectFilter(input: HTMLInputElement, select: HTMLSelectElement): void {
	const { placeholder, options } = snapshot(select);

	const render = (): void => {
		const previous = select.value;
		const matches = filterSelectOptions(options, input.value);

		select.replaceChildren(
			...(placeholder ? [new Option(placeholder.label, placeholder.value)] : []),
			...matches.map((option) => new Option(option.label, option.value)),
		);

		// Narrowing to a single ride is the point of typing — pick it. Otherwise keep the prior choice
		// alive if it still matches, so filtering does not silently clear a made selection.
		if (matches.length === 1) select.value = must(matches[0], 'length === 1 (guarded above)').value;
		else if (matches.some((option) => option.value === previous)) select.value = previous;
	};

	input.addEventListener('input', render);
}
