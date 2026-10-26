/**
 * The field, on the map. Reads RaceField's flat position arrays and writes them into one GeoJSON
 * source; one setData per flush, coordinates mutated in place so a frame allocates nothing.
 *
 * TWO sources, and the split is what makes 424 riders viable:
 *
 *   `race-riders`  — every participant, as CIRCLES. Circles are cheap: MapLibre uploads a point buffer
 *                    and the GPU does the rest.
 *   `race-labels`  — ONLY the named few (yours, starred rivals, the leaders), as SYMBOLS. Symbol layers
 *                    run text collision detection every frame, which is superlinear in label count —
 *                    424 of them would drown the map. So the label source stays small by construction,
 *                    and is refreshed on a UI tick rather than per frame.
 *
 * Real riders are drawn bigger and stroked: they are the only ones whose line is TRUE, and they should
 * look it. A simulated rider's position is a model's opinion; a real rider's is a fact.
 */

import { safeCleanupLayers } from '../core/map-utils-shared';
import { createTickThrottle } from '../core/tick-throttle';
import type { RaceField } from './race-field';

export const RIDER_SOURCE_ID = 'race-riders';
export const RIDER_HALO_LAYER_ID = 'race-riders-halo';
export const RIDER_FILL_LAYER_ID = 'race-riders-fill';
export const LABEL_SOURCE_ID = 'race-labels';
export const LABEL_LAYER_ID = 'race-labels-text';

/** Per-participant presentation. Index-aligned with the RaceField's sources. */
export interface RiderMeta {
	readonly name: string;
	readonly color: string;
	/** Real riders (own/friends' GPS) are drawn larger and stroked — their line is the true one. */
	readonly isReal: boolean;
	/** Persisted emphasis: a real rider's size, and drawn in front. See RaceParticipant.Emphasised. */
	readonly emphasised: boolean;
}

interface RiderFeature {
	type: 'Feature';
	properties: {
		idx: number;
		color: string;
		real: boolean;
		finished: boolean;
		/** The FOLLOW halo (one rider). Independent of `dimmed` — see RaceMarkers.setHighlight. */
		highlighted: boolean;
		/** Filtered OUT by the results table: still drawn, still moving, but pushed into the background. */
		dimmed: boolean;
		/** Persisted emphasis: real-rider size, stroked, and sorted to the FRONT. See setEmphasis. */
		emphasised: boolean;
	};
	geometry: { type: 'Point'; coordinates: [number, number] };
}

interface LabelFeature {
	type: 'Feature';
	/** `idx` is the rider index — Hide mode filters this layer on it, exactly as it filters the dots. */
	properties: { name: string; idx: number };
	geometry: { type: 'Point'; coordinates: [number, number] };
}

export interface RaceMarkersOptions {
	/**
	 * Cap on setData calls per second. The position maths is free (0.028 ms/frame for 424 riders); the
	 * cost is MapLibre re-tiling the source, so THIS is the dial that matters. 30 Hz is imperceptible
	 * for markers moving at race speed and halves the upload work of a 60 fps flush.
	 */
	readonly maxUpdatesPerSecond?: number;
	readonly now?: () => number;
}

export interface RaceMarkers {
	/** Point the labels at these participant indices (own / starred / leaders). Keep it small. */
	setLabelled(indices: readonly number[]): void;

	/**
	 * The FOLLOW halo — one rider, blurred ring, fatter dot. Deliberately NOT the same channel as the
	 * group colour/dim below: following a rider and highlighting a filtered group are independent
	 * questions, and sharing one visual would make them fight.
	 */
	setHighlight(index: number, on: boolean): void;

	/**
	 * Repaint riders by index. Colour is already a per-feature property read by a `['get','color']`
	 * paint expression, so this costs nothing beyond the next flush — no field rebuild, no layer churn.
	 * An index absent from the map keeps its current colour.
	 */
	setColors(colors: ReadonlyMap<number, string>): void;

	/**
	 * Emphasise riders by index — a real rider's size, stroked, and sorted in FRONT of the whole field.
	 *
	 * The front half is the point, not a bonus: at 424 riders a painted group is still covered by whoever
	 * happens to be drawn after it, and draw order here is feature order, which is participant id — i.e.
	 * arbitrary. Same free-repaint path as setColors; an index absent from the map keeps its current state.
	 */
	setEmphasis(flags: ReadonlyMap<number, boolean>): void;

	/**
	 * Dim everyone EXCEPT these indices (the results table's Highlight mode). Null clears the dimming.
	 * An empty set dims the whole field, which is the honest rendering of "the filter matched nobody".
	 */
	setDimmed(exceptIndices: readonly number[] | null): void;

	/**
	 * Show ONLY these indices (the results table's Hide mode). Null shows everyone.
	 *
	 * A layer `filter` rather than a rebuild of the feature array: the field keeps advancing and the
	 * positions keep flushing for hidden riders too, so a mode flip back to All is instant and correct
	 * instead of needing the pacing model re-run.
	 */
	setVisible(indices: readonly number[] | null): void;

