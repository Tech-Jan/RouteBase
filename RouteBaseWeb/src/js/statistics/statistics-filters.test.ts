import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { createStatsFilterBar, type StatsFilterBoot } from './statistics-filters';
import { buildStatsColumns } from './statistics-filter-schema';
import { serializeFilters } from '../core/grid/grid-url';

const BOOT: StatsFilterBoot = {
	multiUser: true,
	typeOptions: ['run', 'bike'],
	ownerOptions: [{ value: 'u1', label: 'Anna' }],
};

// ST-08: Reset wipes the filter bar DOM; it must close any open popup first so buildCheckboxPopup's
// document/window listeners don't leak on the detached <details>.
describe('stats filter Reset closes open popups (ST-08)', () => {
	beforeEach(() => { document.body.innerHTML = '<div id="bar"></div>'; });
	afterEach(() => { vi.restoreAllMocks(); document.body.innerHTML = ''; });

	it('clicks the open popup summary before wiping the mount', () => {
		const mount = document.getElementById('bar')!;
		createStatsFilterBar(mount, BOOT);

		const details = mount.querySelector('details')!;
		const summary = details.querySelector('summary') as HTMLElement;
		summary.click(); // open the popup (jsdom sets details.open = true)
		expect(details.open).toBe(true);

		const closeSpy = vi.spyOn(summary, 'click');
		(mount.querySelector('.stats-filter-reset') as HTMLButtonElement).click();

		// render() closed the open popup (fires the real toggle-close cleanup in a browser) before rebuild.
		expect(closeSpy).toHaveBeenCalled();
		// The rebuilt bar has no open popups left.
		expect(mount.querySelectorAll('details[open]')).toHaveLength(0);
	});

	it('a resize after Reset does not throw', () => {
		const mount = document.getElementById('bar')!;
		createStatsFilterBar(mount, BOOT);
		(mount.querySelector('.stats-filter-reset') as HTMLButtonElement).click();
		expect(() => window.dispatchEvent(new Event('resize'))).not.toThrow();
	});
});

describe('stats visibility filter', () => {
	beforeEach(() => { document.body.innerHTML = '<div id="bar"></div>'; });
	afterEach(() => { document.body.innerHTML = ''; });

	function fieldByLabel(mount: HTMLElement, label: string): HTMLElement {
		return Array.from(mount.querySelectorAll<HTMLElement>('.stats-filter-field'))
			.find((f) => f.querySelector('.stats-filter-label')?.textContent === label)!;
	}

	it('renders a Visibility control', () => {
		const mount = document.getElementById('bar')!;
		createStatsFilterBar(mount, BOOT);
		expect(fieldByLabel(mount, 'Visibility')).toBeTruthy();
	});

	it('checking Public drives the visibility filter and serializes to f.visibility.in', () => {
		const mount = document.getElementById('bar')!;
		const state = createStatsFilterBar(mount, BOOT);

		const publicCb = Array.from(fieldByLabel(mount, 'Visibility').querySelectorAll<HTMLInputElement>('input[type=checkbox]'))
			.find((cb) => cb.value === 'public')!;
		publicCb.checked = true;
		publicCb.dispatchEvent(new Event('change'));

		expect(state.getFilter('visibility')?.selected).toContain('public');

		// Serializes to the shared f.<key>.in wire format the server's StatisticsApiController reads.
		const p = new URLSearchParams();
		serializeFilters(buildStatsColumns(BOOT.typeOptions, BOOT.ownerOptions), state.snapshot().filters, p);
		expect(p.getAll('f.visibility.in')).toContain('public');
	});
});
