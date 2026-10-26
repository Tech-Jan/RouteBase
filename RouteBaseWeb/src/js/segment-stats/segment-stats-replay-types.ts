/**
 * Segment-stats replay contracts — the replay effort domain types plus the
 * core/efforts/tracks/markers/perf module APIs of the multi-effort replay engine.
 *
 * Formerly ambient globals (GodFileSplit).
 */

import type { EffortDataPoint, EffortPerformanceData } from './segment-stats-effort-types';

/**
 * Marker halo layer id — single source of truth so the tracks module (RR-02) can insert the
 * follow-track BENEATH the markers without a raw string literal reaching into markers.ts's
 * private layer-id namespace (a rename there would otherwise silently break the z-order).
 */
export const MARKER_HALO_LAYER_ID = "replay-markers-halo";

// ---------------------------------------------------------------------------
// Replay effort — internally constructed from performance data by segment-stats-replay.ts
// ---------------------------------------------------------------------------

export interface ReplayEffort {
	sourceIndex: number;
	name: string;
	date: string;
	color: string;
	points: EffortDataPoint[];
	elapsedTime: number;
	totalDistance: number;
	/** Per-point completion percentage (0–100), monotonic, lockstep with `points`; used by
	 *  percent-sync replay. Derived from EffortDataPoint.segmentProgress with a distance-fraction
	 *  fallback. Always built (build() is the only constructor) — required on purpose. */
	progress: number[];
}

/** One effort, sampled at a moment of the replay clock. */
export interface ReplayPointSample {
	distance: number;
	time: number;
	speed: number;
	altitude: number;
	/** Completion along the segment, 0–100 — the same axis `ReplayEffort.progress` is on. */
	progress: number;
}

/** Replay sync domain: "time" (faster efforts pull ahead) or "percent" (all synced by
 *  segment completion, so gaps read as time-to-reach-this-%). */
export type ReplayMode = "time" | "percent";

export interface ReplayEffortState {
	currentIndex: number;
	finished: boolean;
	finishOrder: number;
	finishTime: number;
	lastLat: number;
	lastLng: number;
}

// ---------------------------------------------------------------------------
// Segment Stats Replay Efforts — effort state machine (segment-stats-replay-efforts.ts)
// ---------------------------------------------------------------------------

export interface SegmentStatsReplayEffortsApi {
	/** Build ReplayEffort list from raw performance data. Stores internally. */
	build(perfData: EffortPerformanceData[], visibleIndices: number[], effortColors: string[]): void;
	/** Return the built effort list. */
	getEfforts(): ReplayEffort[];
	/** Return the current effort states. */
	getEffortStates(): ReplayEffortState[];
	/** Return the maximum elapsed time across all efforts (seconds). */
	getMaxTime(): number;
	/** Set the sync domain: "time" (default) or "percent" (segment completion). */
	setMode(mode: ReplayMode): void;
	/** Current sync domain. */
	getMode(): ReplayMode;
	/** Reset all effort states to the beginning (called on start). */
	reset(): void;
	/**
	 * Reset effort states based on a seek target time:
	 * efforts that finish before currentTime are marked finished,
	 * others are reset to index 0. Updates markers via M.
	 */
	seekReset(currentTime: number, M: SegmentStatsReplayMarkersApi): void;
	/**
	 * Advance all effort states to currentTime. Calls M to update marker
	 * positions and fires onEffortFinish for newly completed efforts.
	 * Returns allFinished and activeCount for the animation loop.
	 */
	advance(
		currentTime: number,
		M: SegmentStatsReplayMarkersApi,
		onEffortFinish: ((idx: number, order: number, time: number) => void) | null,
	): { allFinished: boolean; activeCount: number };
	/** Force all markers to current positions (used after seek). */
	updateAllMarkers(currentTime: number, M: SegmentStatsReplayMarkersApi): void;
	/** Return progress percentage [0, 100] for the given currentTime. */
	getProgressPct(currentTime: number): number;
	/** Return interpolated effort data at currentTime, or null if index out of range.
	 *  `progress` is where the effort is along the segment (0–100) at that moment — the x-axis the
	 *  replay profile strip plots against. */
	getDataAtTime(effortIdx: number, currentTime: number): ReplayPointSample | null;
	/** Clear all stored state. */
	destroy(): void;
}

// ---------------------------------------------------------------------------
// Segment Stats Replay — core multi-effort replay engine (segment-stats-replay.ts)
// ---------------------------------------------------------------------------

