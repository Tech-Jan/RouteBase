/**
 * The panel's contract is WHO gets drawn, WHEN the field cursor moves, and that the COURSE survives an
 * empty rider set — not how a line looks. `race-speed-chart` is therefore mocked out (the same boundary
 * `segment-replay-profile.test.ts` draws against `core/profile-chart`), so these tests fail for a wrong
 * rider set and nothing else.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { buildRace } from './race-build';
import type { RaceCheckpointDto, RaceParticipantDto, RaceReplayPayload } from './race-api';
import type { RiderSpeedSeries } from './race-rider-speed';

const H = vi.hoisted(() => ({
	created: [] as Array<{
		series: readonly RiderSpeedSeries[]; course: unknown; checkpoints: unknown; onHover: unknown;
	}>,
	setSeriesCalls: [] as Array<readonly RiderSpeedSeries[]>,
	checkpointCalls: [] as Array<readonly RaceCheckpointDto[]>,
	selectedCalls: [] as Array<number | null>,
	playheadCalls: [] as Array<[number | null, number | null]>,
	destroyed: 0,
	/** The pointer layer, mocked at the same boundary: what matters here is that it is armed at all. */
	placeableCalls: [] as boolean[],
	pointerBuilt: 0,
	pointerDestroyed: 0,
}));

vi.mock('./race-speed-chart', () => ({
	createRaceSpeedChart: (opts: {
		series: readonly RiderSpeedSeries[]; course: unknown; checkpoints: unknown; onHover: unknown;
	}) => {
		H.created.push({
			series: opts.series, course: opts.course, checkpoints: opts.checkpoints, onHover: opts.onHover,
		});
		return {
			setSeries: (s: readonly RiderSpeedSeries[]) => H.setSeriesCalls.push(s),
			setMode: () => {},
			setCheckpoints: (c: readonly RaceCheckpointDto[]) => H.checkpointCalls.push(c),
			setSelected: (id: number | null) => H.selectedCalls.push(id),
			setFieldProfile: () => {},
			setPlayhead: (m: number | null, lead: number | null) => H.playheadCalls.push([m, lead]),
			instance: () => null,
			destroy: () => { H.destroyed++; },
		};
	},
}));

vi.mock('./race-speed-checkpoints', () => ({
	createRaceSpeedCheckpoints: () => {
		H.pointerBuilt++;
		return {
			setPlaceable: (armed: boolean) => H.placeableCalls.push(armed),
			destroy: () => { H.pointerDestroyed++; },
		};
	},
}));

import { createRaceSpeedPanel } from './race-speed-panel';

const NODES = 201;
const STEP = 10;
const TOTAL = (NODES - 1) * STEP;

function payload(participants: RaceParticipantDto[]): RaceReplayPayload {
	return {
		raceId: 1,
		raceName: 'Black Forest',
		course: {
			lat: Array.from({ length: NODES }, () => 47.9),
			lng: Array.from({ length: NODES }, (_, i) => 8.1 + i * 0.0001),
			elevation: Array.from({ length: NODES }, () => 500),
			gradePercent: Array.from({ length: NODES }, () => 0),
			stepMeters: STEP,
			totalMeters: TOTAL,
		},
		checkpoints: [
			{ checkpointId: 1, seq: 0, name: 'Start', chainageMeters: 0, node: 0, legName: null, legColor: null },
			{ checkpointId: 3, seq: 1, name: 'Finish', chainageMeters: TOTAL, node: NODES - 1, legName: null, legColor: null },
		],
		participants,
		costCurve: null,
		costCurveMinBin: 0,
	};
}

function simulated(id: number, emphasised = false, calloutOff = false): RaceParticipantDto {
	return {
		participantId: id, kind: 'simulated', name: `Rider ${id}`, bib: `${id}`,
		category: null, gender: null, club: null, color: null, emphasised, calloutOff,
		officialFinishSeconds: null, finishStatus: null, finishStatusRaw: null,
		startOffsetSeconds: null, wave: null, discipline: null, seed: id,
		splits: [{ checkpointId: 3, elapsedSeconds: 1000 }], track: null,
	};
}

