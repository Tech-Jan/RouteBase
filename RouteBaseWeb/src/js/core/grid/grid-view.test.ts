import { describe, it, expect, afterEach } from 'vitest';
import { GridView, escapeHtml } from './grid-view';
import type { GridColumn, GridDataResponse, GridDataSource, GridQuery } from './grid-types';
import { must } from '../invariant';

interface Row {
	id: number;
	name: string;
	type: string;
	hr: number | null;
}

const rows: Row[] = [
	{ id: 1, name: 'Morning Run', type: 'Run', hr: 120 },
	{ id: 2, name: 'Evening Ride', type: 'Bike', hr: 95 },
];

/** In-memory fake that records every query it served. */
class FakeSource implements GridDataSource<Row> {
	queries: GridQuery[] = [];
	fetch(query: GridQuery): Promise<GridDataResponse<Row>> {
		this.queries.push(query);
		return Promise.resolve({ rows, filteredIds: rows.map((r) => r.id), total: rows.length });
	}
}

function columns(): GridColumn<Row>[] {
	return [
		{ key: 'name', header: 'Name', sortable: true, filter: { type: 'text' }, getValue: (r) => r.name, formatter: (r) => escapeHtml(r.name) },
		{ key: 'type', header: 'Type', filter: { type: 'combo', options: ['Run', 'Bike'] }, getValue: (r) => r.type, formatter: (r) => escapeHtml(r.type) },
		{ key: 'hr', header: 'HR', optionalKey: 'hr', filter: { type: 'numberrange' }, getValue: (r) => r.hr, formatter: (r) => String(r.hr ?? '') },
	];
}

const flush = (): Promise<void> => new Promise((r) => setTimeout(r, 0));

interface Ctx {
	mount: HTMLElement;
	source: FakeSource;
	grid: GridView<Row>;
}

async function makeGrid(visibleKeys?: Set<string>): Promise<Ctx> {
	const mount = document.createElement('div');
	document.body.appendChild(mount);
	const source = new FakeSource();
	const grid = new GridView<Row>({
		mount,
		columns: columns(),
		dataSource: source,
		defaultSort: { column: 'name', direction: 'asc' },
		getRowId: (r) => r.id,
		visibleKeys,
	});
	await flush(); // initial refresh
	return { mount, source, grid };
}

afterEach(() => {
	document.body.innerHTML = '';
});

describe('GridView.setFilter (GR-03)', () => {
	it('updates the filter-row text input to the programmatic value', async () => {
		const { mount, grid } = await makeGrid();
		grid.setFilter('name', { text: 'abc' });
		const input = mount.querySelector<HTMLInputElement>('.grid-filter-row input[aria-label="Filter Name"]');
		expect(input?.value).toBe('abc');
		await flush();
		expect(grid.getFilter('name')).toEqual({ text: 'abc' });
	});

	it('updates the combo popup checkboxes so a later user edit cannot clobber the restored value', async () => {
		const { mount, grid } = await makeGrid();
		grid.setFilter('type', { text: '', selected: ['Bike'] });
		const boxes = [...mount.querySelectorAll<HTMLInputElement>('.grid-filter-combo input[type=checkbox]')];
		expect(boxes.map((b) => [b.value, b.checked])).toEqual([
			['Run', false],
			['Bike', true],
		]);
		// Simulate the user typing in the combo's text box: the rebuilt control must carry the
		// restored selection forward instead of the stale pre-setFilter snapshot.
		const text = mount.querySelector<HTMLInputElement>('.grid-filter-combo input[type=text]')!;
		text.value = 'ru';
		text.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
		await flush();
		expect(grid.getFilter('type')).toEqual({ text: 'ru', selected: ['Bike'] });
	});
});

describe('GridView.setVisibleKeys (GR-06)', () => {
	it('clears the filter of a column hidden via the picker', async () => {
		const { grid, source } = await makeGrid(new Set(['hr']));
		grid.setFilter('hr', { expr: '>100' });
		await flush();
		expect(grid.getFilter('hr')).toEqual({ expr: '>100' });

		grid.setVisibleKeys(new Set());
		await flush();
		expect(grid.getFilter('hr')).toBeUndefined();
		const last = must(source.queries.at(-1), 'at least one query');
		expect(last.filters.hr).toBeUndefined();
	});

	it('keeps filters of columns that stay visible', async () => {
		const { grid } = await makeGrid(new Set(['hr']));
		grid.setFilter('name', { text: 'run' });
		grid.setVisibleKeys(new Set());
		await flush();
		expect(grid.getFilter('name')).toEqual({ text: 'run' });
	});
});

