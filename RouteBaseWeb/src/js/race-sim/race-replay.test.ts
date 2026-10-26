import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import type { MapShellConfig } from '../core/core-types';
import type { RaceParticipantDto, RaceReplayPayload } from './race-api';
import { installFakeRaf, type FakeRaf } from '../core/raf-test-harness';

// LV-04 pin for /Races/Replay's half of the follow-camera seam: toggling follow on a leaderboard row
// applies the follow handler policy and activates CameraControls; toggling it off hands every handler
// back. The policy is selective now — following takes only the centre-moving handlers and leaves the
// gesture handlers on, centre-anchored — so this no longer pins "all six off" (that contract is gone,
// see core/replay-follow-camera.ts).
//
// The page is driven end-to-end with the real playback shell, the real field builder and the real
// CameraControls — only the map, the marker layer, the fetch and the two side panels are stubbed.

/** Taken away while following — these move the centre, which the follow owns. */
const OFF_WHILE_FOLLOWING = ['dragPan', 'doubleClickZoom', 'keyboard'] as const;
/** Left on while following, so pinch / twist / 2-finger tilt keep working over the locked-on rider. */
const KEPT_WHILE_FOLLOWING = ['scrollZoom', 'touchZoomRotate', 'touchPitch', 'dragRotate'] as const;
const HANDLER_NAMES = [...OFF_WHILE_FOLLOWING, ...KEPT_WHILE_FOLLOWING] as const;

function handler(): { enable: ReturnType<typeof vi.fn>; disable: ReturnType<typeof vi.fn> } {
	return { enable: vi.fn(), disable: vi.fn() };
}

class FakeMap {
	dragRotate = handler();
	dragPan = handler();
	scrollZoom = handler();
	touchZoomRotate = handler();
	touchPitch = handler();
	keyboard = handler();
	doubleClickZoom = handler();
	isMoving = (): boolean => false;
	private _canvas = document.createElement('canvas');
	getCanvas(): HTMLCanvasElement { return this._canvas; }
	getCenter(): [number, number] { return [8.1, 47.9]; }
	jumpTo = vi.fn();
	addSource = vi.fn();
	addLayer = vi.fn();
	getLayer = vi.fn(() => undefined);
	// drawCourseLine() is idempotent now (it is re-run on a style switch / bfcache re-attach), so it asks
	// whether its source already exists. A fresh fake map has nothing on it.
	getSource = vi.fn(() => undefined);
	once = vi.fn();
	fitBounds = vi.fn();
}

let fakeMap: FakeMap;
let shellConfig: MapShellConfig;

const markersStub = {
	setHighlight: vi.fn(),
	setEmphasis: vi.fn(),
	setLabelled: vi.fn(),
	flush: vi.fn(),
	flushNow: vi.fn(),
};

vi.mock('../core/map-shell', () => ({
	createMapShell: (cfg: MapShellConfig) => {
		shellConfig = cfg;
		return { switchToBasemap: vi.fn(), setActiveBasemap: vi.fn(), destroy: vi.fn() };
	},
}));
vi.mock('../maplibre/maplibre-terrain', () => ({
	initMapLibreTerrain: () => fakeMap,
}));
vi.mock('./race-markers', () => ({
	createRaceMarkers: () => markersStub,
}));
vi.mock('./race-import-panel', () => ({ initRaceImportPanel: vi.fn() }));
vi.mock('./race-riders-panel', () => ({ initRaceRidersPanel: vi.fn() }));
vi.mock('../core/map-toolbar', () => ({ setupMapToolbar: vi.fn() }));
vi.mock('./race-api', () => ({
	fetchRaceReplay: () => Promise.resolve(payload()),
}));

const NODES = 201;
const STEP = 10;
const TOTAL = (NODES - 1) * STEP;

function simulated(id: number, finishSeconds: number): RaceParticipantDto {
	return {
		participantId: id, kind: 'simulated', name: `Rider ${id}`, bib: `${id}`,
		category: null, gender: null, club: null, color: null, emphasised: false, calloutOff: false, officialFinishSeconds: null, finishStatus: null, finishStatusRaw: null, startOffsetSeconds: null, wave: null, discipline: null, seed: id,
		splits: [{ checkpointId: 3, elapsedSeconds: finishSeconds }],
		track: null,
	};
}

