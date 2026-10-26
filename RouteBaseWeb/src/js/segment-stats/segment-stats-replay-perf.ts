/**
 * Segment Statistics — Replay Performance Logger
 *
 * Dev-only diagnostic tool: ring-buffer stats, frame interval tracking,
 * jank detection, periodic console.table() reports.
 *
 * The core replay engine imports replayPerfApi directly.
 *
 * Depends on: nothing (self-contained)
 */
import type { ReplayDiagnosticSnapshot, PerfStatsSummary, PerfPhaseRow, ReplayPerfApi } from './segment-stats-replay-types';
import { getAccountSettings } from '../core/account-settings';
import { must } from '../core/invariant';

const REPORT_INTERVAL_MS = 5000;
	let _active = false;
	let _lastReport = 0;

	const BUF = 300;

	const _intervals = new Float32Array(BUF);
	let _intIdx = 0;
	let _intCount = 0;
	let _lastAnimateTime = 0;

	const PHASES = ["markers", "camera", "flush", "progress", "total"];
	const _phaseBufs: Record<string, Float32Array> = {};
	for (const phase of PHASES) {
		_phaseBufs[phase] = new Float32Array(BUF);
	}
	let _phaseIdx = 0;
	let _phaseCount = 0;

	const _activeCountBuf = new Uint8Array(BUF);
	let _acIdx = 0;
	let _acCount = 0;

	let _jankFrameCount = 0;
	let _followFrameCount = 0;

	let _events: string[] = [];

	// — Diagnostic snapshot callback ——————————————————
	// Set by the core engine so the perf logger can read layer counts
	// without knowing the module internals.
	let _getDiagnosticSnapshot: (() => ReplayDiagnosticSnapshot) | null = null;

	const _emptySnap: ReplayDiagnosticSnapshot = {
		mlTrackSources: 0, mlTrackLayers: 0,
		markerFeatureCount: 0, markerDirty: false,
		mlMarkerSources: 0, mlMarkerLayers: 0,
		mlSources: 0, mlLayers: 0,
		followFrameCount: 0, effortCount: 0,
	};

	function _stats(
		buf: Float32Array | Uint8Array,
		count: number,
	): PerfStatsSummary {
		if (count === 0) return { avg: "0", min: "0", max: "0", p95: "0" };
		const len = Math.min(count, BUF);
		const arr = Array.from(buf.subarray(0, len));
		arr.sort((a: number, b: number) => a - b);
		// ?? NaN: arr is non-empty (count guard above) and every index is clamped in-bounds —
		// dead fallbacks; NaN would print "NaN", not a plausible number.
		let sum = 0;
		for (let i = 0; i < arr.length; i++) sum += arr[i] ?? NaN;
		return {
			avg: (sum / arr.length).toFixed(2),
			min: (arr[0] ?? NaN).toFixed(2),
			max: (arr[arr.length - 1] ?? NaN).toFixed(2),
			p95: (arr[Math.floor(arr.length * 0.95)] ?? NaN).toFixed(2),
		};
	}

	function _report(): void {
		const totalFrames = Math.min(_phaseCount, BUF);
		if (totalFrames === 0) return;

		const snap: ReplayDiagnosticSnapshot = _getDiagnosticSnapshot ? _getDiagnosticSnapshot() : _emptySnap;

		const intStats = _stats(_intervals, _intCount);
		const fps =
			parseFloat(intStats.avg) > 0 ? (1000 / parseFloat(intStats.avg)).toFixed(1) : "?";
		const jankCount = _jankFrameCount;

		const rows: Record<string, PerfPhaseRow> = {};
		for (const ph of PHASES) {
			const s = _stats(must(_phaseBufs[ph], 'seeded for every PHASES key'), _phaseCount);
			rows[ph] = {
				"avg (ms)": s.avg,
				"min (ms)": s.min,
				"max (ms)": s.max,
				"p95 (ms)": s.p95,
			};
		}

		const acStats = _stats(_activeCountBuf, _acCount);

		const p95Interval = parseFloat(intStats.p95);
		// MapLibre 3D terrain fills the remaining frame budget, so a higher p95 is expected.
		let fpsContext = "";
		if (p95Interval > 50) {
			fpsContext =
				" ⚠ p95=" +
				intStats.p95 +
				"ms — GPU may be under heavy load (expected ~30ms for 3D terrain + " +
				(snap.effortCount || 0) +
				" effort tracks)";
		} else if (p95Interval > 33) {
			fpsContext = " (normal for 3D terrain — MapLibre WebGL render fills remaining budget)";
		} else if (parseFloat(intStats.avg) < 10) {
			fpsContext = " (high-refresh display or uncapped rAF)";
		}

		const followLine =
			_followFrameCount > 0
				? jankCount + "/" + _followFrameCount + " follow-frames janked (>50ms)"
				: "no follow active";

		console.groupCollapsed(
			"%c[ReplayPerf] 3D | " +
				totalFrames +
				" frames | " +
				fps +
				" fps" +
				fpsContext +
				" | " +
				jankCount +
				" jank | active markers avg=" +
				acStats.avg,
			"color: #4fc3f7; font-weight: bold;",
		);
		console.table(rows);
		console.log(
			"Frame interval (ms): avg=" +
				intStats.avg +
				" min=" +
				intStats.min +
				" max=" +
				intStats.max +
				" p95=" +
				intStats.p95,
		);
		console.log("[ReplayPerf] follow: " + followLine);
		if (_events.length > 0) {
			console.log("Events since last report:", _events.slice());
			_events = [];
		}
		console.groupEnd();

		_jankFrameCount = 0;
		_followFrameCount = 0;
	}

	// ====================================================================
	// PUBLIC API
	// ====================================================================

	const pub = {} as ReplayPerfApi;
