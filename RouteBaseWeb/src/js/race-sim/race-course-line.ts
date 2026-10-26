/**
 * The course polyline on the map — one feature per leg so each leg wears its own colour, drawn beneath the
 * riders. Split from race-replay.ts: it is a self-contained map concern (add the source/layer once, setData
 * on a checkpoint move) with no other coupling to the page.
 *
 * The grid is the RIDE and the race is a window on it, so the ride outside Start..Finish — the roll-out, the
 * cooldown, a wrong turn at the end — is drawn too, dimmed. Leaving it out was indistinguishable from a
 * course line that had simply stopped.
 */

import { OUT_OF_RACE_COLOR, legSpans, outOfRaceSpans } from './race-profile';
import type { RaceCheckpointDto, RaceCourseDto } from './race-api';

interface CourseSpan {
	readonly fromMetres: number;
	readonly toMetres: number;
	readonly color: string | null;
	readonly dim: boolean;
}

/** The course, drawn one FEATURE PER LEG so each leg wears its own colour. */
function courseFeatures(course: RaceCourseDto, checkpoints: readonly RaceCheckpointDto[]): GeoJSON.Feature[] {
	const legs: CourseSpan[] = legSpans(checkpoints).map((s) => ({ ...s, dim: false }));
	// Out of race first, so a leg is never painted under it. One grid step of tolerance: a span thinner
	// than a node draws nothing but still costs a feature.
	const outside: CourseSpan[] = outOfRaceSpans(checkpoints, course.totalMeters, course.stepMeters)
		.map((s) => ({ ...s, dim: true }));
	// No legs (a race whose checkpoints carry no colours) is one red feature — the same code path, not a
	// special case.
	const drawn: CourseSpan[] = legs.length > 0
		? [...outside, ...legs]
		: [{ fromMetres: 0, toMetres: course.totalMeters, color: null, dim: false }];

	return drawn.map((span) => {
		const from = Math.max(0, Math.floor(span.fromMetres / course.stepMeters));
		const to = Math.min(course.lat.length - 1, Math.ceil(span.toMetres / course.stepMeters));
		const coordinates: [number, number][] = [];
		for (let i = from; i <= to; i++) coordinates.push([course.lng[i] ?? NaN, course.lat[i] ?? NaN]);

		return {
			type: 'Feature' as const,
			// Default (no leg colours) is the app's --acc-red — a literal hex, since MapLibre reads it via ['get','color'].
			properties: { color: span.color ?? (span.dim ? OUT_OF_RACE_COLOR : '#e5484d'), dim: span.dim },
			geometry: { type: 'LineString' as const, coordinates },
		};
	});
}

/**
 * Draw (or, with `redraw`, update) the course line. setData on redraw, never a re-add: re-adding would drop
 * the layer under the riders. It is inserted beneath the riders' halo — the course is context, not a
 * participant.
 */
export function drawCourseLine(
	map: maplibregl.Map,
	course: RaceCourseDto,
	checkpoints: readonly RaceCheckpointDto[],
	redraw = false,
): void {
	const data = { type: 'FeatureCollection' as const, features: courseFeatures(course, checkpoints) };
	const existing = map.getSource('race-course') as maplibregl.GeoJSONSource | undefined;
	if (existing) {
		if (redraw) existing.setData(data as GeoJSON.GeoJSON);
		return;
	}

	map.addSource('race-course', { type: 'geojson', data: data as GeoJSON.GeoJSON });
	map.addLayer(
		{
			id: 'race-course-line',
			type: 'line',
			source: 'race-course',
			paint: {
				'line-color': ['get', 'color'],
				'line-width': ['case', ['get', 'dim'], 2, 3],
				'line-opacity': ['case', ['get', 'dim'], 0.4, 0.75],
			},
		},
		// Beneath the riders: the course is context, not a participant.
		map.getLayer('race-riders-halo') ? 'race-riders-halo' : undefined,
	);
}
