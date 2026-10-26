/**
 * Segment Statistics — Map → Chart hover tracking (engine-agnostic)
 *
 * rAF-throttled proximity hit-testing: finds the effort GPS point nearest
 * to the mouse position and drives chart active elements + position markers.
 *
 * The map engine is reached through a small adapter (project lat/lng → container
 * pixels, drag start/end) so the algorithm is independent of the map library.
 * segment-stats-map.ts binds a MapLibre adapter in onMapLibreReady.
 *
 * H1: adapter.project() is called O(1) times per frame (anchor + two axis probes
 * that linearize the projection at the mouse position) — NEVER per GPS point.
 * MapLibre's project() runs a DEM elevation lookup per call when terrain is on;
 * projecting every point of every full-resolution effort track froze the browser
 * for seconds per mousemove (regression from the SS-03 Leaflet port).
 *
 * Receives: ctx (shared context) as the initSegmentStatsMapHover(ctx) parameter — the api methods and
 *           the private _executeMapHover(ctx) hit-test read it through the parameter, not the import.
 * Depends on: seg-stats-shared slots (getMapShared / getReplayApi / getSegmentReplayActive) —
 *             registered by map.ts + replay.ts; read lazily here to avoid a dep cycle with them.
 */
import type { TrackPoint, ChartJsInstance } from '../core/core-types';
import type { SegStatsHoverMapAdapter, SegmentStatsMapHoverApi } from './segment-stats-map-types';
import type { SegmentStatisticsContext } from './segment-stats-page-types';
import { MAP_THROTTLE_MS } from '../core/constants';
import { ctx } from './segment-statistics';
import { mapDataApi } from './segment-stats-map-data';
import { getMapShared, getReplayApi, getSegmentReplayActive } from './seg-stats-shared';
import { must } from '../core/invariant';
if (!ctx) {
	throw new Error("segment-stats-map-hover: no ctx");
}

// Resolved at bind() time — the engine adapter (project + drag hooks).
let _adapter: SegStatsHoverMapAdapter | null = null;

// ====================================================================
// STATE
// ====================================================================

let _lastHoverEffortIdx = -1;
let _lastHoverPointIdx = -1;
let _hoverRafId = 0;
// Geographic mouse position from the map mousemove (MapLibre e.lngLat); the
// hit-test runs against it in a locally-linearized pixel space on the next rAF.
let _pendingHoverLngLat: { lng: number; lat: number } | null = null;
let _lastMoveTime = 0;
const _THROTTLE_MS = MAP_THROTTLE_MS;

// MH-04: on-segment [startIdx, endIdx] span within each effort's FULL activity track, so the
// hover ratio maps to segment-scoped perf points (not the whole activity). Keyed by effort index,
// revalidated by gps-array identity (progressive reloads / bfcache swap the array object).
const _splitCache = new Map<number, { gps: TrackPoint[]; startIdx: number; endIdx: number }>();

// True while the user is dragging the map. The O(N×M) hit-test loop below fights
// the map's own pan render frame-for-frame — suppressing it during drag keeps the
// pan smooth.
let _isPanning = false;

// Touch devices: there is no true hover — "mousemove" only arrives as a tap-synthesized
// event, so running the O(efforts × points) projection loop there is pure jank with no
// hover UX to gain. Chart scrubbing on mobile has its own touch path.
const _IS_TOUCH = typeof window !== 'undefined' && window.matchMedia?.('(hover: none)').matches === true;

// ====================================================================
// PUBLIC API
// ====================================================================

const api = {} as SegmentStatsMapHoverApi;
export const mapHoverApi = api;

/**
 * Populate the map→chart hover api. Hoisted out of the module top level (the api-method assignments
 * ran bare on import) so each method receives the shared context via the `ctx` parameter instead of
 * the module-level singleton import (the private hit-test `_executeMapHover` takes it as a parameter
 * too). Import-time semantics are unchanged: `api` + `mapHoverApi` stay module-level, and the
 * top-level call at the bottom passes the shared ctx exactly as before.
 */
