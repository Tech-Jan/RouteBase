import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import type { GpsPoint } from '../core/core-types';

// The replay engine is heavy (real MapLibre); mock it so the UI wiring can be pinned in jsdom.
vi.mock('./activity-replay', () => ({ ActivityReplay: vi.fn() }));

import { setupReplayControls } from './activity-replay-ui';
import { ActivityReplay } from './activity-replay';
import { setGpsData } from '../core/gps-data';
import { setMaplibreMap } from '../core/map-state';
import { registerActionPanel } from './details-shared';

const FIXTURE = `
  <div id="mapCard">
    <div class="det-hero-tr"></div>
    <button id="mapFullscreenBtn"></button>
  </div>
  <button id="playReplayBtn"></button>
  <div id="replayControls"></div>
  <button id="replayExit"></button>
  <button id="replayPlayPause"></button>
  <input id="replaySeek" />
  <select id="replaySpeed"></select>
  <button id="replaySpeedSlower"></button>
  <button id="replaySpeedFaster"></button>
  <span id="replaySpeedLabel"></span>
  <div id="replayInfo"><div class="replay-info-header"></div></div>
  <span id="replayInfoDistance"></span>
  <span id="replayInfoSpeed"></span>
  <span id="replayInfoSpeedBar"></span>
  <span id="replayInfoAltitude"></span>
  <span id="replayInfoHR"></span>
  <span id="replayCurrentTime"></span>
  <span id="replayInfoCompletion"></span>
`;

function makeGps(): GpsPoint[] {
	return Array.from({ length: 12 }, (_, i) => ({
		lat: 45 + i * 0.001, lng: 9 + i * 0.001, altitude: 100 + i, speed: 5 + i, distance: i * 0.1,
	})) as unknown as GpsPoint[];
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
let engine: any;
const ReplayMock = ActivityReplay as unknown as ReturnType<typeof vi.fn>;

beforeEach(() => {
	vi.useFakeTimers();
	// eslint-disable-next-line @typescript-eslint/no-explicit-any
	(window as any).matchMedia = () => ({ matches: false });
	document.body.innerHTML = FIXTURE;
	setGpsData(makeGps());
	// eslint-disable-next-line @typescript-eslint/no-explicit-any
	setMaplibreMap({ resize: vi.fn() } as any);
	engine = {
		onProgress: null, onComplete: null, onModeChange: null, isPlaying: false,
		setMode: vi.fn(), getMode: () => 'time', getTotalTime: () => 120, getCurrentTime: () => 60,
		start: vi.fn(), pause: vi.fn(), resume: vi.fn(), seek: vi.fn(), setSpeed: vi.fn(), destroy: vi.fn(),
	};
	ReplayMock.mockReset();
	// Regular function (not arrow) so `new ActivityReplay(...)` returns our stub instance.
	ReplayMock.mockImplementation(function () { return engine; });
});

afterEach(() => {
	vi.useRealTimers();
	document.body.innerHTML = '';
	setGpsData([]);
	// eslint-disable-next-line @typescript-eslint/no-explicit-any
	setMaplibreMap(null as any);
	vi.restoreAllMocks();
});

describe('activity-replay-ui — RP-01 / RP-05 / RP-06', () => {
	it('second Replay click while starting is ignored — one engine, one pushState (RP-01)', async () => {
		const pushSpy = vi.spyOn(history, 'pushState');
		setupReplayControls({ totalTimeSeconds: 0 });

		const btn = document.getElementById('playReplayBtn')!;
		btn.click();
		btn.click(); // re-entrant click during the 150ms fullscreen wait
		await vi.runAllTimersAsync();

		expect(ReplayMock).toHaveBeenCalledTimes(1);
		expect(pushSpy).toHaveBeenCalledTimes(1);
	});

	// The transport no longer carries a duration label — the elapsed clock is the whole readout, and it
	// comes from the engine rather than the config total (which is 0 here).
	it('elapsed label follows the engine clock, not the config total (RP-05)', async () => {
		setupReplayControls({ totalTimeSeconds: 0 });
		document.getElementById('playReplayBtn')!.click();
		await vi.runAllTimersAsync();

		// (pct, index, point, fracIndex) — the strip takes the last one, the clock takes the index.
		engine.onProgress(50, 2, { distance: 1, speed: 5, altitude: 100, heartRate: 120 }, 2.4);

		expect(document.getElementById('replayCurrentTime')!.textContent).toBe('1:00');
	});

	it('starting replay closes registered action panels (RP-06)', async () => {
		const closer = vi.fn();
		registerActionPanel('photos', closer);
		setupReplayControls({ totalTimeSeconds: 0 });

		document.getElementById('playReplayBtn')!.click();
		await vi.runAllTimersAsync();

		expect(closer).toHaveBeenCalled();
	});
});
