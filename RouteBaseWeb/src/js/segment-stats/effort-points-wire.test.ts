// @vitest-environment node
import type { EffortPerformanceDataWire, EffortPointsWire } from './segment-stats-effort-types';
import { describe, it, expect } from 'vitest';
import { hydrateEffortPoints, hydrateEffort } from './effort-points-wire';
import { must } from '../core/invariant';

describe('effort-points-wire — PS-04 hydration', () => {
	it('hydrates parallel arrays into point objects with exact values incl. nulls', () => {
		const wire: EffortPointsWire = {
			distance: [0, 100, 200],
			time: [0, 10, 20],
			speed: [7.2, 14.4, 21.6],
			altitude: [100, null, 120],
			lat: [47.0, 47.1, 47.2],
			lng: [11.0, 11.0, 11.0],
			cumulativeCostKj: [0, 1, 2],
			segmentProgress: null,
		};
		const points = hydrateEffortPoints(wire);
		expect(points).toHaveLength(3);
		expect(points[0]).toEqual({
			distance: 0, time: 0, speed: 7.2, altitude: 100, lat: 47.0, lng: 11.0,
			cumulativeCostKj: 0, segmentProgress: null,
		});
		expect(must(points[1], 'point 1').altitude).toBeNull();
		expect(must(points[2], 'point 2').cumulativeCostKj).toBe(2);
	});

	it('maps a null/absent whole channel to per-point null', () => {
		const wire: EffortPointsWire = {
			distance: [0, 1],
			time: [0, 1],
			speed: [5, 5],
			altitude: null,
			lat: null,
			lng: null,
			cumulativeCostKj: null, // no cost model available → whole channel null
			segmentProgress: null,
		};
		const points = hydrateEffortPoints(wire);
		expect(points.map((p) => p.cumulativeCostKj)).toEqual([null, null]);
		expect(points.map((p) => p.altitude)).toEqual([null, null]); // absent channel too
	});

	// The wire type always carries every channel (null when absent) — matches what STJ writes.
	const emptyChannels = {
		altitude: null, lat: null, lng: null, cumulativeCostKj: null, segmentProgress: null,
	};

	it('empty wire hydrates to []', () => {
		expect(hydrateEffortPoints({ distance: [], time: [], speed: [], ...emptyChannels })).toEqual([]);
	});

	it('hydrateEffort copies metadata and re-hydrates points', () => {
		const wire: EffortPerformanceDataWire = {
			effortId: 7, activityId: 42, elapsedTime: 123, pointsTruncated: true,
			activityName: null, activityDate: null, userId: null, userDisplayName: null,
			costEstimated: false, costKind: null,
			points: { distance: [0], time: [0], speed: [9], ...emptyChannels },
		};
		const effort = hydrateEffort(wire);
		expect(effort.effortId).toBe(7);
		expect(effort.pointsTruncated).toBe(true);
		expect(effort.points).toEqual([
			{ distance: 0, time: 0, speed: 9, altitude: null, lat: null, lng: null, cumulativeCostKj: null, segmentProgress: null },
		]);
	});
});