export function initSegmentStatsMapHover(ctx: SegmentStatisticsContext): void {

	api.bind = (adapter: SegStatsHoverMapAdapter): void => {
		_adapter = adapter;
		// Gate the hit-test on drag state: stop the per-frame projection loop while
		// the user pans, drop any queued hover, and clear the chart highlight.
		adapter.onDragStart(() => {
			_isPanning = true;
			api.resetState();
			if (_lastHoverEffortIdx !== -1) {
				_lastHoverEffortIdx = -1;
				_lastHoverPointIdx = -1;
				ctx.onChartLeave?.();
			}
		});
		adapter.onDragEnd(() => { _isPanning = false; });
	};

	/**
	 * Enqueue the geographic mouse position for rAF processing.
	 * Call from the map mousemove listener (MapLibre hands one over as e.lngLat).
	 */
	api.handleMouseMove = (lngLat: { lng: number; lat: number }): void => {
		if (!_adapter) return;
		if (_IS_TOUCH) return; // no hover on touch — skip the heavy hit-test entirely
		if (_isPanning) return; // skip the heavy hit-test mid-pan (see _isPanning)
		const shared = getMapShared();
		if (!shared || !shared.effortGpsArrays.some((a) => a && a.length > 0) || !ctx.performanceData) return;

		// Skip during replay — the fullscreen overlay has pointer-events: none
		// so mousemove events pass straight through to the map. The O(N×M)
		// hit-testing loop below is extremely expensive at 60fps.
		if (getSegmentReplayActive()) return;
		const replayApi = getReplayApi();
		if (replayApi && replayApi.isPlaying()) return;

		// Throttle: skip if called too recently
		const now = performance.now();
		if (now - _lastMoveTime < _THROTTLE_MS) return;
		_lastMoveTime = now;

		_pendingHoverLngLat = lngLat;
		if (!_hoverRafId) {
			_hoverRafId = requestAnimationFrame(() => _executeMapHover(ctx));
		}
	};

	/** Reset all hover state — call whenever layers change so stale indices don't block updates. */
	api.resetState = (): void => {
		_lastHoverEffortIdx = -1;
		_lastHoverPointIdx = -1;
		if (_hoverRafId) {
			cancelAnimationFrame(_hoverRafId);
			_hoverRafId = 0;
		}
		_pendingHoverLngLat = null;
	};

} // end initSegmentStatsMapHover

// ====================================================================
// PRIVATE
// ====================================================================

