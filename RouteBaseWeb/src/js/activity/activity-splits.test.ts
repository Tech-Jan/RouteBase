import { describe, it, expect, vi, beforeEach } from 'vitest';
import { must } from '../core/invariant';

// DC-01: the last split's highlight must survive F2-rounding of the final distance rounding UP
// past the last GPS point's distance — the end lookup clamps to the last index instead of aborting.

// Hoisted so the (hoisted) vi.mock factories can reference these without a TDZ error.
const h = vi.hoisted(() => ({ showSplitHighlight: vi.fn(), gps: { data: [] as Array<{ distance: number }> } }));
const showSplitHighlight = h.showSplitHighlight;

vi.mock('../core/gps-data', () => ({ getGpsData: () => h.gps.data }));
vi.mock('../core/map-dispatcher', () => ({
	mapDispatcher: { showSplitHighlight: h.showSplitHighlight, clearSplitHighlight: vi.fn(), setupProximityHover: vi.fn() },
}));
vi.mock('../core/chart-overlay', () => ({ showSplitOverlay: vi.fn(), clearSplitOverlay: vi.fn() }));
vi.mock('../core/split-state', () => ({ getLockedSplit: () => null, setLockedSplit: vi.fn() }));
vi.mock('../core/nearest-point', () => ({ findNearestPointIndex: () => 0 }));
vi.mock('../core/map-state', () => ({ getMaplibreMap: () => null }));
vi.mock('./activity-details', () => ({
	getSpeedChart: () => null, getElevationChart: () => null, updateHighlight: vi.fn(), hideHighlight: vi.fn(),
}));
vi.mock('./details-shared', () => ({
	registerSplitHighlightApi: vi.fn(), getOnChartClick: () => null, registerHoverTracking: vi.fn(),
}));

import { setupSplitsInteraction } from './activity-splits';

function rowFixture(rows: Array<[string, string]>): void {
	const trs = rows
		.map(([s, e]) => `<tr class="split-row" data-split-start="${s}" data-split-end="${e}"></tr>`)
		.join('');
	document.body.innerHTML = `<table><tbody>${trs}</tbody></table>`;
}

function hover(index: number): void {
	must(document.querySelectorAll<HTMLElement>('.split-row')[index], 'split row at index')
		.dispatchEvent(new MouseEvent('mouseenter', { bubbles: true }));
}

describe('activity-splits highlight clamp (DC-01)', () => {
	beforeEach(() => {
		showSplitHighlight.mockClear();
		// Track max distance rounds UP to 5.68 in the F2 data attribute.
		h.gps.data = [0, 1, 2, 3, 4, 5.6789].map((d) => ({ distance: d }));
	});

	it('last split highlights when data-split-end rounds above track end', () => {
		rowFixture([['0.00', '4.00'], ['4.00', '5.68']]);
		setupSplitsInteraction([]);
		hover(1);
		expect(showSplitHighlight).toHaveBeenCalledTimes(1);
		// endIndex clamps to the last GPS index (5) instead of -1.
		expect(showSplitHighlight).toHaveBeenCalledWith(4, 5);
	});

	it('end clamp does not mask a start beyond the track end', () => {
		rowFixture([['6.00', '6.50']]);
		setupSplitsInteraction([]);
		hover(0);
		expect(showSplitHighlight).not.toHaveBeenCalled();
	});
});
