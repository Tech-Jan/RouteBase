import { describe, it, expect, beforeEach, vi } from 'vitest';
import { createSeriesModeControl } from './series-mode-control';
import { clearAllDevicePrefs } from './device-prefs';
import type { SeriesMode } from './chart-series-modes';

/** The button renders an icon plus a label span; the label is the part that names the mode. */
function label(btn: HTMLElement): string {
	return btn.querySelector('.chart-mode-label')?.textContent ?? '';
}

function button(): HTMLButtonElement {
	const b = document.createElement('button');
	document.body.appendChild(b);
	return b;
}

describe('createSeriesModeControl', () => {
	beforeEach(() => {
		document.body.innerHTML = '';
		clearAllDevicePrefs();
	});

	it('reports its mode before first render, so the chart never opens on the wrong one', () => {
		// The callback fires on wire-up, not only on click: the chart is built from what this says.
		const onChange = vi.fn();
		createSeriesModeControl({ button: button(), prefKey: 'x', onChange });
		expect(onChange).toHaveBeenCalledWith('smoothBin');
	});

	it('cycles forward through the modes and back to the start', () => {
		const seen: SeriesMode[] = [];
		const btn = button();
		createSeriesModeControl({ button: btn, prefKey: 'x', onChange: (m) => seen.push(m) });

		btn.click(); btn.click();
		expect(seen).toEqual(['smoothBin', 'recorded', 'smoothBin']);
	});

	it('paints the label and the pressed state onto the markup', () => {
		const btn = button();
		createSeriesModeControl({ button: btn, prefKey: 'x', onChange: () => {} });

		expect(label(btn)).toBe('Smoothed');
		expect(btn.querySelector('i')?.className).toContain('bi-');
		expect(btn.getAttribute('aria-pressed')).toBe('false'); // the default view is not "on"

		btn.click();
		expect(label(btn)).toBe('Recorded');
		expect(btn.getAttribute('aria-pressed')).toBe('true');
		expect(btn.classList.contains('is-active')).toBe(true);
	});

	it('never labels the recorded view "Raw" — the stored channel is already averaged at import', () => {
		const btn = button();
		createSeriesModeControl({ button: btn, prefKey: 'x', onChange: () => {} });
		btn.click();
		expect(label(btn)).toBe('Recorded');
	});

	it('restores the stored mode and PAINTS it, so the control cannot lie about the chart', () => {
		const first = button();
		createSeriesModeControl({ button: first, prefKey: 'speed', onChange: () => {} });
		first.click(); // → recorded, persisted

		document.body.innerHTML = '';
		const onChange = vi.fn();
		const second = button();
		createSeriesModeControl({ button: second, prefKey: 'speed', onChange });

		expect(onChange).toHaveBeenCalledWith('recorded');
		expect(label(second)).toBe('Recorded');
		expect(second.getAttribute('aria-pressed')).toBe('true');
	});

	it('keeps two charts preferences apart', () => {
		const a = button();
		createSeriesModeControl({ button: a, prefKey: 'chartA', onChange: () => {} });
		a.click(); // chartA → recorded

		const b = button();
		const onChange = vi.fn();
		createSeriesModeControl({ button: b, prefKey: 'chartB', onChange });
		expect(onChange).toHaveBeenCalledWith('smoothBin');
	});


	it('falls back to the default when the stored value is one this build no longer offers', () => {
		localStorage.setItem('rb1.chart.speed.seriesmode', 'levels');
		const onChange = vi.fn();
		createSeriesModeControl({ button: button(), prefKey: 'speed', onChange });
		expect(onChange).toHaveBeenCalledWith('smoothBin');
	});



	it('keeps the SAME child nodes across a click — a detached target breaks click guards', () => {
		// The bug this pins: paint() used to rebuild the button's children, so the <i> the click landed
		// on was detached before the event finished bubbling. A detached node's closest() finds nothing,
		// so collapsible-card's "ignore clicks on buttons in my header" guard missed and switching the
		// view folded the whole chart card.
		const btn = button();
		createSeriesModeControl({ button: btn, prefKey: 'x', onChange: () => {} });
		const icon = btn.querySelector('i');
		const text = btn.querySelector('.chart-mode-label');

		btn.click();

		expect(btn.querySelector('i')).toBe(icon);
		expect(btn.querySelector('.chart-mode-label')).toBe(text);
		expect(icon?.isConnected).toBe(true);
		expect(label(btn)).toBe('Recorded'); // and it still actually switched
	});

	it('is a no-op without markup rather than a crash', () => {
		expect(createSeriesModeControl({ button: null, prefKey: 'x', onChange: () => {} })).toBeNull();
	});

	it('stops responding after destroy', () => {
		const btn = button();
		const onChange = vi.fn();
		const control = createSeriesModeControl({ button: btn, prefKey: 'x', onChange });
		control?.destroy();
		btn.click();
		expect(onChange).toHaveBeenCalledTimes(1); // the wire-up call only
	});
});
