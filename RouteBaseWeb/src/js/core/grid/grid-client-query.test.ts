// @vitest-environment node
import { describe, it, expect } from 'vitest';
import { canSortClientSide, compareValues, filterRowsBySchema, sortRowsBySchema } from './grid-client-query';
import type { GridColumn } from './grid-types';

interface Row {
	id: number;
	name: string | null;
	km: number | null;
	type: string;
}

const columns: GridColumn<Row>[] = [
	{ key: 'name', header: 'Name', filter: { type: 'text' }, getValue: (r) => r.name },
	{ key: 'km', header: 'Km', filter: { type: 'numberrange' }, getValue: (r) => r.km },
	{ key: 'type', header: 'Type', filter: { type: 'multiselect' }, getValue: (r) => r.type },
	{ key: 'special', header: 'Special', filter: { type: 'text' }, clientFilter: (r) => r.id === 1 },
	{ key: 'noValue', header: 'NoValue', filter: { type: 'text' } }, // filterable but not evaluable client-side
];

const rows: Row[] = [
	{ id: 1, name: 'Alpha', km: 5, type: 'Run' },
	{ id: 2, name: 'beta', km: null, type: 'Bike' },
	{ id: 3, name: null, km: 12, type: 'Run' },
];

describe('compareValues', () => {
	it('sorts numbers numerically and strings case-insensitively', () => {
		expect(compareValues(2, 10)).toBeLessThan(0);
		expect(compareValues('beta', 'Alpha')).toBeGreaterThan(0);
	});
	it('sorts nulls last regardless of direction (consumer applies dir to non-null pairs only)', () => {
		expect(compareValues(null, 1)).toBeGreaterThan(0);
		expect(compareValues(1, null)).toBeLessThan(0);
		expect(compareValues(null, null)).toBe(0);
	});
});

describe('sortRowsBySchema', () => {
	it('sorts by getValue with direction, nulls last, stable tiebreak', () => {
		const sorted = sortRowsBySchema(columns, rows, [{ column: 'km', direction: 'desc' }]);
		expect(sorted.map((r) => r.id)).toEqual([3, 1, 2]); // 12, 5, null-last
	});
	it('keeps nulls last on ascending sorts too (GR-11)', () => {
		const sorted = sortRowsBySchema(columns, rows, [{ column: 'km', direction: 'asc' }]);
		expect(sorted.map((r) => r.id)).toEqual([1, 3, 2]); // 5, 12, null-last
	});
	it('applies multi-key sorts primary-first', () => {
		const data: Row[] = [
			{ id: 1, name: 'a', km: 1, type: 'Run' },
			{ id: 2, name: 'b', km: 1, type: 'Run' },
			{ id: 3, name: 'a', km: 2, type: 'Run' },
		];
		const sorted = sortRowsBySchema(columns, data, [
			{ column: 'name', direction: 'asc' },
			{ column: 'km', direction: 'desc' },
		]);
		expect(sorted.map((r) => r.id)).toEqual([3, 1, 2]);
	});
	it('returns input order when no sort level is evaluable', () => {
		const sorted = sortRowsBySchema(columns, rows, [{ column: 'noValue', direction: 'asc' }]);
		expect(sorted.map((r) => r.id)).toEqual([1, 2, 3]);
	});
});

describe('canSortClientSide', () => {
	it('requires getValue on every sort level', () => {
		expect(canSortClientSide(columns, [{ column: 'km', direction: 'asc' }])).toBe(true);
		expect(
			canSortClientSide(columns, [
				{ column: 'km', direction: 'asc' },
				{ column: 'noValue', direction: 'asc' },
			]),
		).toBe(false);
	});
});

describe('filterRowsBySchema', () => {
	it('ANDs active filters across columns', () => {
		const out = filterRowsBySchema(columns, rows, {
			type: { selected: ['Run'] },
			km: { expr: '>6' },
		});
		expect(out.map((r) => r.id)).toEqual([3]);
	});
	it('clientFilter wins over the derived predicate', () => {
		const out = filterRowsBySchema(columns, rows, { special: { text: 'ignored-by-clientFilter' } });
		expect(out.map((r) => r.id)).toEqual([1]);
	});
	it('ignores filters that cannot be evaluated client-side and empty values', () => {
		const out = filterRowsBySchema(columns, rows, { noValue: { text: 'x' }, name: { text: '  ' } });
		expect(out).toHaveLength(3);
	});
	it('null values never match text/set/range filters', () => {
		expect(filterRowsBySchema(columns, rows, { name: { text: '*a*' } }).map((r) => r.id)).toEqual([1, 2]);
		expect(filterRowsBySchema(columns, rows, { km: { expr: '0-100' } }).map((r) => r.id)).toEqual([1, 3]);
	});
});
