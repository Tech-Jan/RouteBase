/**
 * Segment Statistics — pure effort-track GeoJSON builder.
 *
 * Extracted from segment-stats-map-maplibre.ts: a pure GPS→GeoJSON transform with no
 * map handle. Given a track, its effort metadata, its display index, and the pre-computed
 * on-segment split (from mapDataApi.splitSegmentIndices — the engine owns that impure call),
 * it produces the split-by-`_onSeg` LineString features the combined effort source is fed.
 *
 * On-segment portions (`_onSeg: 1`) render full opacity/weight; the pre/post portions
 * (`_onSeg: 0`) render dim. See _addCombinedEffortLayer's data-driven paint.
 */
import type { TrackPoint } from '../core/core-types';
import type { SegmentEffort } from './segment-stats-effort-types';

/**
 * Build the GeoJSON features for one effort, split into on-segment (full opacity)
 * and off-segment (dim) portions using the _onSeg property for data-driven styling.
 */
export function buildEffortFeatures(
	gps: TrackPoint[],
	effort: SegmentEffort,
	idx: number,
	split: { startIdx: number; endIdx: number },
): GeoJSON.Feature[] {
	const startIdx = split.startIdx,
		endIdx = split.endIdx;
	const features: GeoJSON.Feature[] = [];

	function toCoord(p: TrackPoint): number[] {
		return [p.lng, p.lat];
	}
	const baseProps = {
		color: effort.color,
		_effortIndex: idx,
		name: effort.name || "",
		date: effort.date || "",
		rank: effort.rank || 0,
	};

	// Pre-segment portion (dim)
	if (startIdx > 0) {
		features.push({
			type: "Feature",
			properties: Object.assign({}, baseProps, { _onSeg: 0 }),
			geometry: {
				type: "LineString",
				coordinates: gps.slice(0, startIdx + 1).map(toCoord),
			},
		});
	}
	// On-segment portion (full)
	features.push({
		type: "Feature",
		properties: Object.assign({}, baseProps, { _onSeg: 1 }),
		geometry: {
			type: "LineString",
			coordinates: gps.slice(startIdx, endIdx + 1).map(toCoord),
		},
	});
	// Post-segment portion (dim)
	if (endIdx < gps.length - 1) {
		features.push({
			type: "Feature",
			properties: Object.assign({}, baseProps, { _onSeg: 0 }),
			geometry: {
				type: "LineString",
				coordinates: gps.slice(endIdx).map(toCoord),
			},
		});
	}
	return features;
}
