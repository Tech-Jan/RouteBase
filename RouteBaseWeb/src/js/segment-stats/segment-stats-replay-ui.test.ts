import type { EffortPerformanceData } from './segment-stats-effort-types';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// CR1-06 pin (RS2-05): ctx.startReplay re-entrancy. The heavy map module and the full-res
// loader (PS-03) are mocked; the real replay engine runs against a FakeMap so the guard's
// synchronous flag — set inside the first run's replayApi.start() — is exercised for real.
vi.mock('./segment-stats-map-maplibre', () => ({
	maplibreApi: { hideStaticLayers: vi.fn(), showStaticLayers: vi.fn() },
}));
vi.mock('./segment-stats-replay-data', () => ({
	buildReplayPerfData: vi.fn((pd: EffortPerformanceData[]) => pd),
	loadFullResEfforts: vi.fn(() => Promise.resolve()),
}));

const COORDS: number[][] = [[1, 10], [2, 11], [3, 12], [2, 13], [1, 14]]; // [lat, lng]

interface FakeSource { setData: ReturnType<typeof vi.fn>; }

function handlerGroup(): { enable: ReturnType<typeof vi.fn>; disable: ReturnType<typeof vi.fn> } {
	return { enable: vi.fn(), disable: vi.fn() };
}

// Same maplibregl.Map-shaped stub as segment-stats-replay-maplibre.test.ts.
class FakeMap {
	calls: Array<[string, string]> = [];
	fitBoundsArgs: unknown[] = [];
	dragPan = handlerGroup();
	dragRotate = handlerGroup();
	scrollZoom = handlerGroup();
	touchZoomRotate = handlerGroup();
	touchPitch = handlerGroup();
	isMoving = (): boolean => false;
	keyboard = handlerGroup();
	doubleClickZoom = handlerGroup();
	private _canvas = document.createElement('canvas');
	private _container = document.createElement('div');
	private _sources = new Map<string, FakeSource>();
	private _layers = new Set<string>();

	addSource(id: string, _spec: unknown): void { this.calls.push(['addSource', id]); this._sources.set(id, { setData: vi.fn() }); }
	addLayer(spec: { id: string }, _beforeId?: string): void { this.calls.push(['addLayer', spec.id]); this._layers.add(spec.id); }
	removeLayer(id: string): void { this.calls.push(['removeLayer', id]); this._layers.delete(id); }
	removeSource(id: string): void { this.calls.push(['removeSource', id]); this._sources.delete(id); }
	getSource(id: string): FakeSource | undefined { return this._sources.get(id); }
	getLayer(id: string): { id: string } | undefined { return this._layers.has(id) ? { id } : undefined; }
	fitBounds(...args: unknown[]): void { this.fitBoundsArgs.push(args); }
	jumpTo(): void {}
	getCanvas(): HTMLCanvasElement { return this._canvas; }
	getContainer(): HTMLDivElement { return this._container; }
	on(): void {}
	off(): void {}
}

let fakeMap: FakeMap;

function perfDatum(idx: number): EffortPerformanceData {
	return {
		effortId: idx, activityId: 100 + idx, activityName: 'Effort ' + idx, activityDate: '2026-01-0' + (idx + 1),
		elapsedTime: 100, points: [
			{ distance: 0, time: 0, speed: 5, altitude: 0, lat: 1, lng: 10 },
			{ distance: 250, time: 50, speed: 5, altitude: 0, lat: 2, lng: 12 },
			{ distance: 500, time: 100, speed: 5, altitude: 0, lat: 1, lng: 14 },
		],
	};
}

