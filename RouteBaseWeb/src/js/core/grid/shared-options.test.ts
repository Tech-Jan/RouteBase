// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { SHARED_FILTER_OPTIONS, sharedCellText, sharedFilterValue } from './shared-options';
import { InMemoryGridDataSource } from './grid-data-source';
import { matchesSet } from './grid-predicates';
import type { GridColumn, GridQuery } from './grid-types';

interface Row {
	id: number;
	isShared: boolean;
	wasShared: boolean;
}

const ROWS: Row[] = [
	{ id: 1, isShared: true, wasShared: false },
	{ id: 2, isShared: false, wasShared: false },
	{ id: 3, isShared: true, wasShared: false },
	{ id: 4, isShared: false, wasShared: true },
];

// The column exactly as the three grids declare it.
const COLUMNS: GridColumn<Row>[] = [
	{
		key: 'shared',
		header: 'Shared',
		sortable: true,
		filter: { type: 'multiselect', options: SHARED_FILTER_OPTIONS },
		getValue: (r) => sharedFilterValue(r.isShared, r.wasShared),
		formatter: (r) => sharedCellText(r.isShared, r.wasShared),
	},
];

function query(selected?: string[]): GridQuery {
	return {
		filters: selected ? { shared: { selected } } : {},
		sort: { column: 'shared', direction: 'asc' },
		sorts: [],
		page: 1,
		pageSize: 50,
	};
}

async function filteredIds(selected?: string[]): Promise<number[]> {
	const source = new InMemoryGridDataSource<Row>(ROWS, { getId: (r) => r.id, columns: COLUMNS });
	const res = await source.fetch(query(selected));
	return res.rows.map((r) => r.id).sort((a, b) => a - b);
}

describe('shared-options', () => {
	it('emits exactly the three values the filter offers', () => {
		// THE property this file exists for: the row's value and the option's value are produced in one
		// place. If they ever drift, the filter matches nothing and does so silently — no error, just an
		// empty list, which reads as "you have no shared items".
		const optionValues = SHARED_FILTER_OPTIONS.map((o) => (typeof o === 'string' ? o : o.value));

		expect(optionValues).toContain(sharedFilterValue(true, false));
		expect(optionValues).toContain(sharedFilterValue(false, true));
		expect(optionValues).toContain(sharedFilterValue(false, false));
		expect(new Set(optionValues).size).toBe(3);
		expect(optionValues).toHaveLength(3);
	});

	it('treats a live link as Shared whatever the history says', () => {
		// The two flags are mutually exclusive server-side, but the tie-break is pinned here rather than
		// left to whichever grid asks first.
		expect(sharedFilterValue(true, true)).toBe('shared');
		expect(sharedCellText(true, true)).toBe('Shared');
	});

	it('leaves the cell blank for never-shared rows', () => {
		expect(sharedCellText(false, false)).toBe('');
		expect(sharedCellText(false, true)).toBe('Previously shared');
	});

	it('matches through the grid predicate the multiselect filter actually uses', () => {
		expect(matchesSet(sharedFilterValue(true, false), ['shared'])).toBe(true);
		expect(matchesSet(sharedFilterValue(false, false), ['shared'])).toBe(false);
		expect(matchesSet(sharedFilterValue(false, false), ['notshared'])).toBe(true);
		expect(matchesSet(sharedFilterValue(false, true), ['wasshared'])).toBe(true);
		expect(matchesSet(sharedFilterValue(false, true), ['notshared'])).toBe(false);
	});
});

describe('the Shared grid column', () => {
	it('narrows to the rows with a live link', async () => {
		expect(await filteredIds(['shared'])).toEqual([1, 3]);
	});

	it('narrows to the rows whose sharing has ended', async () => {
		// Row 4 kept its view count after the link died; it must be findable without being confused for
		// either a live share or a never-shared row.
		expect(await filteredIds(['wasshared'])).toEqual([4]);
	});

	it('narrows to the never-shared complement', async () => {
		expect(await filteredIds(['notshared'])).toEqual([2]);
	});

	it('shows everything when nothing is checked, and when all are', async () => {
		// An empty multiselect must impose no constraint — otherwise opening the filter and closing it
		// again would silently empty the page.
		expect(await filteredIds()).toEqual([1, 2, 3, 4]);
		expect(await filteredIds([])).toEqual([1, 2, 3, 4]);
		expect(await filteredIds(['shared', 'wasshared', 'notshared'])).toEqual([1, 2, 3, 4]);
	});
});
