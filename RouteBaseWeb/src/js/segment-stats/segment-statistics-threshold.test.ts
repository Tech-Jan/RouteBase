import { describe, it, expect, beforeEach, afterEach } from 'vitest';

// SS-06: match-threshold input normalization — a typed 0 must survive (min="0"), while blank /
// non-finite entries fall back to the default.

function seedConfig(): void {
	const cfg = { hasSegment: true, hasGpsData: true, hasEfforts: false, mapTilerApiKey: '', segmentId: 5, kind: 'polyline', effortRows: [] };
	document.body.innerHTML = '<script type="application/json" id="segmentStatisticsConfig">' + JSON.stringify(cfg) + '</script>';
}

beforeEach(() => { seedConfig(); });
afterEach(async () => {
	document.body.innerHTML = '';
	const { vi } = await import('vitest');
	vi.resetModules();
});

describe('normalizeThresholdInput — SS-06', () => {
	it('preserves a typed 0', async () => {
		const { normalizeThresholdInput } = await import('./segment-statistics');
		expect(normalizeThresholdInput('0', 40, 0, 100)).toBe(0);
	});
	it('falls back to the default for a blank entry', async () => {
		const { normalizeThresholdInput } = await import('./segment-statistics');
		expect(normalizeThresholdInput('  ', 40, 0, 100)).toBe(40);
	});
	it('clamps to max', async () => {
		const { normalizeThresholdInput } = await import('./segment-statistics');
		expect(normalizeThresholdInput('150', 40, 0, 100)).toBe(100);
	});
	it('falls back to the default for a non-numeric entry', async () => {
		const { normalizeThresholdInput } = await import('./segment-statistics');
		expect(normalizeThresholdInput('abc', 40, 0, 100)).toBe(40);
	});
});
