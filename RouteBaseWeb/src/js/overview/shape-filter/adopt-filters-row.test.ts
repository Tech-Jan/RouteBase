import { describe, it, expect, vi } from 'vitest';
import { AdoptFiltersRow } from './adopt-filters-row';
import { registerGridFilterApi } from '../overview-grid-bridge';

/**
 * The "Merge table ↔ shapes" toggle. restrictsTable() is the gate the orchestrator reads to (de)apply the
 * shape→table restriction — true only in live mode, deliberately NOT in stored (edit) mode.
 */
describe('AdoptFiltersRow (merge toggle)', () => {
	const checkbox = (row: AdoptFiltersRow): HTMLInputElement =>
		row.root.querySelector('input[type=checkbox]') as HTMLInputElement;

	// Must run first: grid registration is module-global and can't be undone.
	it('reveals itself as soon as the grid registers — no table filter needed', () => {
		const row = new AdoptFiltersRow({ onChange: vi.fn() });
		expect(row.root.hidden).toBe(true); // grid hasn't booted yet

		registerGridFilterApi({
			getOwnerFilter: () => null,
			setOwnerFilter: () => {},
			getGridFilters: () => null,
			setSpatialFilter: () => {},
		});

		expect(row.root.hidden).toBe(false);
	});

	it('restricts the table only while checked (live)', () => {
		const onChange = vi.fn();
		const row = new AdoptFiltersRow({ onChange });

		expect(row.restrictsTable()).toBe(false);

		const cb = checkbox(row);
		cb.checked = true;
		cb.dispatchEvent(new Event('change'));
		expect(row.restrictsTable()).toBe(true);
		expect(onChange).toHaveBeenCalledTimes(1);

		cb.checked = false;
		cb.dispatchEvent(new Event('change'));
		expect(row.restrictsTable()).toBe(false);
	});

	it('does NOT restrict the table in stored (edit) mode, even though the box is checked', () => {
		const row = new AdoptFiltersRow({ onChange: vi.fn() });

		// Editing a saved segment: filters shown frozen, box checked — but the table must not be
		// shape-restricted (its live columns would diverge from the segment's stored ones).
		row.setStored({ distance: { range: '0-10' } });
		expect(checkbox(row).checked).toBe(true);
		expect(row.restrictsTable()).toBe(false);

		row.setStored(null);
		expect(checkbox(row).checked).toBe(false);
		expect(row.restrictsTable()).toBe(false);
	});
});