function seedDom(): void {
	const cfg = {
		hasSegment: true, hasGpsData: true, hasEfforts: true, mapTilerApiKey: '',
		segmentId: 5, kind: 'polyline',
		segmentCoords: COORDS,
		effortRows: [
			{ effortIndex: 0, colorIndex: 0, gpsInSegmentId: 1, activityId: 100, activityName: 'A0', activityType: 'Run', isOwn: true, userDisplayName: null, dateIso: '2026-01-01', matchPct: 100, elapsedSec: 100, distanceM: 500, avgSpeedMs: 5, avgHr: 150, avgPower: 200, elevationM: 10, entryId: 0, exitId: 0 },
			{ effortIndex: 1, colorIndex: 1, gpsInSegmentId: 2, activityId: 101, activityName: 'A1', activityType: 'Run', isOwn: true, userDisplayName: null, dateIso: '2026-01-02', matchPct: 100, elapsedSec: 100, distanceM: 500, avgSpeedMs: 5, avgHr: 150, avgPower: 200, elevationM: 10, entryId: 0, exitId: 0 },
		],
	};
	document.body.innerHTML =
		'<script type="application/json" id="segmentStatisticsConfig">' + JSON.stringify(cfg) + '</script>' +
		'<button id="segReplayBtn"><i class="bi bi-play-circle"></i> <span class="seg-replay-label">Replay</span></button>' +
		'<div id="segmentReplayOverlay"></div>';
}

beforeEach(() => {
	vi.resetModules();
	fakeMap = new FakeMap();
	seedDom();
	// eslint-disable-next-line @typescript-eslint/no-explicit-any
	(globalThis as any).maplibregl = {};
	// eslint-disable-next-line @typescript-eslint/no-explicit-any
	(window as any).matchMedia = () => ({ matches: false });
});

afterEach(() => {
	// eslint-disable-next-line @typescript-eslint/no-explicit-any
	delete (globalThis as any).maplibregl;
	document.body.innerHTML = '';
	vi.restoreAllMocks();
});

describe('segment-stats replay UI — CR1-06 pins', () => {
	it('RS2-05: startReplay is re-entrant-safe — one engine start, one pushState', async () => {
		const { ctx } = await import('./segment-statistics');
		const { replayApi } = await import('./segment-stats-replay');
		await import('./segment-stats-replay-ui'); // overwrites the ctx.startReplay/stopReplay stubs
		const { setMaplibreMap } = await import('../core/map-state');
		setMaplibreMap(fakeMap as unknown as maplibregl.Map);

		ctx.performanceData = [perfDatum(0), perfDatum(1)];
		ctx.performanceDataLoaded = true;
		ctx.getVisibleEffortIndices = () => [0, 1];

		const pushSpy = vi.spyOn(history, 'pushState').mockImplementation(() => {});
		const startSpy = vi.spyOn(replayApi, 'start');

		ctx.startReplay();
		ctx.startReplay(); // keyboard double-activation dispatches a second synchronous call

		expect(startSpy).toHaveBeenCalledTimes(1);
		expect(pushSpy).toHaveBeenCalledTimes(1);

		ctx.stopReplay(); // teardown: stop the rAF + clear the replay-active flag
	});

	// The alert() this replaces was blocking: a modal froze the page's chunked effort load until it was
	// dismissed, so an early click delayed the data it was waiting for.
	it('a click before the data lands defers the start — spinner, no alert, auto-start on arrival', async () => {
		vi.useFakeTimers();
		try {
			const { ctx } = await import('./segment-statistics');
			const { replayApi } = await import('./segment-stats-replay');
			await import('./segment-stats-replay-ui');
			const { setMaplibreMap } = await import('../core/map-state');
			setMaplibreMap(fakeMap as unknown as maplibregl.Map);

			const alertSpy = vi.spyOn(window, 'alert').mockImplementation(() => {});
			vi.spyOn(history, 'pushState').mockImplementation(() => {});
			const startSpy = vi.spyOn(replayApi, 'start');

			ctx.performanceData = [perfDatum(0), perfDatum(1)];
			ctx.performanceDataLoaded = false; // chunked load still running
			ctx.getVisibleEffortIndices = () => [0, 1];

			ctx.startReplay();

			const btn = document.getElementById('segReplayBtn');
			expect(alertSpy).not.toHaveBeenCalled();
			expect(startSpy).not.toHaveBeenCalled();
			expect(btn?.classList.contains('is-waiting')).toBe(true);
			expect(btn?.querySelector('i')?.className).toContain('bi-arrow-repeat');

			ctx.performanceDataLoaded = true;
			await vi.advanceTimersByTimeAsync(250);

			expect(startSpy).toHaveBeenCalledTimes(1);
			expect(btn?.classList.contains('is-waiting')).toBe(false);
			expect(btn?.querySelector('i')?.className).toContain('bi-play-circle');

			ctx.stopReplay();
		} finally {
			vi.useRealTimers();
		}
	});
});