function real(id: number, name: string, calloutOff = false): RaceParticipantDto {
	return {
		participantId: id, kind: 'real', name, bib: null, category: null, gender: null, club: null,
		color: null, emphasised: false, calloutOff, officialFinishSeconds: null, finishStatus: null,
		finishStatusRaw: null, startOffsetSeconds: null, wave: null, discipline: null, seed: 0,
		splits: [],
		track: {
			lat: [47.95, 47.95, 47.95],
			lng: [8.1, 8.11, 8.12],
			elapsedSeconds: [0, 500, 1000],
			chainageMeters: [0, TOTAL / 2, TOTAL],
		},
	};
}

beforeEach(() => {
	H.created = [];
	H.setSeriesCalls = [];
	H.checkpointCalls = [];
	H.selectedCalls = [];
	H.playheadCalls = [];
	H.placeableCalls = [];
	H.pointerBuilt = 0;
	H.pointerDestroyed = 0;
	H.destroyed = 0;
	// Mirrors _RaceSpeedPanel.cshtml, note included — the note is what lets the empty state stop hiding
	// the canvas, which is the whole reason the course can be drawn on an unpopulated race.
	document.body.innerHTML =
		'<small id="raceSpeedNote" hidden></small>' +
		'<div id="raceSpeedPanel"><canvas id="raceSpeedChart"></canvas></div>';
});

afterEach(() => {
	document.body.replaceChildren();
});