function _executeMapHover(ctx: SegmentStatisticsContext): void {
	_hoverRafId = 0;
	const mouse = _pendingHoverLngLat;
	if (!mouse || !_adapter) return;

	const shared = getMapShared();
		if (!shared) return;
		const effortGpsArrays = shared.effortGpsArrays;

		// Linearize the projection at the mouse position: 3 project() calls give
		// pixels-per-degree vectors, so every GPS point becomes a pure-arithmetic
		// screen offset from the cursor. Exact to first order (incl. rotation/pitch)
		// over the 30px hover radius; avoids the per-point project() that froze the
		// page (H1 — see header).
		const PROBE_DEG = 1e-4; // ~11 m
		const p0 = _adapter.project(mouse.lat, mouse.lng);
		const pLat = _adapter.project(mouse.lat + PROBE_DEG, mouse.lng);
		const pLng = _adapter.project(mouse.lat, mouse.lng + PROBE_DEG);
		if (!p0 || !pLat || !pLng) return;
		const latPxX = (pLat.x - p0.x) / PROBE_DEG,
			latPxY = (pLat.y - p0.y) / PROBE_DEG;
		const lngPxX = (pLng.x - p0.x) / PROBE_DEG,
			lngPxY = (pLng.y - p0.y) / PROBE_DEG;

		let bestDist = Infinity,
			bestEffortIdx = -1,
			bestPointIdx = -1;
		const MAX_PX = 30;

		effortGpsArrays.forEach((gps: TrackPoint[], ei: number) => {
			if (!gps || !ctx.effortVisibility.has(ei)) return;
			for (let pi = 0; pi <= gps.length - 1; pi += 3) {
				const p = must(gps[pi], 'pi < length (loop-bounded)');
				const dLat = p.lat - mouse.lat;
				let dLng = p.lng - mouse.lng;
				if (dLng > 180) dLng -= 360;
				else if (dLng < -180) dLng += 360; // antimeridian wrap
				const dx = lngPxX * dLng + latPxX * dLat;
				const dy = lngPxY * dLng + latPxY * dLat;
				const d = dx * dx + dy * dy;
				if (!(d < bestDist)) continue; // NaN-safe: bad coords never win
				bestDist = d;
				bestEffortIdx = ei;
				bestPointIdx = pi;
			}
		});

		// Compare with squared MAX_PX threshold
		if (bestDist > MAX_PX * MAX_PX || bestEffortIdx === -1) {
			if (_lastHoverEffortIdx !== -1) {
				_lastHoverEffortIdx = -1;
				_lastHoverPointIdx = -1;
				ctx.onChartLeave?.();
			}
			return;
		}

		// Same-index skip guard
		if (bestEffortIdx === _lastHoverEffortIdx && bestPointIdx === _lastHoverPointIdx) return;
		_lastHoverEffortIdx = bestEffortIdx;
		_lastHoverPointIdx = bestPointIdx;

		const perfData = ctx.performanceData;
		if (!perfData || bestEffortIdx >= perfData.length) return;
		const gps = must(effortGpsArrays[bestEffortIdx], 'bestEffortIdx found in this array');
		const perfPoints = must(perfData[bestEffortIdx], 'bestEffortIdx < length (guarded above)').points;

		// MH-04: effortGpsArrays holds the FULL activity track; perfPoints are SEGMENT-scoped. Map the
		// hovered whole-track index onto the on-segment span so the chart point/marker match the cursor
		// even when the segment isn't the entire activity. Off-segment hover clamps to the nearest end.
		// Falls back to the whole-track fraction when effort metadata isn't available (no crash).
		const eff = shared.efforts?.[bestEffortIdx];
		let ratio: number;
		if (eff) {
			let cached = _splitCache.get(bestEffortIdx);
			if (!cached || cached.gps !== gps) {
				const { startIdx, endIdx } = mapDataApi.splitSegmentIndices(gps, eff, shared.segmentCoords);
				cached = { gps, startIdx, endIdx };
				_splitCache.set(bestEffortIdx, cached);
			}
			const span = Math.max(1, cached.endIdx - cached.startIdx);
			ratio = Math.min(1, Math.max(0, (bestPointIdx - cached.startIdx) / span));
		} else {
			ratio = bestPointIdx / Math.max(1, gps.length - 1);
		}
		const perfIdx = Math.min(
			Math.round(ratio * (perfPoints.length - 1)),
			perfPoints.length - 1,
		);

		// Show the segment-position marker at the corresponding point on the segment
		const perfPt = perfPoints[perfIdx];
		if (perfPt && perfPt.lat != null && perfPt.lng != null) {
			ctx.showSegmentPositionMarker?.(perfPt.lat, perfPt.lng);
		}

		// Update performance + elevation + distance charts.
		// SH-01: each call is independently guarded (ID-16 stale-element throw swallowed) so a throw in
		// one chart can no longer abort the whole function and desync the others. No draw() — hover.mode
		// is null so Chart.js won't repaint; the hover strip provides the value display.
		[ctx.performanceChart, ctx.elevationChart, ctx.effortChart].forEach((chart: ChartJsInstance | null) => {
			const ds = chart ? chart.data.datasets[bestEffortIdx] : undefined;
			if (!chart || !ds || ds.hidden) return;
			ctx._trySetChartActive?.(chart, bestEffortIdx, perfIdx);
		});

		// SH-06: the `comparisonChart` block was removed — `ctx.comparisonChart` is never assigned
		// anywhere in segment-stats/, so it was permanently dead. The field is gone from the ctx type.

		if (ctx.progressChart && bestEffortIdx < ctx.progressChart.data.datasets.length) {
			ctx._trySetChartActive?.(ctx.progressChart, bestEffortIdx, 0);
		}
	}

// Top-level call — populates `api` at import, exactly as the bare assignments did before the hoist.
// The guard above already threw if ctx is absent; the singleton ctx is passed as the received parameter.
initSegmentStatsMapHover(ctx);
