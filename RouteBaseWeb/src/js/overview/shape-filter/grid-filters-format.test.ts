// @vitest-environment node
import { describe, it, expect } from 'vitest';
import { formatGridFilters } from './grid-filters-format';

describe('formatGridFilters', () => {
	it('renders legacy min/max bounds (SP-09)', () => {
		expect(formatGridFilters({ distance: { min: '10', max: '20' } })).toBe('distance 10–20 km');
	});

	it('renders an open-ended min/max bound', () => {
		expect(formatGridFilters({ ascent: { min: '100' } })).toBe('ascent 100–… m');
	});
});
