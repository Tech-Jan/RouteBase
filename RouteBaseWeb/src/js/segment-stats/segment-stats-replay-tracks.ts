/**
 * Segment Statistics – Replay Track Layers (MapLibre)
 *
 * Owns the replay polyline rendering as GeoJSON line layers:
 *   - Segment background line (replay-seg-bg)
 *   - Effort track lines      (replay-efforts)
 *
 * Exported as `replayTracksApi`. The core replay engine (segment-stats-replay.ts)
 * calls these functions; marker management lives in segment-stats-replay-markers.ts.
 *
 * Depends on: ctx (shared context from segment-statistics.ts)
 *             MapLibre (maplibregl)
 */
import type { EffortDataPoint } from './segment-stats-effort-types';
import type { ReplayEffort, SegmentStatsReplayTracksApi, TrackDiagnosticSnapshot } from './segment-stats-replay-types';
import { MARKER_HALO_LAYER_ID } from './segment-stats-replay-types';
import type { SegmentStatisticsContext } from './segment-stats-page-types';
import { ctx } from './segment-statistics';
import { safeCleanupLayers } from '../core/map-utils-shared';
import { must } from '../core/invariant';

// The api object is created at module load and populated by the module's own bottom-of-file call
// Every internal function receives the page context via the `ctx` PARAMETER of
// initSegmentStatsReplayTracks rather than reaching for the imported singleton; the singleton is read
// only for that one top-level call, so module-eval order is unchanged.
export const replayTracksApi = {} as SegmentStatsReplayTracksApi;
export function initSegmentStatsReplayTracks(ctx: SegmentStatisticsContext): void {

	// ====================================================================
	// STATE
	// ====================================================================

	let _maplibreMap: maplibregl.Map | null = null;
	let efforts: ReplayEffort[] = [];

	// ====================================================================
	// PUBLIC API
	// ====================================================================

	const api = replayTracksApi;

	/**
	 * Bind this module to a map instance for the current replay session.
	 * Must be called before draw().
	 */
	api.bind = (mapInstance: maplibregl.Map, effortData: ReplayEffort[]): void => {
		_maplibreMap = mapInstance;
		efforts = effortData;
	};

	/**
	 * Draw the segment background line and all effort track lines as GeoJSON
	 * sources + line layers.
	 */
	api.draw = (): void => {
		_drawSegmentBackground();
		_drawEffortTracks();
	};

	/**
	 * Point the tracks module at a new effort array WITHOUT tearing down the map
	 * layers (PS-03 full-res upgrade). Pair with updateEffortTracks() to sharpen
	 * the polyline geometry in place.
	 */
	api.rebind = (effortData: ReplayEffort[]): void => {
		efforts = effortData;
	};

	/**
	 * Refresh the effort-track polylines in place via setData — no remove/re-add,
	 * so the sharpened full-res geometry streams in without the layers flashing.
	 * Falls back to a full draw if the source isn't present yet.
	 */
	api.updateEffortTracks = (): void => {
		if (!_maplibreMap) return;
		const src = _maplibreMap.getSource("replay-efforts") as maplibregl.GeoJSONSource | undefined;
		if (!src) { _drawEffortTracks(); return; }
		src.setData({ type: "FeatureCollection", features: _buildEffortTrackFeatures() });
	};

	// —— Follow track ——————————————————————————————————————————————————————
	// The full path of the currently-followed effort. In heatmap-view replay no base tracks are
	// drawn (markers race over the raster), so following one reveals just its track; unfollow clears it.
	const FOLLOW_SRC = "replay-follow-track";
	const FOLLOW_LAYER = "replay-follow-track-line";
	const FOLLOW_CASING_LAYER = "replay-follow-track-casing";

	api.showEffortTrack = (idx: number): void => {
		if (!_maplibreMap) return;
		const effort = efforts[idx];
		if (!effort) return;
		api.hideEffortTrack(); // replace any previously-followed track
		const geo = {
			type: "Feature" as const,
			properties: {},
			geometry: {
				type: "LineString" as const,
				coordinates: effort.points.map((p: EffortDataPoint) => [p.lng || 0, p.lat || 0]),
			},
		};
		try {
			_maplibreMap.addSource(FOLLOW_SRC, { type: "geojson", data: geo });
			// Insert beneath the marker layers so the racing markers stay on top (RR-02: shared id).
			const beforeId = _maplibreMap.getLayer(MARKER_HALO_LAYER_ID) ? MARKER_HALO_LAYER_ID : undefined;
			// Fixed high-contrast styling (black line + white casing) instead of the effort's own colour,
			// which is random and can vanish into the heatmap raster (e.g. orange over warm cells).
			_maplibreMap.addLayer({
				id: FOLLOW_CASING_LAYER,
				type: "line",
				source: FOLLOW_SRC,
				layout: { "line-join": "round", "line-cap": "round" },
				paint: { "line-color": "#ffffff", "line-width": 7, "line-opacity": 0.9 },
			}, beforeId);
			_maplibreMap.addLayer({
				id: FOLLOW_LAYER,
				type: "line",
				source: FOLLOW_SRC,
				layout: { "line-join": "round", "line-cap": "round" },
				paint: { "line-color": "#000000", "line-width": 3.5, "line-opacity": 1 },
			}, beforeId);
		} catch (e) {
			console.warn("replay follow-track error:", e);
		}
	};

	api.hideEffortTrack = (): void => {
		if (!_maplibreMap) return;
		safeCleanupLayers(_maplibreMap, [FOLLOW_LAYER, FOLLOW_CASING_LAYER], [FOLLOW_SRC]);
	};

	api.setSegmentBackgroundVisible = (visible: boolean): void => {
		if (!_maplibreMap) return;
		try {
			if (_maplibreMap.getLayer("replay-seg-bg-line")) {
				_maplibreMap.setPaintProperty("replay-seg-bg-line", "line-opacity", visible ? 0.3 : 0);
			}
		} catch (e: unknown) { console.warn('[replay-tracks] setPaintProperty failed:', e); }
	};

	/**
	 * Remove all track layers. Called by the core engine on destroy().
	 */
	api.destroyAll = (): void => {
		_removePolylines();
		_maplibreMap = null;
		efforts = [];
	};

	/**
	 * Return a diagnostic snapshot of track state for the perf logger.
	 */
	api.getDiagnosticSnapshot = (): TrackDiagnosticSnapshot => {
		const snap: TrackDiagnosticSnapshot = {
			mlTrackSources: 0,
			mlTrackLayers: 0,
		};

		if (_maplibreMap) {
			try {
				if (_maplibreMap.getSource("replay-seg-bg")) snap.mlTrackSources++;
				if (_maplibreMap.getSource("replay-efforts")) snap.mlTrackSources++;
				if (_maplibreMap.getLayer("replay-seg-bg-line")) snap.mlTrackLayers++;
				if (_maplibreMap.getLayer("replay-efforts-line")) snap.mlTrackLayers++;
			} catch (e: unknown) { console.warn('[replay-tracks] getDiagnosticSnapshot failed:', e); }
		}

		return snap;
	};

	// ====================================================================
	// INTERNAL – MAP DRAWING
	// ====================================================================

	function _drawSegmentBackground(): void {
		const segCoords = ctx.config.segmentCoords;
		if (!segCoords || segCoords.length === 0 || !_maplibreMap) return;

		const geo = {
			type: "Feature" as const,
			properties: {},
			geometry: {
				type: "LineString" as const,
				coordinates: segCoords.map((c: number[]) => [c[1] ?? NaN, c[0] ?? NaN]),
			},
		};
		// RR-03: no inline pre-emptive teardown — api.init always calls api.destroy() first
		// (replay.ts), so _removePolylines has already cleared replay-seg-bg before draw() runs.
		try {
			_maplibreMap.addSource("replay-seg-bg", { type: "geojson", data: geo });
			_maplibreMap.addLayer({
				id: "replay-seg-bg-line",
				type: "line",
				source: "replay-seg-bg",
				paint: {
					"line-color": "#888",
					"line-width": 4,
					"line-opacity": 0.3,
					"line-dasharray": [4, 6],
				},
			});
		} catch (e) {
			console.warn("replay bg polyline error:", e);
		}
	}

	function _buildEffortTrackFeatures(): GeoJSON.Feature[] {
		const features: GeoJSON.Feature[] = [];
		for (let j = 0; j < efforts.length; j++) {
			const effort = must(efforts[j], 'j < length (loop-bounded)');
			features.push({
				type: "Feature",
				properties: { color: effort.color, _idx: j },
				geometry: {
					type: "LineString",
					coordinates: effort.points.map((p: EffortDataPoint) => [p.lng || 0, p.lat || 0]),
				},
			});
		}
		return features;
	}

	function _drawEffortTracks(): void {
		if (!_maplibreMap) return;
		const features = _buildEffortTrackFeatures();
		try {
			_maplibreMap.addSource("replay-efforts", {
				type: "geojson",
				data: { type: "FeatureCollection", features: features },
			});
			_maplibreMap.addLayer({
				id: "replay-efforts-line",
				type: "line",
				source: "replay-efforts",
				layout: { "line-join": "round", "line-cap": "round" },
				paint: {
					"line-color": ["get", "color"],
					"line-width": 3,
					"line-opacity": 0.5,
				},
			});
		} catch (e) {
			console.warn("replay effort polyline error:", e);
		}
	}

	// ====================================================================
	// INTERNAL – CLEANUP
	// ====================================================================

	function _removePolylines(): void {
		if (!_maplibreMap) return;
		// RR-04: all effort tracks live in the single collective "replay-efforts" source — the old
		// per-effort "replay-effort-{j}" loop was vestigial (nothing creates those ids), costing
		// ~2 no-op lookups per effort on every teardown. One shared cleanup is the whole story.
		safeCleanupLayers(
			_maplibreMap,
			["replay-seg-bg-line", "replay-efforts-line", "replay-follow-track-line", "replay-follow-track-casing"],
			["replay-seg-bg", "replay-efforts", "replay-follow-track"],
		);
	}
} // end initSegmentStatsReplayTracks

if (!ctx) { throw new Error("segment-stats-replay-tracks: no ctx"); }
initSegmentStatsReplayTracks(ctx);
