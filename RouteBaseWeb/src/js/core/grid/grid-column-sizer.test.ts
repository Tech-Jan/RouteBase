import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { GridColumnSizer } from './grid-column-sizer';
import { must } from '../invariant';
import { devicePrefKeys, getJson, listDevicePrefKeys } from '../device-prefs';
import type { GridColumn } from './grid-types';

type Row = Record<string, never>;

const cols: GridColumn<Row>[] = [
	{ key: 'a', header: 'A', width: 100 },
	{ key: 'b', header: 'B', width: 80 },
	{ key: 'c', header: 'C', width: 60 },
];

function setup(persistKey?: string): { table: HTMLTableElement; sizer: GridColumnSizer<Row>; ths: HTMLElement[] } {
	const table = document.createElement('table');
	const tr = document.createElement('tr');
	const ths = cols.map(() => {
		const th = document.createElement('th');
		tr.appendChild(th);
		return th;
	});
	const thead = document.createElement('thead');
	thead.appendChild(tr);
	table.appendChild(thead);
	document.body.appendChild(table);
	const sizer = new GridColumnSizer<Row>(table, persistKey);
	sizer.ensureWidths(cols, ths);
	return { table, sizer, ths };
}

function drag(sizer: GridColumnSizer<Row>, th: HTMLElement, col: GridColumn<Row>, index: number, dx: number, end: 'pointerup' | 'pointercancel'): void {
	sizer.attachHandle(th, col, index);
	const handle = th.querySelector('.grid-col-resize')!;
	handle.dispatchEvent(new MouseEvent('pointerdown', { clientX: 0, bubbles: true }));
	document.dispatchEvent(new MouseEvent('pointermove', { clientX: dx }));
	document.dispatchEvent(new Event(end));
}

beforeEach(() => {
	localStorage.clear();
});

afterEach(() => {
	document.body.innerHTML = '';
	localStorage.clear();
});

describe('GridColumnSizer persistence (GR-05)', () => {
	it('persists only user-dragged widths, not seeded/measured ones', () => {
		const { sizer, ths } = setup('t1');
		drag(sizer, must(ths[1], 'th 1'), must(cols[1], 'col 1'), 1, 50, 'pointerup'); // b: 80 → 130
		const stored = getJson<Record<string, number>>(devicePrefKeys.gridColumnWidths('t1'), {});
		expect(stored).toEqual({ b: 130 });
	});

	it('reloads persisted widths as user-set (survive clearMeasuredWidths)', () => {
		const first = setup('t2');
		drag(first.sizer, must(first.ths[1], 'th 1'), must(cols[1], 'col 1'), 1, 50, 'pointerup');
		document.body.innerHTML = '';

		const { table, sizer } = setup('t2');
		sizer.clearMeasuredWidths(); // must keep the persisted user width
		sizer.syncColumns(cols);
		const colEls = [...table.querySelectorAll('col')];
		expect(must(colEls[1], 'col 1').style.width).toBe('130px');
	});
});

describe('GridColumnSizer resize lifecycle (GR-04)', () => {
	it('applies the drag to the col element and locks fixed layout', () => {
		const { table, sizer, ths } = setup();
		drag(sizer, must(ths[0], 'th 0'), must(cols[0], 'col 0'), 0, 30, 'pointerup');
		expect(table.style.tableLayout).toBe('fixed');
		expect(must([...table.querySelectorAll('col')][0], 'col 0').style.width).toBe('130px');
	});

	it('pointercancel ends the resize: body class removed and later moves ignored', () => {
		const { table, sizer, ths } = setup();
		drag(sizer, must(ths[0], 'th 0'), must(cols[0], 'col 0'), 0, 30, 'pointercancel');
		expect(document.body.classList.contains('grid-resizing')).toBe(false);

		document.dispatchEvent(new MouseEvent('pointermove', { clientX: 500 }));
		expect(must([...table.querySelectorAll('col')][0], 'col 0').style.width).toBe('130px'); // unchanged after cancel
	});

	it('enforces the minimum width', () => {
		const { table, sizer, ths } = setup();
		drag(sizer, must(ths[2], 'th 2'), must(cols[2], 'col 2'), 2, -500, 'pointerup');
		expect(must([...table.querySelectorAll('col')][2], 'col 2').style.width).toBe('48px');
	});

	it('reset forgets widths and clears persistence', () => {
		const { table, sizer, ths } = setup('t3');
		drag(sizer, must(ths[0], 'th 0'), must(cols[0], 'col 0'), 0, 30, 'pointerup');
		sizer.reset();
		expect(listDevicePrefKeys()).not.toContain(devicePrefKeys.gridColumnWidths('t3'));
		expect(table.style.tableLayout).toBe('auto');
		expect([...table.querySelectorAll('col')].every((c) => c.style.width === '')).toBe(true);
	});
});
