import { describe, it, expect, beforeEach, vi } from 'vitest';
import { initStatsChartControls } from './statistics-chart';
import { state } from './statistics-chart-store';

/**
 * The statistics chart's metric selection — which SERIES the chart draws — as a device preference.
 *
 * The guard worth having a test for is the empty case: the metric toggle refuses to remove the last
 * metric, so an empty stored array can only come from a corrupted or hand-edited key. Applying it would
 * open the page on a chart with no series at all, and nothing would report an error — the chart would
 * just be blank. It is ignored instead.
 */

// Chart.js is a CDN global the render path guards on; the controls do not need it.
vi.stubGlobal('Chart', undefined);

const KEY = 'rb1.chart.statistics.series';

function mountControls(): void {
	document.body.innerHTML = `
		<div id="cumulative-metric">
			<button data-metric="distance" class="active"></button>
			<button data-metric="elevation"></button>
			<button data-metric="time"></button>
			<button data-metric="count"></button>
		</div>`;
}

const activeMetrics = (): string[] =>
	[...document.querySelectorAll<HTMLElement>('#cumulative-metric .active')]
		.map((b) => b.dataset['metric'] ?? '')
		.sort();

beforeEach(() => {
	localStorage.clear();
	document.body.innerHTML = '';
	state.chartType = 'line';
	state.metrics = new Set(['distance']);
});

describe('statistics chart — remembered series', () => {
	it('restores the stored metrics onto both the state and the buttons', () => {
		localStorage.setItem(KEY, JSON.stringify(['elevation', 'time']));
		mountControls();

		initStatsChartControls();

		expect([...state.metrics].sort()).toEqual(['elevation', 'time']);
		// The markup hard-codes `active` on Distance — the control has to follow the restore, not the markup.
		expect(activeMetrics()).toEqual(['elevation', 'time']);
	});

	it('persists what the user toggles', () => {
		mountControls();
		initStatsChartControls();

		document.querySelector<HTMLElement>('[data-metric="elevation"]')!.click();

		expect(JSON.parse(localStorage.getItem(KEY) ?? '[]').sort()).toEqual(['distance', 'elevation']);
	});

	it('IGNORES an empty stored selection instead of opening on a blank chart', () => {
		localStorage.setItem(KEY, JSON.stringify([]));
		mountControls();

		initStatsChartControls();

		expect([...state.metrics]).toEqual(['distance']);
	});

	it('drops stored metrics that no longer exist, and ignores the set if none survive', () => {
		localStorage.setItem(KEY, JSON.stringify(['elevation', 'calories']));
		mountControls();
		initStatsChartControls();
		expect([...state.metrics]).toEqual(['elevation']);

		localStorage.setItem(KEY, JSON.stringify(['calories']));
		state.metrics = new Set(['distance']);
		mountControls();
		initStatsChartControls();
		expect([...state.metrics]).toEqual(['distance']);
	});

	it('narrows to a single metric in bar mode, which allows only one', () => {
		localStorage.setItem(KEY, JSON.stringify(['distance', 'elevation', 'time']));
		state.chartType = 'bar';
		mountControls();

		initStatsChartControls();

		expect(state.metrics.size).toBe(1);
	});
});
