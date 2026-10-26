/**
 * Segment Statistics — pure MapLibre paint/layout spec factories.
 *
 * Extracted from segment-stats-map-maplibre.ts: the paint/layout literals and the
 * data-driven line-width/opacity expressions were inline in addPolyline and
 * _addCombinedEffortLayer. They hold no map handle, so they live here — greppable and
 * snapshot-testable without a map. The engine keeps every map.addSource|addLayer|on and
 * the hover/leave/click handlers; only the pure spec VALUES moved.
 *
 * The raw expressions return `unknown` because they feed maplibregl.setPaintProperty
 * (value: any) — the `maplibregl` ambient namespace does not alias ExpressionSpecification.
 * The base-line expressions are duplicated once, inline, inside combinedEffortLayerSpec's
 * strictly-typed paint; the sync test pins the two copies identical so they can't drift.
 */

/** Line layer spec for a segment/background polyline. `width`/`opacity` fall back to 4/0.8 (0 → default). */
export function polylineLayerSpec(
	id: string,
	srcId: string,
	color: string,
	width: number,
	opacity: number,
	dashArray?: number[],
): maplibregl.AddLayerObject {
	const paint: Record<string, unknown> = {
		"line-color": color,
		"line-width": width || 4,
		"line-opacity": opacity || 0.8,
	};
	if (dashArray) {
		paint["line-dasharray"] = dashArray;
	}
	return {
		id: id,
		type: "line",
		source: srcId,
		layout: { "line-join": "round", "line-cap": "round" },
		paint: paint,
	};
}

/**
 * Combined effort-tracks line layer. Data-driven styling: on-segment portions (`_onSeg`=1)
 * are full opacity/weight, off-segment portions (`_onSeg`=0) are dimmed. The base line-width /
 * line-opacity here MUST match effortBaseLineWidth()/effortBaseLineOpacity() (the hover-leave
 * reset) — the sync test enforces it.
 */
export function combinedEffortLayerSpec(layerId: string, sourceId: string): maplibregl.AddLayerObject {
	return {
		id: layerId,
		type: "line",
		source: sourceId,
		layout: { "line-join": "round", "line-cap": "round" },
		paint: {
			"line-color": ["get", "color"],
			"line-width": ["case", ["==", ["get", "_onSeg"], 1], 4, 2],
			"line-opacity": ["case", ["==", ["get", "_onSeg"], 1], 0.8, 0.25],
		},
	};
}

/** Reset (non-hovered) line-width: on-segment full weight (4), off-segment dim (2). */
export function effortBaseLineWidth(): unknown {
	return ["case", ["==", ["get", "_onSeg"], 1], 4, 2];
}

/** Reset (non-hovered) line-opacity: on-segment full (0.8), off-segment dim (0.25). */
export function effortBaseLineOpacity(): unknown {
	return ["case", ["==", ["get", "_onSeg"], 1], 0.8, 0.25];
}

/**
 * Hover line-width for the effort under the cursor (`hoveredIdx` = its `_effortIndex`):
 * hovered+on-segment thickest (5), hovered off-segment (3), other on-segment base (4), else (2).
 */
export function effortHoverLineWidth(hoveredIdx: unknown): unknown {
	return [
		"case",
		[
			"all",
			["==", ["get", "_effortIndex"], hoveredIdx],
			["==", ["get", "_onSeg"], 1],
		],
		5,
		["==", ["get", "_effortIndex"], hoveredIdx],
		3,
		["==", ["get", "_onSeg"], 1],
		4,
		2,
	];
}

/**
 * Hover line-opacity for the effort under the cursor: hovered+on-segment full (1.0),
 * hovered off-segment (0.4), other on-segment base (0.8), else dim (0.25).
 */
export function effortHoverLineOpacity(hoveredIdx: unknown): unknown {
	return [
		"case",
		[
			"all",
			["==", ["get", "_effortIndex"], hoveredIdx],
			["==", ["get", "_onSeg"], 1],
		],
		1.0,
		["==", ["get", "_effortIndex"], hoveredIdx],
		0.4,
		["==", ["get", "_onSeg"], 1],
		0.8,
		0.25,
	];
}