describe('createRaceSpeedPanel', () => {
	it('is null when the page does not render the canvas', () => {
		document.body.replaceChildren();

		expect(createRaceSpeedPanel()).toBeNull();
	});

	it('draws every real rider without being asked to', () => {
		const race = buildRace(payload([simulated(1), real(2, 'Me'), real(3, 'Ana')]));
		const panel = createRaceSpeedPanel();

		panel?.update(race, TOTAL);

		expect(H.created.length).toBe(1);
		expect(H.created[0]?.series.map((s) => s.participantId)).toEqual([2, 3]);
	});

	it('draws an emphasised simulated rider alongside the real ones', () => {
		const race = buildRace(payload([simulated(1, true), simulated(4), real(2, 'Me')]), { noise: 0 });
		const panel = createRaceSpeedPanel();

		panel?.update(race, TOTAL);

		const drawn = H.created[0]?.series.map((s) => s.participantId) ?? [];
		expect(drawn).toContain(1);   // emphasised
		expect(drawn).toContain(2);   // real
		expect(drawn).not.toContain(4);
	});

	/**
	 * The reason the opt-out exists: two real riders are two lines here, and a rider you are not comparing
	 * is a line in the way. Hiding him must not mean deleting him from the race.
	 */
	it('leaves out a real rider whose call-out the owner hid', () => {
		const race = buildRace(payload([real(2, 'Me'), real(3, 'Ana', true)]));
		const panel = createRaceSpeedPanel();

		panel?.update(race, TOTAL);

		expect(H.created[0]?.series.map((s) => s.participantId)).toEqual([2]);
	});

	it('draws no line for the only real rider when the owner hid his call-out', () => {
		const race = buildRace(payload([simulated(1), real(2, 'Me', true)]), { noise: 0 });
		const panel = createRaceSpeedPanel();

		panel?.update(race, TOTAL);

		// The chart is still built — it is the course's canvas now — but nobody is on it.
		expect(H.created[0]?.series).toEqual([]);
	});

	// ⚠ CONTRACT CHANGED 2026-08-12, deliberately. This used to assert the opposite — "builds no chart at
	// all until there is something to draw", plus an empty-state overlay that HID the canvas. Both are
	// wrong now that the panel carries the course: the terrain, the section rules and (for an owner)
	// click-to-place all have to be on screen for a race with no highlighted riders, which is exactly the
	// state a race is in while its sections are being laid out. The old tests are obsolete, not violated.
	it('builds the chart even when no rider is drawn, so the course is still on screen', () => {
		const race = buildRace(payload([simulated(1), simulated(2)]), { noise: 0 });
		const panel = createRaceSpeedPanel();

		panel?.update(race, TOTAL);

		expect(H.created.length).toBe(1);
		expect(H.created[0]?.series).toEqual([]);
	});

	it('says what to do about an empty panel WITHOUT hiding the canvas', () => {
		const p = payload([simulated(1)]);
		const panel = createRaceSpeedPanel({ course: p.course, checkpoints: p.checkpoints });

		panel?.update(buildRace(p, { noise: 0 }), TOTAL);

		const note = document.getElementById('raceSpeedNote');
		expect(note?.textContent).toMatch(/highlight riders/i);
		expect(note?.hidden).toBe(false);
		// The course lives on this canvas; a curtain over it would take the section boundaries with it.
		expect(document.getElementById('raceSpeedChart')?.style.visibility).toBe('');
	});

	it('clears the note once somebody is drawn', () => {
		const p = payload([simulated(1)]);
		const panel = createRaceSpeedPanel({ course: p.course });
		panel?.update(buildRace(p, { noise: 0 }), TOTAL);

		panel?.update(buildRace(payload([real(2, 'Me')])), TOTAL);

		expect(document.getElementById('raceSpeedNote')?.hidden).toBe(true);
	});

	// The other half of the same rule: with no course the canvas really IS blank when nobody is drawn,
	// and a blank chart with no explanation is what core/chart-empty-state exists to prevent.
	it('curtains the canvas instead when there is no course to leave on it', () => {
		const panel = createRaceSpeedPanel();

		panel?.update(buildRace(payload([simulated(1)]), { noise: 0 }), TOTAL);

		expect(document.getElementById('raceSpeedPanel')?.textContent).toMatch(/highlight riders/i);
		expect(document.getElementById('raceSpeedChart')?.style.visibility).toBe('hidden');
	});

	it('hands the chart the course and its checkpoints, so it can draw the terrain', () => {
		const p = payload([real(2, 'Me')]);
		const panel = createRaceSpeedPanel({ course: p.course, checkpoints: p.checkpoints });

		panel?.update(buildRace(p), TOTAL);

		expect(H.created[0]?.course).toBe(p.course);
		expect(H.created[0]?.checkpoints).toEqual(p.checkpoints);
	});

	it('wires the placement layer only when it has a course to place on', () => {
		const p = payload([real(2, 'Me')]);
		createRaceSpeedPanel()?.update(buildRace(p), TOTAL);
		expect(H.pointerBuilt).toBe(0);

		createRaceSpeedPanel({ course: p.course, editable: true })?.update(buildRace(p), TOTAL);
		expect(H.pointerBuilt).toBe(1);
	});

	it('carries an arming that happened before the chart existed', () => {
		const p = payload([real(2, 'Me')]);
		const panel = createRaceSpeedPanel({ course: p.course, editable: true });

		// The Sections editor can arm placement before the first update — the click that follows must
		// still place, so the state cannot be dropped on the floor while there is no pointer layer yet.
		panel?.setPlaceable(true);
		panel?.update(buildRace(p), TOTAL);

		expect(H.placeableCalls).toEqual([true]);
	});

	it('forwards a checkpoint edit and a selection to the chart', () => {
		const p = payload([real(2, 'Me')]);
		const panel = createRaceSpeedPanel({ course: p.course, checkpoints: p.checkpoints });
		panel?.update(buildRace(p), TOTAL);

		const moved = p.checkpoints.map((c) => ({ ...c, chainageMeters: c.chainageMeters + 100 }));
		panel?.setCheckpoints(moved);
		panel?.setSelected(3);

		expect(H.checkpointCalls.at(-1)).toEqual(moved);
		expect(H.selectedCalls.at(-1)).toBe(3);
	});

	it('hands the chart its hover callback, so the map dot survives the strip going fullscreen-only', () => {
		const p = payload([real(2, 'Me')]);
		const onHover = vi.fn();
		const panel = createRaceSpeedPanel({ course: p.course, onHover });

		panel?.update(buildRace(p), TOTAL);

		expect(H.created[0]?.onHover).toBe(onHover);
	});

	it('passes the playhead and the lead line straight through, per frame', () => {
		const p = payload([real(2, 'Me')]);
		const panel = createRaceSpeedPanel({ course: p.course });
		panel?.update(buildRace(p), TOTAL);

		panel?.setPlayhead(1200, 1500);

		expect(H.playheadCalls.at(-1)).toEqual([1200, 1500]);
	});

	it('warns the view when drawing a modelled rider moved the field cursor', () => {
		const race = buildRace(payload([simulated(1, true), real(2, 'Me')]), { noise: 0 });
		const onFieldWalked = vi.fn();

		createRaceSpeedPanel({ onFieldWalked })?.update(race, TOTAL);

		expect(onFieldWalked).toHaveBeenCalledTimes(1);
	});

	it('does not warn when every drawn rider brought his own track', () => {
		const race = buildRace(payload([simulated(1), real(2, 'Me')]));
		const onFieldWalked = vi.fn();

		createRaceSpeedPanel({ onFieldWalked })?.update(race, TOTAL);

		expect(onFieldWalked).not.toHaveBeenCalled();
	});

	it('reuses the chart on the next update rather than building a second one', () => {
		const race = buildRace(payload([real(2, 'Me')]));
		const panel = createRaceSpeedPanel();

		panel?.update(race, TOTAL);
		panel?.update(race, TOTAL);

		expect(H.created.length).toBe(1);
		expect(H.setSeriesCalls.length).toBe(1);
	});

	it('picks up an emphasis change on the next update', () => {
		const race = buildRace(payload([simulated(1), real(2, 'Me')]), { noise: 0 });
		const panel = createRaceSpeedPanel();
		panel?.update(race, TOTAL);

		// The same write-back seam race-replay-view uses: the flag lives on the participant object.
		const target = race.participants.find((p) => p.participantId === 1);
		if (target) target.emphasised = true;
		panel?.update(race, TOTAL);

		expect(H.setSeriesCalls[0]?.map((s) => s.participantId)).toEqual([1, 2]);
	});

	it('re-derives the drawn set from the race it is handed, never from a stored index', () => {
		// A rebuild renumbers the field: rider 9 is index 1 in the first race and index 0 in the second.
		const panel = createRaceSpeedPanel();
		panel?.update(buildRace(payload([real(8, 'Ana'), real(9, 'Me')])), TOTAL);
		panel?.update(buildRace(payload([real(9, 'Me')])), TOTAL);

		expect(H.setSeriesCalls[0]?.map((s) => s.participantId)).toEqual([9]);
		expect(H.setSeriesCalls[0]?.map((s) => s.fieldIndex)).toEqual([0]);
	});

	it('falls back to the empty state when the last drawn rider goes away', () => {
		const panel = createRaceSpeedPanel({ course: payload([]).course });
		panel?.update(buildRace(payload([real(2, 'Me')])), TOTAL);
		panel?.update(buildRace(payload([simulated(1)]), { noise: 0 }), TOTAL);

		expect(H.setSeriesCalls[0]).toEqual([]);
		expect(document.getElementById('raceSpeedNote')?.textContent).toMatch(/highlight riders/i);
	});

	it('destroys the chart and the placement layer it built', () => {
		const p = payload([real(2, 'Me')]);
		const panel = createRaceSpeedPanel({ course: p.course, editable: true });
		panel?.update(buildRace(p), TOTAL);

		panel?.destroy();

		expect(H.destroyed).toBe(1);
		// The pointer layer holds an AbortController and a pan-yield registration on the canvas; leaking
		// either would leave a dead closure hit-testing a destroyed chart on the next race.
		expect(H.pointerDestroyed).toBe(1);
	});
});
