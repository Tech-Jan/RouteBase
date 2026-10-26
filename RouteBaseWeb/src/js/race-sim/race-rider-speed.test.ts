// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { buildRace } from './race-build';
import { computeRiderSpeedSeries } from './race-rider-speed';
import type { RaceParticipantDto, RaceReplayPayload } from './race-api';

const NODES = 201;          // 2000 m at 10 m
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

function simulated(id: number, finishSeconds: number): RaceParticipantDto {
	return {
		participantId: id, kind: 'simulated', name: `Rider ${id}`, bib: `${id}`,
		category: null, gender: null, club: null, color: null, emphasised: false, calloutOff: false,
		officialFinishSeconds: null, finishStatus: null, finishStatusRaw: null,
		startOffsetSeconds: null, wave: null, discipline: null, seed: id,
		splits: [{ checkpointId: 3, elapsedSeconds: finishSeconds }], track: null,
	};
}

function real(
	id: number,
	elapsedSeconds: number[],
	chainageMeters: number[],
	color: string | null = null,
): RaceParticipantDto {
	return {
		participantId: id, kind: 'real', name: 'Me', bib: null, category: null, gender: null,
		club: null, color, emphasised: false, calloutOff: false, officialFinishSeconds: null, finishStatus: null,
		finishStatusRaw: null, startOffsetSeconds: null, wave: null, discipline: null, seed: 0,
		splits: [],
		track: {
			lat: elapsedSeconds.map(() => 47.95),
			lng: elapsedSeconds.map((_, i) => 8.1 + i * 0.001),
			elapsedSeconds,
			chainageMeters,
		},
	};
}

/** 2000 m in 1000 s at an even 200 m per 100 s = 2 m/s = 7.2 km/h. */
const EVEN_TIMES = Array.from({ length: 11 }, (_, i) => i * 100);
const EVEN_CHAINAGE = Array.from({ length: 11 }, (_, i) => i * 200);

/** A rider sampled every 10 s, holding 10 m/s apart from one 400 m step — a spike to smooth away. */
function spikeRace() {
	const times = Array.from({ length: 11 }, (_, i) => i * 10);
	const chainage = [0, 100, 200, 300, 400, 800, 900, 1000, 1100, 1200, 1300];
	return buildRace(payload([real(7, times, chainage)]));
}

describe('computeRiderSpeedSeries — a measured rider', () => {
	it('reads a constant-pace rider at his constant speed, across a full 0–100 % axis', () => {
		const race = buildRace(payload([real(7, EVEN_TIMES, EVEN_CHAINAGE)]));

		const [series] = computeRiderSpeedSeries(race, TOTAL, [0]);

		expect(series).toBeDefined();
		expect(series?.participantId).toBe(7);
		expect(series?.isMeasured).toBe(true);

		const points = series?.views.views.recorded.full ?? [];
		expect(points.length).toBe(EVEN_TIMES.length);
		expect(points[0]?.x).toBeCloseTo(0, 6);
		expect(points[points.length - 1]?.x).toBeCloseTo(100, 6);
		for (const p of points) expect(p.y).toBeCloseTo(7.2, 6);
	});

	it('carries the rider identity the chart paints with', () => {
		const race = buildRace(payload([real(7, EVEN_TIMES, EVEN_CHAINAGE, '#ff0000')]));

		const [series] = computeRiderSpeedSeries(race, TOTAL, [0]);

		expect(series?.name).toBe('Me');
		expect(series?.color).toBe('#ff0000');
		expect(series?.fieldIndex).toBe(0);
	});

	it('reads a backwards chainage step as a standstill, never as a negative speed', () => {
		// The last sample projects 100 m BACK — a detour artefact, not a reversal.
		const race = buildRace(payload([real(7, [0, 100, 200], [0, 500, 400])]));

		const [series] = computeRiderSpeedSeries(race, TOTAL, [0]);
		const points = series?.views.views.recorded.full ?? [];

		expect(points.length).toBe(3);
		for (const p of points) expect(p.y).toBeGreaterThanOrEqual(0);
		expect(points[2]?.y).toBe(0);
	});

	it('never lets completion go backwards, even when the projection does', () => {
		const race = buildRace(payload([real(7, [0, 100, 200], [0, 500, 400])]));

		const xs = (computeRiderSpeedSeries(race, TOTAL, [0])[0]?.views.views.recorded.full ?? []).map((p) => p.x);

		expect(xs).toEqual([...xs].sort((a, b) => a - b));
		expect(xs[1]).toBeCloseTo(25, 6);
		expect(xs[2]).toBeCloseTo(25, 6);   // held at the furthest point reached, not dropped to 20
	});

	it('clamps completion at 100 % when the projection overshoots the course', () => {
		const race = buildRace(payload([real(7, [0, 100, 200], [0, 1000, TOTAL + 400])]));

		const xs = (computeRiderSpeedSeries(race, TOTAL, [0])[0]?.views.views.recorded.full ?? []).map((p) => p.x);

		expect(Math.max(...xs)).toBeCloseTo(100, 6);
	});
});