export interface SegmentStatsReplayApi {
	/** `suppressTracks` skips the segment/effort track polylines so only the moving markers
	 *  animate over the heatmap raster (used when the heatmap is the active map view). */
	init(mapInstance: maplibregl.Map, perfData: EffortPerformanceData[], visibleIndices: number[], suppressTracks?: boolean): boolean;
	/** PS-03: merge freshly-fetched full-res effort points into the running replay and rebuild the
	 *  marker/track layers, so replayed efforts sharpen to byte-identical resolution progressively. */
	addLoadedEfforts(perfData: EffortPerformanceData[]): void;
	start(): void;
	pause(): void;
	resume(): void;
	togglePlayPause(): boolean;
	seek(percentage: number): void;
	setSpeed(mult: number): void;
	/** Switch the replay sync domain and reposition markers at the current playhead. */
	setMode(mode: ReplayMode): void;
	/** Current sync domain ("time" or "percent"). */
	getMode(): ReplayMode;
	setFollowEffort(effortIdx: number): void;
	isPlaying(): boolean;
	getSpeed(): number;
	getEfforts(): ReplayEffort[];
	getEffortStates(): ReplayEffortState[];
	getProgressPct(): number;
	getMaxTime(): number;
	getCurrentTime(): number;
	onProgress(cb: (pct: number, states: ReplayEffortState[]) => void): void;
	onComplete(cb: () => void): void;
	onEffortFinish(cb: (idx: number, order: number, time: number) => void): void;
	onFollowChange(cb: (idx: number) => void): void;
	getFollowIndex(): number;
	getHighlightedEfforts(): number[];
	toggleEffortHighlight(idx: number, on: boolean): void;
	setHoverEffort(idx: number): void;
	setSegmentBackgroundVisible(visible: boolean): void;
	setMarkerHighlight(idx: number, on: boolean): void;
	destroy(): void;
	getEffortDataAtCurrentTime(effortIdx: number): ReplayPointSample | null;
	getEffortDataAtCurrentDistance(effortIdx: number): ReplayPointSample | null;
}

// ---------------------------------------------------------------------------
// Segment Stats Replay Tracks — track/layer module (segment-stats-replay-tracks.ts)
// ---------------------------------------------------------------------------

export interface SegmentStatsReplayTracksApi {
	bind(mapInstance: maplibregl.Map, effortData: ReplayEffort[]): void;
	draw(): void;
	/** Point at a new effort array without touching the map layers (PS-03 full-res upgrade). */
	rebind(effortData: ReplayEffort[]): void;
	/** Refresh the effort-track polyline geometry in place (setData) — no layer teardown/flash. */
	updateEffortTracks(): void;
	/** Draw the followed effort's full track (heatmap-view replay); replaces any previous one. */
	showEffortTrack(idx: number): void;
	/** Remove the followed-effort track. */
	hideEffortTrack(): void;
	setSegmentBackgroundVisible(visible: boolean): void;
	destroyAll(): void;
	getDiagnosticSnapshot(): TrackDiagnosticSnapshot;
}

// ---------------------------------------------------------------------------
// Segment Stats Replay Markers — marker module (segment-stats-replay-markers.ts)
// ---------------------------------------------------------------------------

export interface SegmentStatsReplayMarkersApi {
	bind(mapInstance: maplibregl.Map, effortData: ReplayEffort[]): void;
	create(): void;
	/** Point at a new effort array without touching the map layers (PS-03 full-res upgrade). */
	rebind(effortData: ReplayEffort[]): void;
	setMarkerPosition(idx: number, lat: number, lng: number): void;
	setMarkerFinished(idx: number, finished: boolean): void;
	setMarkerHighlight(idx: number, on: boolean): void;
	flushMarkerGeoSource(): void;
	resetMarkerFeatures(highlightedEfforts: Record<string, boolean>): void;
	destroyAll(): void;
	getDiagnosticSnapshot(): MarkerDiagnosticSnapshot;
}

// ---------------------------------------------------------------------------
// Replay Perf — performance diagnostics (segment-stats-replay-perf.ts)
// ---------------------------------------------------------------------------

/** Diagnostic snapshot returned by the core replay engine's snapshot callback. */
export interface ReplayDiagnosticSnapshot {
	// Track module fields
	mlTrackSources: number;
	mlTrackLayers: number;
	// Marker module fields
	markerFeatureCount: number;
	markerDirty: boolean;
	mlMarkerSources: number;
	mlMarkerLayers: number;
	// Combined fields (set by replay engine)
	mlSources: number;
	mlLayers: number;
	followFrameCount: number;
	effortCount: number;
}

/** Per-phase timing statistics produced by _stats(). */
export interface PerfStatsSummary {
	avg: string;
	min: string;
	max: string;
	p95: string;
}

/** Row in the console.table() phase report. */
export interface PerfPhaseRow {
	"avg (ms)": string;
	"min (ms)": string;
	"max (ms)": string;
	"p95 (ms)": string;
}

/** Diagnostic snapshot returned by the tracks module. */
export interface TrackDiagnosticSnapshot {
	mlTrackSources: number;
	mlTrackLayers: number;
}

/** Diagnostic snapshot returned by the markers module. */
export interface MarkerDiagnosticSnapshot {
	markerFeatureCount: number;
	markerDirty: boolean;
	mlMarkerSources: number;
	mlMarkerLayers: number;
}

export interface ReplayPerfApi {
	setDiagnosticSnapshot(fn: () => ReplayDiagnosticSnapshot): void;
	start(): void;
	stop(): void;
	report(): void;
	isActive(): boolean;
	frameStart(followActive: boolean): number | null;
	phase(phaseName: string, phaseStartTime: number | null): number | null;
	frameEnd(frameStartTime: number | null, activeCount: number): void;
	event(msg: string): void;
}
