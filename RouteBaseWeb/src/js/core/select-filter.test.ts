import { describe, expect, it } from 'vitest';
import { attachSelectFilter, filterSelectOptions, matchesSelectQuery } from './select-filter';

const rides = [
	{ value: '1', label: 'Ski to Sea course (7 legs) — 142.2 km' },
	{ value: '2', label: 'Morning ride — 42.0 km' },
	{ value: '3', label: 'Sea to Sky — 88.5 km' },
];

describe('matchesSelectQuery', () => {
	it('matches everything on an empty or whitespace query', () => {
		expect(matchesSelectQuery('Morning ride', '')).toBe(true);
		expect(matchesSelectQuery('Morning ride', '   ')).toBe(true);
	});

	it('ignores case', () => {
		expect(matchesSelectQuery('Ski to Sea course', 'SKI')).toBe(true);
	});

	it('requires every term, in any order', () => {
		expect(matchesSelectQuery('Ski to Sea course', 'sea ski')).toBe(true);
		expect(matchesSelectQuery('Ski to Sea course', 'ski alps')).toBe(false);
	});
});

describe('filterSelectOptions', () => {
	it('keeps only matching options', () => {
		expect(filterSelectOptions(rides, 'ski').map((r) => r.value)).toEqual(['1']);
	});

	it('matches across every option that contains the term', () => {
		expect(filterSelectOptions(rides, 'sea').map((r) => r.value)).toEqual(['1', '3']);
	});
});

function buildSelect(): { input: HTMLInputElement; select: HTMLSelectElement } {
	const select = document.createElement('select');
	select.append(new Option('Choose a ride…', ''));
	for (const ride of rides) select.append(new Option(ride.label, ride.value));
	const input = document.createElement('input');
	document.body.replaceChildren(input, select);
	return { input, select };
}

function type(input: HTMLInputElement, query: string): void {
	input.value = query;
	input.dispatchEvent(new Event('input'));
}

describe('attachSelectFilter', () => {
	it('keeps the placeholder and drops non-matching options', () => {
		const { input, select } = buildSelect();
		attachSelectFilter(input, select);

		type(input, 'sea');

		expect(Array.from(select.options).map((o) => o.value)).toEqual(['', '1', '3']);
	});

	it('restores the full list when the query is cleared', () => {
		const { input, select } = buildSelect();
		attachSelectFilter(input, select);

		type(input, 'ski');
		type(input, '');

		expect(Array.from(select.options)).toHaveLength(4);
	});

	it('auto-selects the sole match', () => {
		const { input, select } = buildSelect();
		attachSelectFilter(input, select);

		type(input, 'ski');

		expect(select.value).toBe('1');
	});

	it('keeps an existing selection that still matches', () => {
		const { input, select } = buildSelect();
		attachSelectFilter(input, select);
		select.value = '3';

		type(input, 'sea');

		expect(select.value).toBe('3');
	});

	it('falls back to the placeholder when the selection is filtered out', () => {
		const { input, select } = buildSelect();
		attachSelectFilter(input, select);
		select.value = '2';

		type(input, 'sea');

		expect(select.value).toBe('');
	});
});