describe('computeRiderSpeedSeries — a modelled rider', () => {
	it('produces a line and marks it as NOT measured', () => {
		const race = buildRace(payload([simulated(1, 1000)]), { noise: 0 });

		const [series] = computeRiderSpeedSeries(race, TOTAL, [0], { timeSteps: 200 });

		expect(series?.isMeasured).toBe(false);
		expect(series?.participantId).toBe(1);
		const points = series?.views.views.recorded.full ?? [];
		expect(points.length).toBeGreaterThan(50);
		for (const p of points) {
			expect(p.x).toBeGreaterThanOrEqual(0);
			expect(p.x).toBeLessThanOrEqual(100);
			expect(p.y).toBeGreaterThanOrEqual(0);
		}
	});

	it('ends the line at his finish rather than running flat to the last finisher', () => {
		// One rider finishes in 500 s, the other in 2000 s. The fast rider's series must not carry a
		// tail of zeroes parked on the line for the remaining 1500 s.
		const race = buildRace(payload([simulated(1, 500), simulated(2, 2000)]), { noise: 0 });

		const [fast, slow] = computeRiderSpeedSeries(race, TOTAL, [0, 1], { timeSteps: 400 });

		expect(fast?.views.views.recorded.full.length).toBeLessThan(slow?.views.views.recorded.full.length ?? 0);
		const tail = fast?.views.views.recorded.full.slice(-3) ?? [];
		expect(tail.every((p) => p.y === 0)).toBe(false);
	});

	it('gives the same answer when called twice — the field cursor is rewound, not consumed', () => {
		const race = buildRace(payload([simulated(1, 1000)]), { noise: 0 });

		const first = computeRiderSpeedSeries(race, TOTAL, [0], { timeSteps: 200 });
		const second = computeRiderSpeedSeries(race, TOTAL, [0], { timeSteps: 200 });

		expect(second[0]?.views.views.recorded.full).toEqual(first[0]?.views.views.recorded.full);
	});
});

describe('computeRiderSpeedSeries — the requested set', () => {
	it('de-duplicates and orders the indices, so dataset order never depends on the caller', () => {
		const race = buildRace(payload([
			real(7, EVEN_TIMES, EVEN_CHAINAGE),
			real(8, EVEN_TIMES, EVEN_CHAINAGE),
		]));

		const series = computeRiderSpeedSeries(race, TOTAL, [1, 0, 1, 0]);

		expect(series.map((s) => s.fieldIndex)).toEqual([0, 1]);
	});

	it('drops indices that are not in the field', () => {
		const race = buildRace(payload([real(7, EVEN_TIMES, EVEN_CHAINAGE)]));

		expect(computeRiderSpeedSeries(race, TOTAL, [-1, 99]).length).toBe(0);
		expect(computeRiderSpeedSeries(race, TOTAL, []).length).toBe(0);
	});

	it('is empty when the course has no length to be a percentage of', () => {
		const race = buildRace(payload([real(7, EVEN_TIMES, EVEN_CHAINAGE)]));

		expect(computeRiderSpeedSeries(race, 0, [0])).toEqual([]);
	});

	it('leaves the recorded channel alone and smooths only the smoothed one', () => {
		// An even 10 m/s with one 30 m/s step in the middle. The window has to be wide relative to the
		// SAMPLE SPACING to do anything — a 60 s Gaussian over points 100 s apart is an identity.
		const views = computeRiderSpeedSeries(spikeRace(), TOTAL, [0], { windowSeconds: 60 })[0]?.views;

		const recorded = views?.views.recorded.full.map((p) => p.y) ?? [];
		const smoothed = views?.views.smoothBin.full.map((p) => p.y) ?? [];
		expect(smoothed).not.toEqual(recorded);
		expect(Math.max(...smoothed)).toBeLessThan(Math.max(...recorded));
	});

	it('honours smoothing being switched off — the processed view then tracks the recorded one', () => {
		// Not identical any more: the processed view always bins, so it follows the recorded line
		// rather than reproducing it point for point. What windowSeconds: 0 must do is remove the
		// Gaussian, which is measurable as staying far closer to the raw peak.
		const off = computeRiderSpeedSeries(spikeRace(), TOTAL, [0], { windowSeconds: 0 })[0]?.views;
		const on = computeRiderSpeedSeries(spikeRace(), TOTAL, [0], { windowSeconds: 60 })[0]?.views;

		const peak = (v: typeof off): number => Math.max(...(v?.views.smoothBin.full ?? []).map((p) => p.y));
		const rawPeak = Math.max(...(off?.views.recorded.full ?? []).map((p) => p.y));

		expect(rawPeak - peak(off)).toBeLessThan(rawPeak - peak(on));
	});
});