	/** Flush the field's current positions to the map. Throttled; call it every frame. */
	flush(): void;
	/** Flush regardless of the throttle — after a seek, the markers must not lag the playhead. */
	flushNow(): void;
	destroy(): void;
}

export function createRaceMarkers(
	map: maplibregl.Map,
	field: RaceField,
	meta: readonly RiderMeta[],
	options: RaceMarkersOptions = {},
): RaceMarkers {
	if (meta.length !== field.size) {
		throw new Error(`createRaceMarkers: ${meta.length} meta entries for ${field.size} participants`);
	}

	const now = options.now ?? (() => performance.now());
	const throttle = createTickThrottle(1000 / (options.maxUpdatesPerSecond ?? 30), now);

	const riders: RiderFeature[] = meta.map((m, i) => ({
		type: 'Feature',
		properties: {
			idx: i, color: m.color, real: m.isReal, finished: false,
			highlighted: false, dimmed: false, emphasised: m.emphasised,
		},
		geometry: { type: 'Point', coordinates: [field.lngs[i] ?? NaN, field.lats[i] ?? NaN] },
	}));
	const riderData = { type: 'FeatureCollection' as const, features: riders };

	// The labels are a POOL, not a rebuild. writePositions() used to clear the array and push a fresh
	// object literal per labelled rider per flush — 100-300 objects/s at 30 Hz, from the file whose own
	// header promises "coordinates mutated in place so a frame allocates nothing". Now only setLabelled
	// (a UI tick) resizes it; a flush mutates the coordinates and nothing else.
	let labelled: readonly number[] = [];
	const labelFeatures: LabelFeature[] = [];
	const labelData = { type: 'FeatureCollection' as const, features: labelFeatures };

	map.addSource(RIDER_SOURCE_ID, { type: 'geojson', data: riderData as GeoJSON.GeoJSON });
	map.addSource(LABEL_SOURCE_ID, { type: 'geojson', data: labelData as GeoJSON.GeoJSON });

	// Halo: highlighted riders only. Blur is expensive, so the radius collapses to 0 for everyone else
	// rather than the layer being toggled — one less layer to keep in sync.
	map.addLayer({
		id: RIDER_HALO_LAYER_ID,
		type: 'circle',
		source: RIDER_SOURCE_ID,
		paint: {
			'circle-radius': ['case', ['get', 'highlighted'], 20, 0],
			'circle-color': '#ffffff',
			'circle-blur': 0.6,
			'circle-opacity': ['case', ['get', 'highlighted'], 0.55, 0],
		},
	});

	map.addLayer({
		id: RIDER_FILL_LAYER_ID,
		type: 'circle',
		source: RIDER_SOURCE_ID,
		layout: {
			// DRAW ORDER, and it is load-bearing rather than polish. Without a sort key MapLibre draws
			// circles in feature order — participant id — so which of two overlapping riders you can see is
			// decided by who was imported first. An emphasised rider covered by an anonymous one is the
			// whole feature failing silently.
			//
			// Ranked NO SMALLER THAN the radius list below — emphasised and real are the same 7px, so size
			// alone can no longer say which is in front, and this is the only thing that does. The two
			// branch orders stay identical so a rider can never be drawn small and sorted to the front (or
			// the reverse); race-markers.test.ts pins both halves.
			'circle-sort-key': [
				'case',
				['get', 'dimmed'], 0,
				['get', 'emphasised'], 4,
				['get', 'real'], 3,
				['get', 'highlighted'], 2,
				1,
			],
		},
		paint: {
			// Real riders read as bigger, harder-edged dots; the simulated field is small and soft.
			// `dimmed` is checked FIRST: a rider the table filtered out must recede even if he is real,
			// or the one group you asked to see is the one drowned out by the ones you didn't. Emphasis
			// comes next and beats `real`, because it is the one channel the user set by hand.
			//
			// Emphasised is the SAME 7 as real, deliberately: a third, larger size made the map read as
			// three tiers of importance when there are only two. What emphasis buys over a real rider is
			// the sort key above, not another 3px.
			'circle-radius': [
				'case',
				['get', 'dimmed'], 3,
				['get', 'emphasised'], 7,
				['get', 'real'], 7,
				['get', 'highlighted'], 6,
				4,
			],
			'circle-color': ['get', 'color'],
			'circle-opacity': [
				'case',
				['get', 'dimmed'], 0.15,
				['get', 'finished'], 0.3,
				0.85,
			],
			'circle-stroke-width': [
				'case',
				['get', 'dimmed'], 0,
				['get', 'emphasised'], 2,
				['get', 'real'], 2,
				['get', 'highlighted'], 2,
				0,
			],
			'circle-stroke-color': '#ffffff',
		},
	});

	map.addLayer({
		id: LABEL_LAYER_ID,
		type: 'symbol',
		source: LABEL_SOURCE_ID,
		layout: {
			'text-field': ['get', 'name'],
			'text-size': 12,
			'text-offset': [0, -1.4],
			'text-anchor': 'bottom',
			'text-allow-overlap': false,
		},
		paint: {
			'text-color': '#ffffff',
			'text-halo-color': '#000000',
			'text-halo-width': 1.2,
		},
	});

	function writePositions(): void {
		for (const rider of riders) {
			const i = rider.properties.idx;
			const c = rider.geometry.coordinates;
			c[0] = field.lngs[i] ?? NaN;
			c[1] = field.lats[i] ?? NaN;
			rider.properties.finished = field.finished[i] === 1;
		}
		for (let k = 0; k < labelled.length; k++) {
			const i = labelled[k];
			const lf = labelFeatures[k];
			if (i === undefined || lf === undefined) continue; // lockstep pools — dead branch
			const c = lf.geometry.coordinates;
			c[0] = field.lngs[i] ?? NaN;
			c[1] = field.lats[i] ?? NaN;
		}
	}

	function push(): void {
		const riderSrc = map.getSource(RIDER_SOURCE_ID) as maplibregl.GeoJSONSource | undefined;
		const labelSrc = map.getSource(LABEL_SOURCE_ID) as maplibregl.GeoJSONSource | undefined;
		// The source can be gone if the style reloaded under us — a missing source is not an error.
		riderSrc?.setData(riderData as GeoJSON.GeoJSON);
		labelSrc?.setData(labelData as GeoJSON.GeoJSON);
	}

	return {
		setLabelled(indices) {
			labelled = [...indices];

			// The only place the pool changes shape or content. Grow/shrink to fit, then re-stamp the
			// names — a flush after this only ever writes coordinates.
			//
			// `idx` rides along because Hide mode filters the label LAYER on it, exactly as it filters the
			// dots. Without it a hidden rider kept his name on the map, still being repositioned every
			// flush — a name gliding along the course with nothing underneath it.
			labelFeatures.length = labelled.length;
			for (let k = 0; k < labelled.length; k++) {
				const i = labelled[k];
				if (i === undefined) continue; // labelled just copied — dead branch
				const name = meta[i]?.name ?? '';
				const existing = labelFeatures[k];
				if (existing) {
					existing.properties.name = name;
					existing.properties.idx = i;
				} else {
					labelFeatures[k] = {
						type: 'Feature',
						properties: { name, idx: i },
						geometry: { type: 'Point', coordinates: [0, 0] },
					};
				}
			}
		},
		setHighlight(index, on) {
			const rider = riders[index];
			if (!rider) return;
			rider.properties.highlighted = on;
		},
		setColors(colors) {
			for (const [index, color] of colors) {
				const rider = riders[index];
				if (!rider) continue;
				rider.properties.color = color;
			}
			// No setData here: the next flush pushes the whole FeatureCollection anyway, so a repaint
			// rides along with the positions for free. Callers wanting it now call flushNow().
		},
		setEmphasis(flags) {
			for (const [index, on] of flags) {
				const rider = riders[index];
				if (!rider) continue;
				rider.properties.emphasised = on;
			}
			// Free on the next flush, exactly like setColors — including the re-sort, which MapLibre does
			// when it re-tiles the source it was going to re-tile anyway.
		},
		setDimmed(exceptIndices) {
			if (exceptIndices == null) {
				for (const r of riders) r.properties.dimmed = false;
				return;
			}
			const bright = new Set(exceptIndices);
			for (let i = 0; i < riders.length; i++) {
				const rider = riders[i];
				if (rider) rider.properties.dimmed = !bright.has(i);
			}
		},
		setVisible(indices) {
			// A layer filter, not a feature rebuild: hidden riders keep advancing and keep flushing, so
			// flipping back to All is instant instead of needing the pacing model re-run for 424 riders.
			// Typed off setFilter's own signature — maplibre does not re-export FilterSpecification.
			type MapFilter = Parameters<maplibregl.Map['setFilter']>[1];
			const filter: MapFilter = indices == null
				? null
				: ['in', ['get', 'idx'], ['literal', [...indices]]];

			// The LABELS too — a rider you have hidden must not keep his name on the map. Its features
			// carry `idx` for exactly this (see setLabelled).
			for (const layer of [RIDER_HALO_LAYER_ID, RIDER_FILL_LAYER_ID, LABEL_LAYER_ID]) {
				if (map.getLayer(layer)) map.setFilter(layer, filter);
			}
		},
		flush() {
			if (!throttle.shouldRun()) return;
			writePositions();
			push();
		},
		flushNow() {
			throttle.arm();
			writePositions();
			push();
		},
		destroy() {
			safeCleanupLayers(
				map,
				[RIDER_HALO_LAYER_ID, RIDER_FILL_LAYER_ID, LABEL_LAYER_ID],
				[RIDER_SOURCE_ID, LABEL_SOURCE_ID],
			);
		},
	};
}
