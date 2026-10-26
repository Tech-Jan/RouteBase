// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { must } from './invariant';
import { axisValueAtIndex, buildTerrainBackdrop } from './profile-terrain';

/**
 * Moved here with the function when the activity replay became its second caller. The rule it enforces is
 * the interesting part: a track whose altitudes are incomplete gets NO terrain, because a line with holes
 * in it draws cliffs that are not there.
 */

/** ~100 m apart along a meridian, climbing 10 m each hop. */
function coordsWithAltitude(n: number): number[][] {
	return Array.from({ length: n }, (_, i) => [47.9 + i * 0.0009, 8.1, 500 + i * 10]);
}

describe('buildTerrainBackdrop', () => {
	it('resamples the reference line onto a uniform 0–100 completion axis', () => {
		const terrain = buildTerrainBackdrop(coordsWithAltitude(5), 11)!;

		expect(terrain.kind).toBe('terrain');
		expect(terrain.axisMax).toBe(100);
		expect(terrain.step).toBeCloseTo(10, 6);
		expect(terrain.elevation).toHaveLength(11);
		// Ends are exact; the middle is interpolated between nodes, and the climb is monotonic.
		expect(terrain.elevation[0]).toBeCloseTo(500, 3);
		expect(terrain.elevation[10]).toBeCloseTo(540, 3);
		expect(terrain.elevation[5]).toBeGreaterThan(515);
		expect(terrain.elevation[5]).toBeLessThan(525);
	});

	it('resamples at the SOURCE resolution by default, floored and capped', () => {
		// A fixed count cut summits that fell between two nodes on a long ride. The floor keeps a sparse
		// track drawing the smooth curve it always did; the cap is the pixel column, past which nodes
		// stop being visible.
		expect(buildTerrainBackdrop(coordsWithAltitude(20))!.elevation).toHaveLength(240);
		expect(buildTerrainBackdrop(coordsWithAltitude(900))!.elevation).toHaveLength(900);
		expect(buildTerrainBackdrop(coordsWithAltitude(9000))!.elevation).toHaveLength(2000);
	});

	it('keeps a summit that falls between two nodes of the OLD fixed grid', () => {
		// 600 points, flat but for a one-point 40 m spike at 300 — the kind of feature a 240-node grid
		// lands either side of and interpolates away.
		const coords = Array.from({ length: 600 }, (_, i) => [47.9 + i * 0.0009, 8.1, i === 300 ? 540 : 500]);
		const peak = (samples?: number): number =>
			Math.max(...Array.from(buildTerrainBackdrop(coords, samples)!.elevation));

		expect(peak(240)).toBeLessThan(535);   // the old behaviour: the spike is clipped
		expect(peak()).toBeGreaterThan(539);   // at source resolution it survives
	});

	it('refuses a line with ANY altitude missing — a hole would draw a cliff that is not there', () => {
		const coords = coordsWithAltitude(5);
		const p2 = must(coords[2], 'fixture point 2');
		coords[2] = [must(p2[0], 'fixture lat'), must(p2[1], 'fixture lng')];   // this point never got an altitude

		expect(buildTerrainBackdrop(coords)).toBeNull();
	});

	it('refuses degenerate input rather than dividing by zero', () => {
		expect(buildTerrainBackdrop([])).toBeNull();
		expect(buildTerrainBackdrop([[47.9, 8.1, 500]])).toBeNull();
		// Every point at the same spot: no distance to be a function of.
		expect(buildTerrainBackdrop([[47.9, 8.1, 500], [47.9, 8.1, 520]])).toBeNull();
	});

	it('reports the axis position of every SOURCE point, 0 to 100', () => {
		const terrain = buildTerrainBackdrop(coordsWithAltitude(5), 11)!;

		expect(terrain.axisAt).toHaveLength(5);
		expect(terrain.axisAt[0]).toBeCloseTo(0, 6);
		expect(terrain.axisAt[4]).toBeCloseTo(100, 6);
		// Equidistant points, so here — and ONLY here — index space and distance space agree.
		expect(terrain.axisAt[2]).toBeCloseTo(50, 3);
		for (let i = 1; i < terrain.axisAt.length; i++) {
			expect(terrain.axisAt[i]).toBeGreaterThan(must(terrain.axisAt[i - 1], 'i-1 >= 0 (loop-bounded)'));
		}
	});

	it('places bunched-up points by DISTANCE, not by their share of the sample count', () => {
		// Four short hops then one long one: point 4 is 4/5 of the way through the samples but only a
		// small fraction of the way along the ground. Sample space would put it at 80.
		const coords = [0, 1, 2, 3, 4, 24].map((hops) => [47.9 + hops * 0.0009, 8.1, 500]);
		const terrain = buildTerrainBackdrop(coords, 11)!;

		expect(terrain.axisAt[4]).toBeCloseTo((4 / 24) * 100, 1);
	});
});

describe('axisValueAtIndex', () => {
	const axisAt = Float64Array.from([0, 10, 20, 100]);

	it('reads an exact index straight off', () => {
		expect(axisValueAtIndex(axisAt, 1)).toBe(10);
	});

	it('interpolates a fractional index — the playhead lands between samples, not on one', () => {
		expect(axisValueAtIndex(axisAt, 2.5)).toBeCloseTo(60, 6);
		// Interpolating in AXIS space, not index space: the last leg is 8× the width of the previous one.
		expect(axisValueAtIndex(axisAt, 1.25)).toBeCloseTo(12.5, 6);
	});

	it('clamps both ends so a finished replay pins at 100 instead of running off the strip', () => {
		expect(axisValueAtIndex(axisAt, -3)).toBe(0);
		expect(axisValueAtIndex(axisAt, 3)).toBe(100);
		expect(axisValueAtIndex(axisAt, 99)).toBe(100);
	});

	it('survives an empty axis rather than returning NaN', () => {
		expect(axisValueAtIndex(new Float64Array(0), 2)).toBe(0);
	});
});
