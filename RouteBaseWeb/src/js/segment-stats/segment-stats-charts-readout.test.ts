import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { nearestIndexByX } from './segment-stats-charts-readout';

// SC2-05 split pins for the extracted readout module: nearestIndexByX bisect/tie-break and the
// SC2-01 null-canvas guard on updateHoverStrip (a stale _rerenderStrip closure firing after a rebuild).

function seedConfig(): void {
	const cfg = { hasSegment: true, hasGpsData: true, hasEfforts: true, mapTilerApiKey: '', segmentId: 5, kind: 'polyline', effortRows: [] };
	document.body.innerHTML = '<script type="application/json" id="segmentStatisticsConfig">' + JSON.stringify(cfg) + '</script>';
}

beforeEach(() => { seedConfig(); });
afterEach(async () => {
	document.body.innerHTML = '';
	const { vi } = await import('vitest');
	vi.resetModules();
});

describe('nearestIndexByX', () => {
	it('returns 0 for an empty array', () => {
		expect(nearestIndexByX([], 5)).toBe(0);
	});
	it('resolves the nearest point by x-value (bisect)', () => {
		const data = [{ x: 0 }, { x: 10 }, { x: 20 }];
		expect(nearestIndexByX(data, 12)).toBe(1);
		expect(nearestIndexByX(data, 8)).toBe(1);
		expect(nearestIndexByX(data, 19)).toBe(2);
	});
	it('breaks a midpoint tie toward the lower index', () => {
		const data = [{ x: 0 }, { x: 10 }];
		expect(nearestIndexByX(data, 5)).toBe(0);
	});
});

describe('createChartReadout.updateHoverStrip — SC2-01 null-canvas guard', () => {
	it('returns without throwing when the chart was destroyed (canvas null)', async () => {
		const { createChartReadout } = await import('./segment-stats-charts-readout');
		// ctx is a dep now; the SC2-01 null-canvas guard returns before touching it, so a
		// bare fake is enough to construct the readout here.
		// eslint-disable-next-line @typescript-eslint/no-explicit-any
		const readout = createChartReadout({ delta: {} as any, ctx: {} as any });
		const destroyed = { canvas: null, chartArea: null } as unknown as import('../core/core-types').ChartJsInstance;
		expect(() => readout.updateHoverStrip(destroyed, 0, 0, 'km/h', (v) => String(v), 'auto')).not.toThrow();
		expect(() => readout.updateHoverStrip(null, 0, 0, 'km/h', (v) => String(v), 'auto')).not.toThrow();
	});
});
