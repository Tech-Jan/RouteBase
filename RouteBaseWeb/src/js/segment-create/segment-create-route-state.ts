// segment-create-route-state.ts — Pure route-point list operations for the
// segment-create page. No DOM, no map — unit-testable in isolation.
// Extracted from segment-create.ts (SC-01) ahead of the MapLibre port.
import { haversineDistance } from '../core/geo';
import { must } from '../core/invariant';

export interface RoutePoint {
	lat: number;
	lng: number;
	altitude: number | null;
}

/** Seed a fresh route with a single waypoint (null altitude) — replaces the first-click seed. */
export function startRoute(lat: number, lng: number): RoutePoint[] {
	return [{ lat, lng, altitude: null }];
}

/**
 * Extend a route by one leg: drop the shared last vertex, then append `coords`
 * ([lat, lng] tuples) as new RoutePoints. Returns a NEW array; slice on an empty
 * input is a no-op (pop-guard parity). Covers all three legs of the old
 * routeSegment: routed (coords = fetchRoute result) and straight / route-failure
 * (coords = [[start], [end]]).
 */
export function extendRoute(points: RoutePoint[], coords: [number, number][]): RoutePoint[] {
	const kept = points.slice(0, -1);
	const appended: RoutePoint[] = coords.map(([lat, lng]) => ({ lat, lng, altitude: null }));
	return [...kept, ...appended];
}

/** Sum of haversine leg distances in metres; 0 for fewer than 2 points. */
export function routeDistanceMeters(points: RoutePoint[]): number {
	let total = 0;
	for (let i = 1; i < points.length; i++) {
		const a = must(points[i - 1], 'points[i-1] (loop-bounded)');
		const b = must(points[i], 'points[i] (loop-bounded)');
		total += haversineDistance(a.lat, a.lng, b.lat, b.lng);
	}
	return total;
}

/** Server-side ceiling mirrored client-side (SegmentsApiController.CreateSegment rejects > 1000000). The
 *  server thins the polyline before RDP, so this is a raw-request-size guard, not a geometry limit —
 *  a drawn route realistically never reaches it. */
export const MAX_SEGMENT_POINTS = 1000000;

/** SG2-05: does the accumulated route exceed the server's point cap? (strict `>` to match the server). */
export function exceedsPointCap(points: RoutePoint[]): boolean {
	return points.length > MAX_SEGMENT_POINTS;
}

/**
 * SG2-01: serialize async route-leg tasks so their results apply in enqueue order regardless of
 * completion order, and let a caller invalidate tasks queued before a Clear / new draw session so
 * their results are dropped instead of silently re-inserted later.
 */
export function createLegQueue(): {
	run<T>(task: () => Promise<T>): Promise<T | undefined>;
	invalidate(): void;
} {
	let tail: Promise<unknown> = Promise.resolve();
	let epoch = 0;
	return {
		run<T>(task: () => Promise<T>): Promise<T | undefined> {
			const myEpoch = epoch;
			const p = tail.then(() => (myEpoch === epoch ? task() : undefined));
			tail = p.catch(() => undefined);
			return p;
		},
		invalidate(): void {
			epoch++;
			tail = Promise.resolve();
		},
	};
}

/** Build the /api/SegmentsApi POST body — bytes on the wire identical to the old inline literal. */
export function buildSavePayload(
	segmentName: string,
	activityType: string,
	points: RoutePoint[],
): { segmentName: string; activityType: string; gpsPoints: RoutePoint[] } {
	return { segmentName, activityType, gpsPoints: points };
}
