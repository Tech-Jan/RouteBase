import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { must } from '../core/invariant';
import type { MapShellConfig } from '../core/core-types';
import type { RaceParticipantDto, RaceReplayPayload } from './race-api';
import { installFakeRaf, type FakeRaf } from '../core/raf-test-harness';

// The charts' playhead follows whoever the viewer is WATCHING, not always the leader: follow a rider and
// the orange rule moves onto him while the leader stays on as the golden one, so the gap between the two
// lines is the gap on the road. Unfollow and the orange goes back to the leader, golden off.
//
// Driven end-to-end through the real field builder and the real sidebar rows — only the map, the marker
// layer, the fetch and the two charts are stubbed, so what is pinned is the page's choice of values.

function handler() { return { enable: vi.fn(), disable: vi.fn() }; }

class FakeMap {
	dragRotate = handler(); dragPan = handler(); scrollZoom = handler();
	touchZoomRotate = handler();
	touchPitch = handler();
	isMoving = (): boolean => false; keyboard = handler(); doubleClickZoom = handler();
	private _canvas = document.createElement('canvas');
	getCanvas(): HTMLCanvasElement { return this._canvas; }
	getCenter(): [number, number] { return [8.1, 47.9]; }
	jumpTo = vi.fn();
	addSource = vi.fn();
	addLayer = vi.fn();
	getLayer = vi.fn(() => undefined);
	getSource = vi.fn(() => undefined);
	once = vi.fn();
	fitBounds = vi.fn();
}

let fakeMap: FakeMap;
let raf: FakeRaf;
let shellConfig: MapShellConfig;

interface ChartsStub {
	// Named so noUncheckedIndexedAccess's index-signature undefined doesn't leak into every read.
	setPlayhead: ReturnType<typeof vi.fn>;
	setLeadLine: ReturnType<typeof vi.fn>;
	[k: string]: ReturnType<typeof vi.fn>;
}
let chartsStub: ChartsStub;

const markersStub = {
	setHighlight: vi.fn(), setLabelled: vi.fn(), setEmphasis: vi.fn(), flush: vi.fn(), flushNow: vi.fn(),
};

vi.mock('../core/map-shell', () => ({
	createMapShell: (cfg: MapShellConfig) => {
		shellConfig = cfg;
		return { switchToBasemap: vi.fn(), setActiveBasemap: vi.fn(), destroy: vi.fn() };
	},
}));
vi.mock('../maplibre/maplibre-terrain', () => ({ initMapLibreTerrain: () => fakeMap }));
vi.mock('./race-markers', () => ({ createRaceMarkers: () => markersStub }));
vi.mock('./race-charts', () => ({ createRaceCharts: () => chartsStub }));
vi.mock('./race-import-panel', () => ({ initRaceImportPanel: vi.fn() }));
vi.mock('./race-riders-panel', () => ({ initRaceRidersPanel: vi.fn() }));
vi.mock('../core/map-toolbar', () => ({ setupMapToolbar: vi.fn() }));
vi.mock('./race-api', () => ({ fetchRaceReplay: () => Promise.resolve(payload()) }));

const NODES = 201, STEP = 10, TOTAL = (NODES - 1) * STEP;

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
		raceId: 1, raceName: 'Black Forest',
		course: {
			lat: Array.from({ length: NODES }, () => 47.9),
			lng: Array.from({ length: NODES }, (_, i) => 8.1 + i * 0.0001),
			elevation: Array.from({ length: NODES }, () => 500),
			gradePercent: Array.from({ length: NODES }, (_, i) => (i < NODES / 2 ? 8 : -8)),
			stepMeters: STEP, totalMeters: TOTAL,
		},
		checkpoints: [
			{ checkpointId: 1, seq: 0, name: 'Start', chainageMeters: 0, node: 0, legName: null, legColor: null },
			{ checkpointId: 3, seq: 1, name: 'Finish', chainageMeters: TOTAL, node: NODES - 1, legName: null, legColor: null },
		],
		// Two paces well apart, so mid-race the leader and the tail rider are at different chainages.
		participants: [simulated(1, 600), simulated(2, 1200)],
		costCurve: null, costCurveMinBin: 0,
	};
}

async function bootPage(): Promise<void> {
	await import('./race-replay');
	shellConfig.createMapLibre!('raceMap', 'style', null, 'liberty');
	shellConfig.onMapLibreReady!(fakeMap as unknown as maplibregl.Map, 'style', null);
	await vi.waitFor(() => expect(document.querySelectorAll('.race-sb-row[data-index]').length).toBeGreaterThan(0));
}

