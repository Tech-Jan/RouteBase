// @vitest-environment node
import { describe, it, expect } from 'vitest';
import { must } from '../core/invariant';
import {
	startRoute,
	extendRoute,
	routeDistanceMeters,
	buildSavePayload,
	createLegQueue,
	exceedsPointCap,
	MAX_SEGMENT_POINTS,
	type RoutePoint,
} from './segment-create-route-state';

function deferred<T>(): { promise: Promise<T>; resolve: (v: T) => void } {
	let resolve!: (v: T) => void;
	const promise = new Promise<T>((r) => { resolve = r; });
	return { promise, resolve };
}

// SC-01 characterization pins for the pure route-state logic extracted from
// segment-create.ts. These lock CURRENT behavior (incl. the pop-shared-vertex
// dance) before the MapLibre port.

describe('segment-create route-state — SC-01 pins', () => {
	it('startRoute seeds one point with null altitude', () => {
		const r = startRoute(47.5, 7.6);
		expect(r).toHaveLength(1);
		expect(r[0]).toEqual({ lat: 47.5, lng: 7.6, altitude: null });
	});

	it('extendRoute pops the shared vertex and appends (straight legs)', () => {
		const p1: [number, number] = [47.0, 7.0];
		const p2: [number, number] = [47.1, 7.1];
		const p3: [number, number] = [47.2, 7.2];

		let pts = startRoute(p1[0], p1[1]);
		pts = extendRoute(pts, [p1, p2]);
		expect(pts.map((p) => [p.lat, p.lng])).toEqual([p1, p2]);

		pts = extendRoute(pts, [p2, p3]);
		expect(pts.map((p) => [p.lat, p.lng])).toEqual([p1, p2, p3]);
	});

	it('extendRoute keeps routed intermediate vertices', () => {
		const p1: [number, number] = [47.0, 7.0];
		const routedLeg: [number, number][] = [
			[47.0, 7.0],
			[47.02, 7.01],
			[47.05, 7.03],
			[47.1, 7.1],
		];
		const pts = extendRoute(startRoute(p1[0], p1[1]), routedLeg);
		expect(pts).toHaveLength(4);
		const pt0 = must(pts[0], 'point 0 of 4');
		expect([pt0.lat, pt0.lng]).toEqual(p1);
		expect(pts.every((p) => p.altitude === null)).toBe(true);
	});

	it('extendRoute on empty input appends coords unchanged (pop-guard parity)', () => {
		const a: [number, number] = [47.0, 7.0];
		const b: [number, number] = [47.1, 7.1];
		const pts = extendRoute([], [a, b]);
		expect(pts.map((p) => [p.lat, p.lng])).toEqual([a, b]);
	});

	it('routeDistanceMeters sums haversine legs', () => {
		const pts: RoutePoint[] = [
			{ lat: 47.0, lng: 7.0, altitude: null },
			{ lat: 47.01, lng: 7.0, altitude: null },
			{ lat: 47.02, lng: 7.0, altitude: null },
		];
		expect(routeDistanceMeters(pts)).toBeCloseTo(2 * 1111.95, 0);
		expect(routeDistanceMeters([])).toBe(0);
		expect(routeDistanceMeters([must(pts[0], 'point 0')])).toBe(0);
	});

	it('buildSavePayload carries name, type and points untouched', () => {
		const pts: RoutePoint[] = [
			{ lat: 47.0, lng: 7.0, altitude: null },
			{ lat: 47.1, lng: 7.1, altitude: null },
		];
		expect(buildSavePayload('Climb', 'Cycling', pts)).toEqual({
			segmentName: 'Climb',
			activityType: 'Cycling',
			gpsPoints: pts,
		});
	});
});

describe('segment-create route-state — SG2 findings', () => {
	it('SG2-05: exceedsPointCap is strict > MAX_SEGMENT_POINTS (matches server ceiling)', () => {
		// exceedsPointCap only reads .length; fake it so we don't allocate a million-point array.
		const atCap = { length: MAX_SEGMENT_POINTS } as unknown as RoutePoint[];
		const overCap = { length: MAX_SEGMENT_POINTS + 1 } as unknown as RoutePoint[];
		expect(exceedsPointCap(atCap)).toBe(false);
		expect(exceedsPointCap(overCap)).toBe(true);
	});

	it('SG2-01: createLegQueue applies tasks in enqueue order even when the second resolves first', async () => {
		const q = createLegQueue();
		const d1 = deferred<string>();
		const d2 = deferred<string>();
		const order: string[] = [];

		const p1 = q.run(() => d1.promise).then((v) => { order.push('done1:' + v); });
		const p2 = q.run(() => d2.promise).then((v) => { order.push('done2:' + v); });

		// Resolve the second task's underlying promise first; the queue must still run task 1's
		// continuation before task 2's because task 2 doesn't start until task 1's chain settles.
		d2.resolve('B');
		await Promise.resolve();
		d1.resolve('A');
		await Promise.all([p1, p2]);

		expect(order).toEqual(['done1:A', 'done2:B']);
	});

	it('SG2-01: invalidate() drops the result of an already-queued task', async () => {
		const q = createLegQueue();
		const d = deferred<string>();
		const p = q.run(() => d.promise);
		q.invalidate(); // Clear happened before the task resolved
		d.resolve('stale');
		await expect(p).resolves.toBeUndefined();
	});
});