describe('GridView header re-render (GR-02)', () => {
	it('closes an open filter popup before wiping the header', async () => {
		const { mount, grid } = await makeGrid();
		const details = mount.querySelector<HTMLDetailsElement>('thead details.grid-filter-multi')!;
		details.open = true;

		grid.setVisibleKeys(new Set(['hr'])); // triggers renderHeader → close-before-wipe
		expect(details.open).toBe(false);
	});
});

describe('GridView rendering basics', () => {
	it('renders one row per data row with escaped formatter output', async () => {
		const { mount } = await makeGrid();
		const cells = [...mount.querySelectorAll('tbody tr td:first-child')].map((td) => td.textContent);
		expect(cells).toEqual(['Morning Run', 'Evening Ride']);
	});

	it('hides optional columns unless their key is visible', async () => {
		const { mount } = await makeGrid();
		// Header cells also contain the reorder grip glyph — assert by containment.
		const headers = [...mount.querySelectorAll('thead tr:first-child th')].map((th) => th.textContent ?? '');
		expect(headers.some((h) => h.includes('Name'))).toBe(true);
		expect(headers.some((h) => h.includes('Type'))).toBe(true);
		expect(headers.join(' ')).not.toContain('HR');
	});
});

// CR2-01 pins: lock the grid escaper's contract (null-safe, all-5-char, attribute-safe) via the
// `./grid-view` re-export point before core/html adopts this body verbatim (C2). Must survive C2 unchanged.
describe('GridView escapeHtml contract (CR2-01 pins)', () => {
	it('is null/undefined-safe', () => {
		expect(escapeHtml(null)).toBe('');
		expect(escapeHtml(undefined)).toBe('');
	});

	it('escapes all five reserved chars, including the single quote', () => {
		expect(escapeHtml(`&<>"'`)).toBe('&amp;&lt;&gt;&quot;&#39;');
	});

	it('renders a selection column title containing a double quote as a single intact attribute', async () => {
		const mount = document.createElement('div');
		document.body.appendChild(mount);
		const source = new FakeSource();
		new GridView<Row>({
			mount,
			columns: [
				...columns(),
				{
					key: 'sel',
					header: '',
					selection: { key: 'sel', title: 'Select "the" row' },
				},
			],
			dataSource: source,
			defaultSort: { column: 'name', direction: 'asc' },
			getRowId: (r) => r.id,
		});
		await flush();

		const boxes = [...mount.querySelectorAll<HTMLInputElement>('tbody .grid-sel-cb')];
		expect(boxes).toHaveLength(rows.length);
		for (const box of boxes) {
			expect(box.title).toBe('Select "the" row');
		}
		// The quote round-tripped through the attribute, not into a sibling node.
		expect(mount.querySelectorAll('tbody tr').length).toBe(rows.length);

		document.body.removeChild(mount);
	});
});

describe('GridView.clearSelection', () => {
	it('empties the set (all ids), re-renders unchecked, and fires onSelectionChange with an empty set', async () => {
		const mount = document.createElement('div');
		document.body.appendChild(mount);
		const changes: Array<{ key: string; ids: number[] }> = [];
		const grid = new GridView<Row>({
			mount,
			columns: [...columns(), { key: 'sel', header: '', selection: { key: 'sel' } }],
			dataSource: new FakeSource(),
			defaultSort: { column: 'name', direction: 'asc' },
			getRowId: (r) => r.id,
			initialSelection: { sel: [1, 2] },
			onSelectionChange: (key, selectedIds) => changes.push({ key, ids: [...selectedIds] }),
		});
		await flush();

		const boxes = () => [...mount.querySelectorAll<HTMLInputElement>('tbody .grid-sel-cb')];
		expect(boxes().every((b) => b.checked)).toBe(true); // seeded selection reflected

		grid.clearSelection('sel');

		expect(boxes().some((b) => b.checked)).toBe(false); // re-rendered unchecked
		expect(changes).toEqual([{ key: 'sel', ids: [] }]); // notified once, empty

		document.body.removeChild(mount);
	});
});
