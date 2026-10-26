// @vitest-environment node
import { describe, it, expect, vi } from 'vitest';
import { installStyleFilterSanitizer, sanitizeNumericFilterExpr, sanitizeStyleFilters } from './style-filter-sanitizer';

// The three real Liberty filter shapes that throw "Expected value to be of type number, but found null"
// when a feature's rank/admin_level is null. See style-filter-sanitizer.ts header.

describe('sanitizeNumericFilterExpr — real Liberty shapes', () => {
	it('wraps both rank operands in a poi_r* filter (>= and <)', () => {
		const poiR1 = ['all',
			['match', ['geometry-type'], ['MultiPoint', 'Point'], true, false],
			['>=', ['get', 'rank'], 1],
			['<', ['get', 'rank'], 7]];
		const { filter, changed } = sanitizeNumericFilterExpr(poiR1);
		expect(changed).toBe(true);
		expect(filter).toEqual(['all',
			['match', ['geometry-type'], ['MultiPoint', 'Point'], true, false],
			['>=', ['to-number', ['get', 'rank'], -1], 1],
			['<', ['to-number', ['get', 'rank'], -1], 7]]);
	});

	it('wraps admin_level in boundary_3 but leaves != / ! / has clauses untouched', () => {
		const boundary3 = ['all',
			['>=', ['get', 'admin_level'], 3],
			['<=', ['get', 'admin_level'], 6],
			['!=', ['get', 'maritime'], 1],
			['!=', ['get', 'disputed'], 1],
			['!', ['has', 'claimed_by']]];
		const { filter, changed } = sanitizeNumericFilterExpr(boundary3);
		expect(changed).toBe(true);
		expect(filter).toEqual(['all',
			['>=', ['to-number', ['get', 'admin_level'], -1], 3],
			['<=', ['to-number', ['get', 'admin_level'], -1], 6],
			['!=', ['get', 'maritime'], 1],   // != is null-tolerant → untouched
			['!=', ['get', 'disputed'], 1],
			['!', ['has', 'claimed_by']]]);
	});

	it('wraps only the rank comparison in label_country_3, not the == class match', () => {
		const labelCountry3 = ['all', ['==', ['get', 'class'], 'country'], ['>=', ['get', 'rank'], 3]];
		const { filter } = sanitizeNumericFilterExpr(labelCountry3);
		expect(filter).toEqual(['all',
			['==', ['get', 'class'], 'country'],
			['>=', ['to-number', ['get', 'rank'], -1], 3]]);
	});
});

describe('sanitizeNumericFilterExpr — pass-through (no change)', () => {
	it('leaves a purely null-tolerant filter untouched and returns the same reference', () => {
		const f = ['all', ['==', ['get', 'class'], 'country'], ['has', 'name'], ['in', ['get', 'id'], ['literal', [1, 2]]]];
		const res = sanitizeNumericFilterExpr(f);
		expect(res.changed).toBe(false);
		expect(res.filter).toBe(f); // identity preserved when nothing changed
	});

	it('does not touch == / != even against numeric literals (those operators tolerate null)', () => {
		const f = ['==', ['get', '_onSeg'], 1];
		const res = sanitizeNumericFilterExpr(f);
		expect(res.changed).toBe(false);
		expect(res.filter).toBe(f);
	});

	it('ignores non-array and empty inputs', () => {
		expect(sanitizeNumericFilterExpr(undefined)).toEqual({ filter: undefined, changed: false });
		expect(sanitizeNumericFilterExpr(true)).toEqual({ filter: true, changed: false });
		expect(sanitizeNumericFilterExpr([])).toEqual({ filter: [], changed: false });
	});

	it('does not transform legacy [op, "key", val] filters (bare-string property, not our basemaps)', () => {
		const legacy = ['>=', 'admin_level', 3];
		const res = sanitizeNumericFilterExpr(legacy);
		expect(res.changed).toBe(false);
		expect(res.filter).toBe(legacy);
	});
});