/** Rows follow on a pointer TAP (down + up without movement) — the board rebuilds rows every tick. */
function tapRow(row: HTMLElement): void {
	row.dispatchEvent(new MouseEvent('pointerdown', { bubbles: true, clientX: 0, clientY: 0 }));
	row.dispatchEvent(new MouseEvent('pointerup', { bubbles: true, clientX: 0, clientY: 0 }));
}

/** Scrub to a fraction of the race. The view coalesces seeks into one frame (PERF-1), so the flush is
 *  what actually applies the value — see the coalescing test below. */
function scrubTo(percent: number): void {
	scrubInput(percent);
	raf.flushFrames();
}

/** Just the input event — no frame. Lets a test fire a drag's worth of events like a browser would. */
function scrubInput(percent: number): void {
	const seek = document.getElementById('raceReplaySeek') as HTMLInputElement;
	seek.value = String(percent);
	seek.dispatchEvent(new Event('input', { bubbles: true }));
}

function lastArg(fn: ReturnType<typeof vi.fn>): number | null {
	return fn.mock.calls[fn.mock.calls.length - 1]?.[0] as number | null;
}

beforeEach(() => {
	vi.resetModules();
	fakeMap = new FakeMap();
	chartsStub = {
		setCheckpoints: vi.fn(), setField: vi.fn(), setColors: vi.fn(), setEmphasis: vi.fn(), setFilter: vi.fn(),
		setSelected: vi.fn(), setSpeedProfile: vi.fn(), setPlaceable: vi.fn(), setPlayhead: vi.fn(),
		setLeadLine: vi.fn(), tick: vi.fn(), attachMap: vi.fn(), setMapMarkerSuppressed: vi.fn(),
		destroy: vi.fn(),
	};
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
				<canvas id="raceProfile"></canvas>
			</div>
		</div>`;
	(window as unknown as { matchMedia: (q: string) => { matches: boolean } }).matchMedia = () => ({ matches: false });
	raf = installFakeRaf();
});

afterEach(() => {
	raf.restore();
	vi.unstubAllGlobals();
	document.body.innerHTML = '';
});

describe('/Races/Replay — the playhead rides whoever is followed', () => {
	it('moves the playhead onto the followed rider and leaves the leader on the golden rule', async () => {
		await bootPage();
		scrubTo(25);

		// Nobody followed: the orange IS the leader, and there is no second rule to draw.
		const lead = lastArg(chartsStub.setPlayhead)!;
		expect(lead).toBeGreaterThan(0);
		expect(lastArg(chartsStub.setLeadLine)).toBeNull();

		// The last row is the tail of the field — behind the leader by construction.
		const rows = document.querySelectorAll<HTMLElement>('.race-sb-row[data-index]');
		tapRow(must(rows[rows.length - 1], 'last row'));

		const followed = lastArg(chartsStub.setPlayhead)!;
		expect(followed).toBeLessThan(lead);
		// The leader did not move (the clock did not either) — he is now the golden rule.
		expect(lastArg(chartsStub.setLeadLine)).toBe(lead);
	});

	it('unfollowing hands the playhead back to the leader and clears the golden rule', async () => {
		await bootPage();
		scrubTo(25);
		const lead = lastArg(chartsStub.setPlayhead)!;

		const rows = document.querySelectorAll<HTMLElement>('.race-sb-row[data-index]');
		const tail = must(rows[rows.length - 1], 'last row');
		tapRow(tail);
		expect(lastArg(chartsStub.setLeadLine)).toBe(lead);

		// Same row again → unfollow.
		tapRow(document.querySelector<HTMLElement>(`.race-sb-row[data-index="${tail.dataset.index}"]`)!);
		expect(lastArg(chartsStub.setPlayhead)).toBe(lead);
		expect(lastArg(chartsStub.setLeadLine)).toBeNull();
	});

	it('a drag renders ONCE per frame, at the latest value (PERF-1)', async () => {
		await bootPage();
		scrubTo(10);
		const rendersBefore = chartsStub.setPlayhead.mock.calls.length;

		// A real drag fires far more input events than there are frames.
		for (const pct of [20, 30, 40, 50, 60]) scrubInput(pct);
		expect(chartsStub.setPlayhead.mock.calls.length).toBe(rendersBefore); // nothing yet — coalesced

		raf.flushFrames();
		expect(chartsStub.setPlayhead.mock.calls.length).toBe(rendersBefore + 1); // one render, not five

		// …and it landed on the LAST value, not the first of the burst.
		const at60 = lastArg(chartsStub.setPlayhead)!;
		scrubTo(60);
		expect(lastArg(chartsStub.setPlayhead)).toBe(at60);

		// A frame with nothing pending must not re-render.
		raf.flushFrames();
		expect(lastArg(chartsStub.setPlayhead)).toBe(at60);
	});
});