function payload(): RaceReplayPayload {
	return {
		raceId: 1,
		raceName: 'Black Forest',
		course: {
			lat: Array.from({ length: NODES }, () => 47.9),
			lng: Array.from({ length: NODES }, (_, i) => 8.1 + i * 0.0001),
			elevation: Array.from({ length: NODES }, () => 500),
			gradePercent: Array.from({ length: NODES }, (_, i) => (i < NODES / 2 ? 8 : -8)),
			stepMeters: STEP,
			totalMeters: TOTAL,
		},
		checkpoints: [
			{ checkpointId: 1, seq: 0, name: 'Start', chainageMeters: 0, node: 0, legName: null, legColor: null },
			{ checkpointId: 3, seq: 1, name: 'Finish', chainageMeters: TOTAL, node: NODES - 1, legName: null, legColor: null },
		],
		participants: [simulated(1, 600), simulated(2, 700)],
		costCurve: null,
		costCurveMinBin: 0,
	};
}

/** Boot the page: import (which self-inits), hand it a map, then let start() resolve. */
async function bootPage(): Promise<void> {
	await import('./race-replay');
	shellConfig.createMapLibre!('raceMap', 'style', null, 'liberty');
	shellConfig.onMapLibreReady!(fakeMap as unknown as maplibregl.Map, 'style', null);
	await vi.waitFor(() => expect(document.querySelectorAll('.race-sb-row[data-index]').length).toBeGreaterThan(0));
}

/** A row follows on a pointer TAP now, not a click — the board rebuilds rows every tick, which dropped
 *  clicks. Down + up at the same point (no movement) is the tap the overlay listens for. */
function clickRow(index: number): void {
	const row = document.querySelector<HTMLElement>(`.race-sb-row[data-index="${index}"]`)!;
	row.dispatchEvent(new MouseEvent('pointerdown', { bubbles: true, clientX: 0, clientY: 0 }));
	row.dispatchEvent(new MouseEvent('pointerup', { bubbles: true, clientX: 0, clientY: 0 }));
}

let raf: FakeRaf;

beforeEach(() => {
	vi.resetModules();
	fakeMap = new FakeMap();
	// The follow sidebar (rows carry data-index) replaced the old <tr> leaderboard; the transport moved into
	// the in-map bar (raceReplay* ids). No chart canvases: without them mountCharts no-ops, which keeps this
	// follow-camera pin focused on the sidebar + the map, exactly as the old <tr> fixture did.
	document.body.innerHTML = `
		<div id="raceReplay" data-race-id="1">
			<div id="raceHero">
				<div id="raceMap"></div><div id="raceStyleSwitcher"></div>
				<div id="raceLoading"></div>
				<button id="raceMapFullscreen"></button>
				<div id="raceReplaySidebar">
					<div id="raceReplaySidebarHeader"><button id="raceReplayFollowToggle"></button></div>
					<div id="raceReplaySidebarBody"></div>
				</div>
				<button id="raceReplayPlayPause"></button>
				<span id="raceReplayCurrentTime"></span>
				<input id="raceReplaySeek" type="range" value="0">
				<select id="raceReplaySpeed"></select>
				<button id="raceReplayExit"></button>
			</div>
		</div>`;
	// jsdom has no matchMedia; the overlay probes it for the mobile collapse default.
	(window as unknown as { matchMedia: (q: string) => { matches: boolean } }).matchMedia = () => ({ matches: false });
	raf = installFakeRaf();
});

afterEach(() => {
	raf.restore();
	vi.unstubAllGlobals();
	document.body.innerHTML = '';
});

describe('/Races/Replay follow camera — LV-04 pin', () => {
	it('following a rider takes the centre handlers and keeps the gestures; unfollowing hands all back', async () => {
		await bootPage();
		for (const n of HANDLER_NAMES) { fakeMap[n].enable.mockClear(); fakeMap[n].disable.mockClear(); }

		clickRow(0);
		for (const n of OFF_WHILE_FOLLOWING) {
			expect(fakeMap[n].disable, n).toHaveBeenCalledTimes(1);
			expect(fakeMap[n].enable, n).not.toHaveBeenCalled();
		}
		for (const n of KEPT_WHILE_FOLLOWING) {
			expect(fakeMap[n].disable, n).not.toHaveBeenCalled();
			expect(fakeMap[n].enable, n).toHaveBeenCalledTimes(1);
		}

		clickRow(0); // same row again → unfollow
		for (const n of HANDLER_NAMES) expect(fakeMap[n].enable, n).toHaveBeenCalledWith();
	});
});