describe('sanitizeNumericFilterExpr — operand symmetry & nesting', () => {
	it('wraps a get on either side of the comparison', () => {
		const { filter } = sanitizeNumericFilterExpr(['<', 20, ['get', 'rank']]);
		expect(filter).toEqual(['<', 20, ['to-number', ['get', 'rank'], -1]]);
	});

	it('wraps both operands when two gets are compared', () => {
		const { filter } = sanitizeNumericFilterExpr(['<', ['get', 'a'], ['get', 'b']]);
		expect(filter).toEqual(['<', ['to-number', ['get', 'a'], -1], ['to-number', ['get', 'b'], -1]]);
	});

	it('recurses through any/all wrappers', () => {
		const { filter } = sanitizeNumericFilterExpr(['any', ['all', ['>', ['get', 'rank'], 5]]]);
		expect(filter).toEqual(['any', ['all', ['>', ['to-number', ['get', 'rank'], -1], 5]]]);
	});
});

describe('sanitizeStyleFilters', () => {
	it('rewrites each layer filter in place and leaves filterless layers alone', () => {
		const style = {
			layers: [
				{ id: 'background', type: 'background' },                       // no filter
				{ id: 'poi_r20', type: 'symbol', filter: ['>=', ['get', 'rank'], 20] },
				{ id: 'roads', type: 'line', filter: ['==', ['get', 'class'], 'motorway'] }, // untouched
			],
		};
		sanitizeStyleFilters(style);
		expect(style.layers[0]).toEqual({ id: 'background', type: 'background' });
		expect((style.layers[1] as { filter: unknown }).filter).toEqual(['>=', ['to-number', ['get', 'rank'], -1], 20]);
		expect((style.layers[2] as { filter: unknown }).filter).toEqual(['==', ['get', 'class'], 'motorway']);
	});

	it('is a no-op for a style with no layers', () => {
		expect(() => sanitizeStyleFilters({})).not.toThrow();
		expect(() => sanitizeStyleFilters(null)).not.toThrow();
		expect(() => sanitizeStyleFilters(undefined)).not.toThrow();
	});
});

describe('installStyleFilterSanitizer — the style.load production wiring', () => {
	function fakeMap(layers: unknown[]) {
		const handlers: Record<string, () => void> = {};
		const setFilter = vi.fn();
		const map = {
			on: (event: string, handler: () => void) => { handlers[event] = handler; },
			getStyle: () => ({ layers }),
			setFilter,
		};
		return { map, setFilter, fireStyleLoad: () => handlers['style.load']!() };
	}

	// This is the constructor/raw-URL path that sanitizeStyleFilters can't reach — the only place setFilter
	// is actually called on the live map. It was previously untested end-to-end.
	it('on style.load, calls setFilter with the sanitized filter for CHANGED layers only', () => {
		const { map, setFilter, fireStyleLoad } = fakeMap([
			{ id: 'background', type: 'background' },                                    // no filter → skipped
			{ id: 'poi_r20', type: 'symbol', filter: ['>=', ['get', 'rank'], 20] },      // null-intolerant → rewritten
			{ id: 'roads', type: 'line', filter: ['==', ['get', 'class'], 'motorway'] }, // null-tolerant → untouched
		]);

		installStyleFilterSanitizer(map as unknown as Parameters<typeof installStyleFilterSanitizer>[0]);
		expect(setFilter).not.toHaveBeenCalled(); // wired to the event, not eager — nothing until the style loads

		fireStyleLoad();

		expect(setFilter).toHaveBeenCalledTimes(1); // only the one changed layer
		expect(setFilter).toHaveBeenCalledWith('poi_r20', ['>=', ['to-number', ['get', 'rank'], -1], 20]);
	});

	it('swallows a setFilter failure instead of breaking the rest of the style load', () => {
		const { map, setFilter, fireStyleLoad } = fakeMap([
			{ id: 'poi_r20', type: 'symbol', filter: ['>=', ['get', 'rank'], 20] },
		]);
		setFilter.mockImplementation(() => { throw new Error('bad filter'); });
		const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});

		installStyleFilterSanitizer(map as unknown as Parameters<typeof installStyleFilterSanitizer>[0]);
		expect(() => fireStyleLoad()).not.toThrow();
		expect(warn).toHaveBeenCalled();
		warn.mockRestore();
	});
});