export const replayPerfApi = pub;

	pub.setDiagnosticSnapshot = (fn: () => ReplayDiagnosticSnapshot): void => {
		_getDiagnosticSnapshot = fn;
	};

	pub.start = (): void => {
		// Gated behind the user's "Show debug information" setting (UI category).
		// When disabled, start() no-ops so frameStart/phase/frameEnd/event all stay
		// inert via their existing `!_active` guards — zero per-frame/console cost.
		if (!getAccountSettings().ui.showDebugInfo) return;
		_active = true;
		_lastReport = performance.now();
		_lastAnimateTime = 0;
		_intIdx = _intCount = 0;
		_phaseIdx = _phaseCount = 0;
		_acIdx = _acCount = 0;
		_jankFrameCount = 0;
		_followFrameCount = 0;
		_events = [];
		console.log("%c[ReplayPerf] logging started — report every 5 s", "color:#4fc3f7");
	};

	pub.stop = (): void => {
		if (!_active) return;
		_active = false;
		_report();
		console.log("%c[ReplayPerf] logging stopped", "color:#4fc3f7");
	};

	pub.report = (): void => {
		_report();
	};
	pub.isActive = (): boolean => _active;

	pub.frameStart = (followActive: boolean): number | null => {
		if (!_active) return null;
		const now = performance.now();
		if (_lastAnimateTime > 0) {
			const interval = now - _lastAnimateTime;
			_intervals[_intIdx % BUF] = interval;
			_intIdx++;
			_intCount++;

			if (followActive) {
				_followFrameCount++;
				if (interval > 50) _jankFrameCount++;
			}
		}
		_lastAnimateTime = now;
		return now;
	};

	pub.phase = (phaseName: string, phaseStartTime: number | null): number | null => {
		if (!_active || phaseStartTime === null) return null;
		const now = performance.now();
		if (_phaseBufs[phaseName]) {
			_phaseBufs[phaseName][_phaseIdx % BUF] = now - phaseStartTime;
		}
		return now;
	};

	pub.frameEnd = (frameStartTime: number | null, activeCount: number): void => {
		if (!_active || frameStartTime === null) return;
		const now = performance.now();
		must(_phaseBufs["total"], "seeded for every PHASES key")[_phaseIdx % BUF] = now - frameStartTime;
		_phaseIdx++;
		_phaseCount++;

		_activeCountBuf[_acIdx % BUF] = activeCount;
		_acIdx++;
		_acCount++;

		if (now - _lastReport >= REPORT_INTERVAL_MS) {
			_lastReport = now;

			// DIAGNOSTIC: GeoJSON source/layer count sanity check
			const snap: ReplayDiagnosticSnapshot = _getDiagnosticSnapshot ? _getDiagnosticSnapshot() : _emptySnap;
			console.log(
				"[ReplayPerf] MapLibre | GeoJSON sources: " +
					(snap.mlSources || 0) +
					"/3" +
					" | layers: " +
					(snap.mlLayers || 0) +
					"/5" +
					" | marker features: " +
					(snap.markerFeatureCount || 0) +
					" | dirty: " +
					(snap.markerDirty || false),
			);

			_report();
		}
	};

	pub.event = (msg: string): void => {
		if (!_active) return;
		_events.push(performance.now().toFixed(1) + "ms: " + msg);
		console.log("%c[ReplayPerf] EVENT: " + msg, "color:#ffb74d; font-weight:bold;");
	};

	console.log("segment-stats-replay-perf.ts loaded");
