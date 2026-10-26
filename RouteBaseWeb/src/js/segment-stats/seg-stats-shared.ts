/**
 * Segment Statistics — shared-state slot module (leaf; imports no seg-stats files).
 *
 * Breaks the map ↔ hover ↔ replay import cycles that previously forced these onto
 * window: providers (segment-stats-map.ts, segment-stats-replay.ts, segment-statistics.ts)
 * register here; consumers (segment-stats-map-hover.ts, segment-stats-replay-ui.ts,
 * segment-statistics.ts) call the getters / call-throughs. Runtime semantics are identical
 * to the old window._segStatsMapShared / _segStatsReplay / refreshEffortVisibility /
 * isSegmentReplayActive globals — slots are null/false until a provider registers, and
 * consumers read them lazily inside event handlers (so the "map.ts registers after
 * map-hover loads" ordering holds exactly as before).
 */
import type { SegmentStatsReplayApi } from './segment-stats-replay-types';
import type { SegmentStatsMapShared } from './segment-stats-map-types';

/**
 * Elapsed seconds → "HH:MM:SS" (SC2-10). Hours are computed as Math.floor(sec/3600), so an
 * effort past 24 h reads correctly instead of wrapping modulo 24 (the old
 * `new Date(sec*1000).toISOString().substr(11,8)` truncated the day). Replaces three copies.
 */
export function formatElapsedHms(sec: number): string {
	const s = Math.max(0, Math.floor(sec));
	const h = Math.floor(s / 3600);
	const m = Math.floor((s % 3600) / 60);
	const ss = s % 60;
	const pad = (n: number): string => String(n).padStart(2, "0");
	return pad(h) + ":" + pad(m) + ":" + pad(ss);
}

// Shared map state (segment coords, effort GPS arrays, active-engine accessor).
let _mapShared: SegmentStatsMapShared | null = null;
export function registerMapShared(s: SegmentStatsMapShared | null): void { _mapShared = s; }
export function getMapShared(): SegmentStatsMapShared | null { return _mapShared; }

// Replay controller API (map-hover queries isPlaying(); map teardown destroys it).
let _replayApi: SegmentStatsReplayApi | null = null;
export function registerReplayApi(api: SegmentStatsReplayApi | null): void { _replayApi = api; }
export function getReplayApi(): SegmentStatsReplayApi | null { return _replayApi; }

// Effort-visibility repaint hook (statistics.ts fires it after toggling visible efforts).
let _refreshEffortVisibility: (() => void) | null = null;
export function registerRefreshEffortVisibility(fn: (() => void) | null): void { _refreshEffortVisibility = fn; }
export function refreshEffortVisibility(): void { _refreshEffortVisibility?.(); }

// Replay-active flag (suppresses hover hit-testing while the replay overlay is up).
let _segmentReplayActive = false;
export function setSegmentReplayActive(active: boolean): void { _segmentReplayActive = active; }
export function getSegmentReplayActive(): boolean { return _segmentReplayActive; }
