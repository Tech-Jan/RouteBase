// @vitest-environment node
import { describe, it, expect } from 'vitest';
import {
	buildSeriesViews, lineForMode, readoutDecimalsForMode, SERIES_MODES, type SeriesViews,
} from './chart-series-modes';
import { must } from './invariant';

/** A ride sampled at 1 Hz: xs in km at 10 m/s, plus whatever speeds are handed in. */
function ride(values: number[], opts: { withTime?: boolean; window?: number; bins?: number } = {}): SeriesViews {
	const n = values.length;
	const xs = Array.from({ length: n }, (_, i) => (i * 10) / 1000);
	return buildSeriesViews({
		values,
		xs,
		seconds: opts.withTime === false ? null : Array.from({ length: n }, (_, i) => i),
		windowSeconds: opts.window ?? 6,
		sampleWindow: 5,
		bins: opts.bins ?? 1000,
		target: 1000,
	});
}

describe('buildSeriesViews', () => {
	it('keeps the recorded series untouched — it is the one view that must not be processed', () => {
		const v = [10, 40, 12, 38, 11];
		expect(ride(v).views.recorded.full.map((p) => p.y)).toEqual(v);
	});

	it('smooths the processed view without moving it — the recorded peak stays put in x', () => {
		// Dense enough that the bins resolve the spike: with a handful of samples the bin centres sit
		// between them and the peak can legitimately land on a neighbour.
		const v = Array.from({ length: 61 }, (_, i) => (i === 30 ? 40 : 10));
		const { recorded, smoothBin: smoothed } = ride(v).views;
		// Same x grid, so the crosshair maps identically in either mode.
		expect(smoothed.full.map((p) => p.x)).toEqual(recorded.full.map((p) => p.x));
		// The spike is flattened but stays the maximum, and it stays at its own index.
		const peak = smoothed.full.reduce((best, p, i) => (p.y > (smoothed.full[best]?.y ?? -Infinity) ? i : best), 0);
		expect(peak).toBe(30);
		expect(must(smoothed.full[30], 'peak').y).toBeLessThan(40);
	});




	it('still smooths without a time axis, using the sample kernel as a fallback', () => {
		const v = [10, 10, 10, 40, 10, 10, 10];
		const views = ride(v, { withTime: false });
		expect(must(views.views.smoothBin.full[3], 'peak').y).toBeLessThan(40);
		expect(views.views.smoothBin.full.map((p) => p.y)).not.toEqual(v);
	});

	it('never reads a hole as a standstill', () => {
		// The recorded view keeps the gap; the processed view interpolates ACROSS it (that is the
		// empty-bin fix — an unsampled stretch is a gap in the sampling, not a stop). What neither may
		// do is invent a zero, which is what `|| 0` used to.
		const views = ride([20, 20, Number.NaN, 20, 20]);
		expect(must(views.views.recorded.full[2], 'recorded').y).toBeNaN();
		expect(must(views.views.smoothBin.full[2], 'processed').y).toBeCloseTo(20, 6);
	});

	it('decimates to the target while leaving the full-resolution arrays intact', () => {
		const v = Array.from({ length: 5000 }, (_, i) => 20 + Math.sin(i / 20) * 5);
		const xs = Array.from({ length: 5000 }, (_, i) => i / 100);
		const views = buildSeriesViews({
			values: v, xs, seconds: Array.from({ length: 5000 }, (_, i) => i),
			windowSeconds: 10, sampleWindow: 5, bins: 1000, target: 500,
		});
		expect(views.views.smoothBin.full).toHaveLength(5000);
		expect(views.views.smoothBin.dec).toHaveLength(500);
		expect(views.views.recorded.dec).toHaveLength(500);
	});
});

describe('lineForMode', () => {
	const views = ride([12, 30, 8, 25, 19]);

	it('picks the series each mode names', () => {
		for (const mode of SERIES_MODES) expect(lineForMode(views, mode)).toBe(views.views[mode]);
	});

	it('offers every mode in increasing order of processing', () => {
		expect(SERIES_MODES).toEqual(['recorded', 'smoothBin']);
	});
});

// A table over SERIES_MODES rather than a list of literals: adding a mode extends this test instead
// of leaving it silently covering only the old ones.
describe('every declared mode is fully wired', () => {
	const views = ride([12, 30, 8, 25, 19, 33, 11, 27, 15, 22]);

	it.each(SERIES_MODES)('%s has a view of the right length', (mode) => {
		const v = lineForMode(views, mode);
		expect(v.full).toHaveLength(10);
		expect(v.dec.length).toBeGreaterThan(0);
	});

	it.each(SERIES_MODES)('%s shares the recorded x-grid exactly', (mode) => {
		// Load-bearing: the zoom re-slice and the crosshair both index across modes.
		expect(lineForMode(views, mode).full.map((p) => p.x))
			.toEqual(views.views.recorded.full.map((p) => p.x));
	});

	it.each(SERIES_MODES)('%s has a decimal rule', (mode) => {
		expect(Number.isInteger(readoutDecimalsForMode(mode))).toBe(true);
	});
});

describe('rebuildRange', () => {
	it('re-derives the binned view for a sub-range', () => {
		const views = ride(Array.from({ length: 40 }, (_, i) => 10 + (i % 4)));
		const out = views.rebuildRange('smoothBin', 10, 20);
		expect(out).not.toBeNull();
		expect(out).toHaveLength(11);
	});

	it('returns null for the modes that do not depend on the visible range', () => {
		const views = ride([1, 2, 3, 4, 5, 6, 7, 8]);
		expect(views.rebuildRange('recorded', 2, 5)).toBeNull();
	});
});

describe('readoutDecimalsForMode', () => {
	it('drops the decimal on a smoothed value — its tenths are arithmetic, not measurement', () => {
		// 5.6 km/h averaged over half a minute is not known to a tenth; printing one claims otherwise.
		expect(readoutDecimalsForMode('smoothBin')).toBe(0);
	});

	it('keeps the decimal on the recorded value, which IS the stored sample', () => {
		expect(readoutDecimalsForMode('recorded')).toBe(1);
	});
});
