// @vitest-environment node
import { describe, it, expect } from 'vitest';
import { clearGenericFilterParams, legacyBoundsToExpr, parseFilters, serializeFilters } from './grid-url';
import { parseRanges } from './grid-filter-parse';
import type { GridColumn, GridFilterValues } from './grid-types';

type Row = Record<string, unknown>;

const columns: GridColumn<Row>[] = [
	{ key: 'name', header: 'Name', filter: { type: 'text' } },
	{ key: 'type', header: 'Type', filter: { type: 'combo', options: ['Run', 'Bike'] } },
	{ key: 'owner', header: 'Owner', filter: { type: 'multiselect', options: ['a', 'b'] } },
	{ key: 'distance', header: 'Distance', filter: { type: 'numberrange' } },
	{ key: 'date', header: 'Date', filter: { type: 'daterange' } },
	{ key: 'actions', header: '' }, // not filterable — must be ignored
];

describe('serializeFilters / parseFilters round-trip', () => {
	it('round-trips every filter type through the generic f.<key>.* format', () => {
		const filters: GridFilterValues = {
			name: { text: '*run*' },
			type: { text: 'bi*', selected: ['Bike'] },
			owner: { selected: ['a', 'b'] },
			distance: { expr: '3-6,>20' },
			date: { from: '2026-07-01', to: '2026-07-08' },
		};
		const params = new URLSearchParams();
		serializeFilters(columns, filters, params);
		expect(params.get('f.name.text')).toBe('*run*');
		expect(params.getAll('f.owner.in')).toEqual(['a', 'b']);
		expect(params.get('f.distance.range')).toBe('3-6,>20');

		expect(parseFilters(columns, params)).toEqual(filters);
	});

	it('skips empty values and unknown/non-filterable columns', () => {
		const params = new URLSearchParams();
		serializeFilters(columns, { name: { text: '  ' }, actions: { text: 'x' }, ghost: { text: 'y' } }, params);
		expect([...params.keys()]).toEqual([]);
		expect(parseFilters(columns, new URLSearchParams('f.actions.text=x&f.ghost.text=y'))).toEqual({});
	});

	it('honours a per-column codec override for write and read', () => {
		const params = new URLSearchParams();
		const overrides = {
			date: {
				write: (v: { from?: string }, p: URLSearchParams) => p.set('dateFrom', v.from ?? ''),
				read: (p: URLSearchParams) => (p.get('dateFrom') ? { from: p.get('dateFrom')!, to: '' } : undefined),
			},
		};
		serializeFilters(columns, { date: { from: '2026-07-01', to: '' } }, params, overrides);
		expect(params.get('dateFrom')).toBe('2026-07-01');
		expect(params.get('f.date.from')).toBeNull();
		expect(parseFilters(columns, params, overrides)).toEqual({ date: { from: '2026-07-01', to: '' } });
	});

	it('clearGenericFilterParams removes every owned suffix', () => {
		const params = new URLSearchParams(
			'f.name.text=x&f.owner.in=a&f.owner.in=b&f.distance.range=1-2&f.distance.min=1&f.date.from=2026-07-01&other=keep',
		);
		clearGenericFilterParams(columns, params);
		expect([...params.keys()]).toEqual(['other']);
	});
});

describe('legacy two-bound params (GR-09)', () => {
	it('legacyBoundsToExpr uses operator spellings for single bounds', () => {
		expect(legacyBoundsToExpr('5', '20')).toBe('5-20');
		expect(legacyBoundsToExpr('5', null)).toBe('>=5');
		expect(legacyBoundsToExpr(null, '20')).toBe('<=20');
		expect(legacyBoundsToExpr(null, null)).toBeUndefined();
	});

	it('a negative single bound survives the expr round-trip (old `-N` spelling was unparseable)', () => {
		const expr = legacyBoundsToExpr(null, '-10')!;
		expect(parseRanges(expr)).toEqual([{ min: null, max: -10 }]);
		const exprMin = legacyBoundsToExpr('-10', null)!;
		expect(parseRanges(exprMin)).toEqual([{ min: -10, max: null }]);
	});

	it('parseFilters synthesizes an expr from legacy f.<key>.min/.max when no range param exists', () => {
		expect(parseFilters(columns, new URLSearchParams('f.distance.min=5&f.distance.max=20'))).toEqual({
			distance: { expr: '5-20' },
		});
		expect(parseFilters(columns, new URLSearchParams('f.distance.max=-10'))).toEqual({
			distance: { expr: '<=-10' },
		});
		// An explicit range param wins over legacy bounds.
		expect(
			parseFilters(columns, new URLSearchParams('f.distance.range=1-2&f.distance.min=5')),
		).toEqual({ distance: { expr: '1-2' } });
	});
});
