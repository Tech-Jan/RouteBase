/**
 * Segment Statistics — Replay Effort State Machine
 *
 * Owns the effort list and per-effort state for the multi-effort replay engine.
 * Responsibilities:
 *   - Building ReplayEffort[] from raw EffortPerformanceData (data prep)
 *   - Maintaining ReplayEffortState[] across start / seek / advance calls
 *   - Advancing per-effort positions each animation frame
 *   - Providing interpolated data and progress queries
 *
 * Depends on: ctx (for effortColors)
 */
import type { EffortPerformanceData } from './segment-stats-effort-types';
import type { ReplayEffort, ReplayMode, ReplayEffortState, ReplayPointSample, SegmentStatsReplayEffortsApi, SegmentStatsReplayMarkersApi } from './segment-stats-replay-types';
import { must } from '../core/invariant';
import { ctx } from './segment-statistics';
if (!ctx) { throw new Error("segment-stats-replay-efforts: no ctx"); }

	// ====================================================================
	// INTERNAL STATE
	// ====================================================================

	let _efforts: ReplayEffort[] = [];
	let _effortStates: ReplayEffortState[] = [];
	let _maxTime = 0;
	let _finishCounter = 0;
	// Sync domain. "time": each effort advances by wall-clock time (faster efforts pull
	// ahead). "percent": all efforts advance by the SAME segment-completion fraction, so
	// at each frame every marker sits at the same % along the segment and the sidebar gap
	// reads as "time to reach this %". The playhead stays in the seconds domain [0,_maxTime]
	// either way; percent mode maps it to a shared target completion = currentTime/_maxTime.
	let _mode: ReplayMode = "time";

	// Target completion percentage [0,100] shared by all efforts at the given playhead.
	function _targetPct(currentTime: number): number {
		return _maxTime > 0 ? (currentTime / _maxTime) * 100 : 0;
	}

	// ====================================================================
	// PUBLIC API
	// ====================================================================

	const api = {} as SegmentStatsReplayEffortsApi;
	export const replayEffortsApi = api;

	// ────────────────────────────────────────────────────────────────────
	// Build
	// ────────────────────────────────────────────────────────────────────

	/**
	 * Convert raw EffortPerformanceData into the internal ReplayEffort list.
	 * Only entries with ≥2 GPS points are included. Also initialises
	 * ReplayEffortState for each effort.
	 */
	api.build = (
		perfData: EffortPerformanceData[],
		visibleIndices: number[],
		effortColors: string[],
	): void => {
		_efforts = [];
		_effortStates = [];
		_maxTime = 0;
		_finishCounter = 0;
		_mode = "time";

		for (let vi = 0; vi < visibleIndices.length; vi++) {
			const idx = visibleIndices[vi];
			// undefined >= n was false and fell through — the new arm is dead at runtime (bucket B).
			if (idx === undefined || idx >= perfData.length) continue;
			const pd = must(perfData[idx], 'idx < length (guarded above)');

			// Accumulate _maxTime from the elapsedTime METADATA before the <2-point skip, so the
			// timeline spans every selected effort from t=0 even while some efforts are still
			// streaming in full-res (PS-03 lazy replay). Hoisted above the skip; pinned.
			const elapsed = pd.elapsedTime || (pd.points?.length ? pd.points.at(-1)?.time ?? 0 : 0) || 0;
			if (elapsed > _maxTime) _maxTime = elapsed;

			if (!pd.points || pd.points.length < 2) continue;

			const color = must(effortColors[idx % effortColors.length], 'in-bounds modulo of non-empty palette');
			const pts = pd.points;
			const lastTime = elapsed;

			// Per-point completion % (0–100), monotonic. Mirrors the "% Complete" chart axis:
			// backend segmentProgress when present, else a per-effort distance fraction.
			const totalDist = must(pts.at(-1), 'length >= 2 (guarded above)').distance;
			const progress = new Array<number>(pts.length);
			let prevP = 0;
			for (let k = 0; k < pts.length; k++) {
				const pt = must(pts[k], 'k < length (loop-bounded)');
				// RR-01: a zero-net-distance effort (degenerate GPS / matching artifact, totalDist === 0)
				// would pin every progress[k] to 0 in the distance branch, so percent mode marks it
				// "finished" at frame 0. Fall back to a monotonic index ramp so it still advances visually.
				let p = pt.segmentProgress != null
					? pt.segmentProgress * 100
					: (totalDist > 0
						? (pt.distance / totalDist) * 100
						: (pts.length > 1 ? (k / (pts.length - 1)) * 100 : 100));
				if (p < prevP) p = prevP; // guard against any non-monotonic noise
				prevP = p;
				progress[k] = p;
			}

			_efforts.push({
				sourceIndex: idx,
				name: pd.activityName || "Effort #" + (idx + 1),
				date: pd.activityDate || "",
				color,
				points: pts,
				elapsedTime: lastTime,
				totalDistance: totalDist,
				progress,
			});

			const p0 = must(pts[0], 'length >= 2 (guarded above)');
			_effortStates.push({
				currentIndex: 0,
				finished: false,
				finishOrder: 0,
				finishTime: 0,
				lastLat: p0.lat ?? 0,
				lastLng: p0.lng ?? 0,
			});
		}
	};

	// ────────────────────────────────────────────────────────────────────
	// Accessors
	// ────────────────────────────────────────────────────────────────────

	api.getEfforts = (): ReplayEffort[] => _efforts;
	api.getEffortStates = (): ReplayEffortState[] => _effortStates;
	api.getMaxTime = (): number => _maxTime;
	api.setMode = (mode: ReplayMode): void => { _mode = mode; };
	api.getMode = (): ReplayMode => _mode;

	// ────────────────────────────────────────────────────────────────────
	// State reset
	// ────────────────────────────────────────────────────────────────────

	/** Full reset: rewind every effort to index 0 (called on start). */
	api.reset = (): void => {
		_finishCounter = 0;
		for (let i = 0; i < _efforts.length; i++) {
			const e = must(_efforts[i], 'i < length (loop-bounded)');
			const st = must(_effortStates[i], 'effortStates lockstep with efforts');
			const p0 = must(e.points[0], 'points.length >= 2 (build invariant)');
			st.currentIndex = 0;
			st.finished = false;
			st.finishOrder = 0;
			st.finishTime = 0;
			st.lastLat = p0.lat ?? 0;
			st.lastLng = p0.lng ?? 0;
		}
	};

	/**
	 * Seek reset: mark efforts as finished or unfinished based on currentTime,
	 * reset indices to 0, and update marker states.
	 */
	api.seekReset = (currentTime: number, M: SegmentStatsReplayMarkersApi): void => {
		_finishCounter = 0;
		const percent = _mode === "percent";
		const target = percent ? _targetPct(currentTime) : currentTime;
		for (let i = 0; i < _efforts.length; i++) {
			const e = must(_efforts[i], 'i < length (loop-bounded)');
			const st = must(_effortStates[i], 'effortStates lockstep with efforts');
			// Domain end: full completion (~100) in percent mode, else the effort's finish time.
			// ?? NaN dead (progress lockstep with points): target < NaN is false → the effort is
			// treated finished, same as the undefined comparison before.
			const domEnd = percent ? e.progress[e.points.length - 1] ?? NaN : e.elapsedTime;
			if (target < domEnd) {
				st.finished = false;
				st.finishOrder = 0;
				st.finishTime = 0;
				st.currentIndex = 0;
				M.setMarkerFinished(i, false);
			} else {
				_finishCounter++;
				st.finished = true;
				st.finishOrder = _finishCounter;
				st.finishTime = e.elapsedTime;

				// Snap the marker to the finish point. updateAllMarkers →
				// _advanceEffort returns early for finished efforts, so without
				// this the marker would stay at its last known location.
				const lastPt = must(e.points.at(-1), 'points.length >= 2 (build invariant)');
				st.currentIndex = e.points.length - 1;
				st.lastLat = lastPt.lat ?? 0;
				st.lastLng = lastPt.lng ?? 0;
				M.setMarkerPosition(i, lastPt.lat ?? 0, lastPt.lng ?? 0);
				M.setMarkerFinished(i, true);
			}
		}
	};

	// ────────────────────────────────────────────────────────────────────
	// Per-frame advancement
	// ────────────────────────────────────────────────────────────────────

	/**
	 * Advance all effort states to currentTime. Calls M to reposition markers
	 * and fires onEffortFinish for newly completed efforts.
	 */
	api.advance = (
		currentTime: number,
		M: SegmentStatsReplayMarkersApi,
		onEffortFinish: ((idx: number, order: number, time: number) => void) | null,
	): { allFinished: boolean; activeCount: number } => {
		let activeCount = 0;
		let allFinished = true;

		for (let i = 0; i < _efforts.length; i++) {
			if (!must(_effortStates[i], 'effortStates lockstep with efforts').finished) {
				activeCount++;
				allFinished = false;
			}
			_advanceEffort(i, currentTime, M, onEffortFinish);
		}

		// Re-check allFinished after advancing (some may have just finished)
		allFinished = true;
		for (let j = 0; j < _effortStates.length; j++) {
			if (!must(_effortStates[j], 'j < length (loop-bounded)').finished) { allFinished = false; break; }
		}

		return { allFinished, activeCount };
	};

	/** Force all markers to their interpolated position at currentTime. */
	api.updateAllMarkers = (currentTime: number, M: SegmentStatsReplayMarkersApi): void => {
		for (let i = 0; i < _efforts.length; i++) {
			_advanceEffort(i, currentTime, M, null);
		}
		M.flushMarkerGeoSource();
	};

	// ────────────────────────────────────────────────────────────────────
	// Private: single-effort advancement
	// ────────────────────────────────────────────────────────────────────

	function _advanceEffort(
		effortIdx: number,
		currentTime: number,
		M: SegmentStatsReplayMarkersApi,
		onEffortFinish: ((idx: number, order: number, time: number) => void) | null,
	): void {
		const effort = must(_efforts[effortIdx], 'effortIdx in-bounds (caller loops)');
		const state = must(_effortStates[effortIdx], 'effortStates lockstep with efforts');
		const points = effort.points;

		if (state.finished) return;

		// Domain value the marker is chasing: shared completion % in percent mode, else
		// this effort's own elapsed time. `dom(k)` reads the same domain at point k.
		// ?? NaN in this per-frame kernel: k stays inside [lo, hi] and progress is lockstep with
		// points — dead fallbacks, and NaN compares false exactly as undefined did.
		const percent = _mode === "percent";
		const prog = effort.progress;
		const target = percent ? _targetPct(currentTime) : currentTime;
		const domEnd = percent ? prog[points.length - 1] ?? NaN : effort.elapsedTime;
		const dom = (k: number): number => (percent ? prog[k] : points[k]?.time) ?? NaN;

		if (target >= domEnd) {
			state.finished = true;
			_finishCounter++;
			state.finishOrder = _finishCounter;
			state.finishTime = effort.elapsedTime;

			const lastPt = must(points.at(-1), 'points.length >= 2 (build invariant)');
			M.setMarkerPosition(effortIdx, lastPt.lat ?? 0, lastPt.lng ?? 0);
			state.lastLat = lastPt.lat ?? 0;
			state.lastLng = lastPt.lng ?? 0;
			M.setMarkerFinished(effortIdx, true);

			if (onEffortFinish) onEffortFinish(effortIdx, state.finishOrder, state.finishTime);
			return;
		}

		let lo = state.currentIndex;
		const hi = points.length - 1;

		while (lo < hi - 1 && dom(lo + 1) <= target) {
			lo++;
		}

		if (lo >= hi) {
			const pt = must(points[hi], 'hi = length-1 (in-bounds)');
			M.setMarkerPosition(effortIdx, pt.lat ?? 0, pt.lng ?? 0);
			state.currentIndex = hi;
			state.lastLat = pt.lat ?? 0;
			state.lastLng = pt.lng ?? 0;
			return;
		}

		const p1 = must(points[lo], 'lo < hi (in-bounds)');
		const p2 = must(points[lo + 1], 'lo+1 <= hi (in-bounds)');
		const span = dom(lo + 1) - dom(lo);
		let frac = span > 0 ? (target - dom(lo)) / span : 0;
		frac = Math.max(0, Math.min(1, frac));

		const p1Lat = p1.lat ?? 0, p1Lng = p1.lng ?? 0;
		const lat = p1Lat + ((p2.lat ?? 0) - p1Lat) * frac;
		const lng = p1Lng + ((p2.lng ?? 0) - p1Lng) * frac;

		M.setMarkerPosition(effortIdx, lat, lng);
		state.currentIndex = lo;
		state.lastLat = lat;
		state.lastLng = lng;
	}

	// ────────────────────────────────────────────────────────────────────
	// Queries
	// ────────────────────────────────────────────────────────────────────

	api.getProgressPct = (currentTime: number): number =>
		_maxTime > 0 ? (currentTime / _maxTime) * 100 : 0;

	api.getDataAtTime = (
		effortIdx: number,
		currentTime: number,
	): ReplayPointSample | null => {
		if (effortIdx >= _efforts.length) return null;
		const effort = must(_efforts[effortIdx], 'effortIdx < length (guarded above)');
		const points = effort.points;
		const state = must(_effortStates[effortIdx], 'effortStates lockstep with efforts');
		const progressOf = (i: number): number => effort.progress[i] ?? 0;

		if (state.finished) {
			const last = must(points.at(-1), 'points.length >= 2 (build invariant)');
			return {
				distance: last.distance,
				time: effort.elapsedTime,
				speed: last.speed,
				altitude: last.altitude || 0,
				progress: 100,
			};
		}

		const lo = state.currentIndex;
		if (lo >= points.length - 1) {
			const lastPt = must(points.at(-1), 'points.length >= 2 (build invariant)');
			return {
				distance: lastPt.distance,
				time: lastPt.time,
				speed: lastPt.speed,
				altitude: lastPt.altitude || 0,
				progress: progressOf(points.length - 1),
			};
		}

		// Interpolate in the active domain. In percent mode `time` becomes the effort's real
		// elapsed time at the current completion %, so the sidebar gap reads as the time each
		// effort took to reach this point of the segment.
		const percent = _mode === "percent";
		const prog = effort.progress;
		const target = percent ? _targetPct(currentTime) : currentTime;
		const p1 = must(points[lo], 'lo < length-1 (guarded above)');
		const p2 = must(points[lo + 1], 'lo+1 <= length-1 (guarded above)');
		// ?? NaN dead (progress lockstep with points): a NaN span yields frac 0, as undefined did.
		const d1 = percent ? prog[lo] ?? NaN : p1.time;
		const span = (percent ? prog[lo + 1] ?? NaN : p2.time) - d1;
		let frac = span > 0 ? (target - d1) / span : 0;
		frac = Math.max(0, Math.min(1, frac));

		return {
			distance: p1.distance + (p2.distance - p1.distance) * frac,
			time: percent ? p1.time + (p2.time - p1.time) * frac : currentTime,
			speed: p1.speed + (p2.speed - p1.speed) * frac,
			altitude: (p1.altitude || 0) + ((p2.altitude || 0) - (p1.altitude || 0)) * frac,
			// The same interpolation as everything above it — `prog` is already in scope for percent mode,
			// and the profile strip needs it in BOTH modes to place this effort's dot.
			progress: progressOf(lo) + (progressOf(lo + 1) - progressOf(lo)) * frac,
		};
	};

	// ────────────────────────────────────────────────────────────────────
	// Lifecycle
	// ────────────────────────────────────────────────────────────────────

	api.destroy = (): void => {
		_efforts = [];
		_effortStates = [];
		_maxTime = 0;
		_finishCounter = 0;
	};


