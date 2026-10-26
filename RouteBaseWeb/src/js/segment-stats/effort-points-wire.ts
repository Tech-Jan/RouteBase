// PS-04: hydrate the columnar performance-data wire form back into per-point objects at the fetch
// boundary. Keeping this the single hydration site means every chart/hover/replay consumer keeps
// working on EffortDataPoint[] unchanged.
import type { EffortPerformanceData, EffortPerformanceDataWire, EffortPointsWire, EffortDataPoint } from './segment-stats-effort-types';
import { must } from '../core/invariant';

/** Read column[i], treating a null/absent whole-channel as a per-point null. */
function channel(col: Array<number | null> | null | undefined, i: number): number | null {
	// ?? null: a lockstep hole folds into the same per-point-null path a null cell takes.
	return col == null ? null : col[i] ?? null;
}

/** Columnar parallel arrays → EffortDataPoint[]. Empty wire → []. */
export function hydrateEffortPoints(wire: EffortPointsWire): EffortDataPoint[] {
	const n = wire.distance.length;
	const points = new Array<EffortDataPoint>(n);
	for (let i = 0; i < n; i++) {
		points[i] = {
			// must(): the wire columns are lockstep by contract (all length n) — a short column used
			// to leak undefined into a number slot; now it throws with the column named.
			distance: must(wire.distance[i], 'wire distance column lockstep'),
			time: must(wire.time[i], 'wire time column lockstep'),
			speed: must(wire.speed[i], 'wire speed column lockstep'),
			altitude: channel(wire.altitude, i),
			lat: channel(wire.lat, i),
			lng: channel(wire.lng, i),
			cumulativeCostKj: channel(wire.cumulativeCostKj, i),
			segmentProgress: channel(wire.segmentProgress, i),
		};
	}
	return points;
}

/** Wire effort → hydrated in-memory effort (metadata copied, points re-hydrated). */
export function hydrateEffort(wire: EffortPerformanceDataWire): EffortPerformanceData {
	return {
		effortId: wire.effortId,
		activityId: wire.activityId,
		// Wire nullables (always-present `| null`) → the hydrated shape's optional strings.
		activityName: wire.activityName ?? undefined,
		activityDate: wire.activityDate ?? undefined,
		elapsedTime: wire.elapsedTime,
		userId: wire.userId,
		userDisplayName: wire.userDisplayName,
		costEstimated: wire.costEstimated,
		costKind: wire.costKind,
		pointsTruncated: wire.pointsTruncated,
		points: hydrateEffortPoints(wire.points),
	};
}
