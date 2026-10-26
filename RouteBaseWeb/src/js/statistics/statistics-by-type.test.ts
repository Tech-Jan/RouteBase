import { describe, it, expect, beforeEach } from 'vitest';
import { renderStatsByType } from './statistics-by-type';

function zeroStats() {
	return {
		totalActivities: 0, totalDistance: 0, totalAscent: 0, totalDescent: 0,
		totalTime: 0, totalMovingTime: 0, averageSpeed: 0,
	};
}

// Values chosen so a WRONG divisor changes the digits, not just a trailing zero: distance 12.50 km
// (÷1000; ÷100→125, ÷10000→1.25), time 2h (÷3600; ÷60→120h), speed 18.0 km/h (×3.6; dropped→5.0).
function nonZeroStats() {
	return {
		totalActivities: 12, totalDistance: 12500, totalAscent: 850, totalDescent: 760,
		totalTime: 7200, totalMovingTime: 6000, averageSpeed: 5,
	};
}

function rowValues(): Record<string, string> {
	const out: Record<string, string> = {};
	for (const rowEl of document.querySelectorAll('#statsTypeGrid .stat-row')) {
		out[rowEl.querySelector('.stat-row-label')!.textContent!] =
			rowEl.querySelector('.stat-row-value')!.textContent!;
	}
	return out;
}

// ST-05/ST-06: the type header is a user-controlled (imported FIT/TCX/GPX) string sink.
describe('renderStatsByType', () => {
	beforeEach(() => {
		document.body.innerHTML = '<div id="statsTypeGrid"></div><div id="statsTypeEmpty"></div>';
	});

	it('escapes HTML in the activity type header', () => {
		renderStatsByType([{ type: '<img src=x onerror=alert(1)>', stats: zeroStats() }]);
		const grid = document.getElementById('statsTypeGrid')!;
		expect(grid.querySelector('img')).toBeNull(); // not parsed as markup
		expect(grid.querySelector('.stat-panel-header')!.textContent).toBe('<img src=x onerror=alert(1)>');
	});

	it('applies the m→km, s→h and m/s→km/h conversions with the right divisors', () => {
		renderStatsByType([{ type: 'Ride', stats: nonZeroStats() }]);
		const v = rowValues();
		// Decimal separator is locale-dependent; the DIGITS (which the divisor decides) are not.
		expect(v['Distance']).toMatch(/^12[.,]50 km$/);      // 12500 / 1000
		expect(v['Total Time']).toBe('2h');                   // floor(7200 / 3600)
		expect(v['Avg Speed']).toMatch(/^18[.,]0 km\/h$/);   // 5 m/s × 3.6
		expect(v['Avg Pace']).toBe('3:20 min/km');            // 60 / 18 km/h → 3:20 (confirms speedKmh)
		expect(v['Ascent']).toBe('850 m');
		expect(v['Descent']).toBe('760 m');
		expect(v['Activities']).toBe('12');
	});

	it('shows the empty state and clears the grid when there are no types', () => {
		renderStatsByType([]);
		expect(document.getElementById('statsTypeGrid')!.innerHTML).toBe('');
		expect(document.getElementById('statsTypeEmpty')!.style.display).toBe('');
	});
});
