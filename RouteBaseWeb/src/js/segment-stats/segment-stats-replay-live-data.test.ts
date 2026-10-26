// @vitest-environment node
import { describe, it, expect } from 'vitest';
import type { ReplayEffort, ReplayPointSample } from './segment-stats-replay-types';
import type { EffortDataPoint } from './segment-stats-effort-types';
import type { SpeedScale } from '../core/speed-scale';
import { formatTime } from '../core/format';
import { createSpeedLabel, type SpeedLabel } from '../core/speed-label';
import {
	buildEffortDistanceSeries, buildEffortLiveModel, buildSpeedScaleFromEfforts, computeEffortLiveRow,
} from './segment-stats-replay-live-data';

function pt(time: number, speed: number, distance = 0): EffortDataPoint {
	return { distance, time, speed, altitude: null, lat: null, lng: null };
}

function effort(points: EffortDataPoint[]): ReplayEffort {
	return { sourceIndex: 0, name: 'e', date: '', color: '#000', points, elapsedTime: 0, totalDistance: 0, progress: [] };
}

function sample(time: number, speed: number): ReplayPointSample {
	return { distance: 0, time, speed, altitude: 0, progress: 0 };
}

/** Deterministic scale stub so bar-width assertions don't depend on the CDF math. */
function stubScale(fillValue: number, isEmpty = false): SpeedScale {
	return { isEmpty, fill: () => fillValue };
}

/**
 * A real label latch — a fresh one shows its first value at once, so a one-call row reads the
 * sample itself. Real, not a mock: the deadband IS the contract.
 */
function lb(): SpeedLabel {
	return createSpeedLabel();
}

describe('buildSpeedScaleFromEfforts', () => {
	it('builds a non-empty scale from a ≥2-point effort (dt-weighted samples)', () => {
		const scale = buildSpeedScaleFromEfforts([effort([pt(0, 10), pt(10, 20)])]);
		expect(scale.isEmpty).toBe(false);
		// Single sample at speed 10 → the top of the distribution clamps to 100%.
		expect(scale.fill(10)).toBe(100);
		expect(scale.fill(0)).toBeGreaterThanOrEqual(0);
	});

	it('skips efforts with fewer than 2 points (no sample, empty scale)', () => {
		expect(buildSpeedScaleFromEfforts([effort([pt(0, 10)])]).isEmpty).toBe(true);
		expect(buildSpeedScaleFromEfforts([effort([])]).isEmpty).toBe(true);
	});

	it('drops samples whose dt is non-positive (weight 0 → ignored)', () => {
		// Two points at the SAME time → dt = 0 → weight 0 → buildSpeedScale ignores it.
		expect(buildSpeedScaleFromEfforts([effort([pt(0, 10), pt(0, 20)])]).isEmpty).toBe(true);
	});

	it('aggregates samples across multiple efforts', () => {
		const scale = buildSpeedScaleFromEfforts([
			effort([pt(0, 5), pt(10, 5)]),
			effort([pt(0, 30), pt(10, 30)]),
		]);
		expect(scale.isEmpty).toBe(false);
		// Fast speed sits at/above the top of the distribution.
		expect(scale.fill(30)).toBe(100);
	});
});

describe('computeEffortLiveRow', () => {
	it('leader (index 0) shows no gap', () => {
		const row = computeEffortLiveRow({
			data: sample(50, 12), finished: false, isLeader: true, leaderTime: 0, speedScale: stubScale(30), label: lb(), series: null, playbackRate: 1,
		});
		expect(row.gapText).toBe('');
		expect(row.speed).toBe(12);
		expect(row.speedText).toBe('12 km/h');
		expect(row.barWidthPct).toBe(30);
	});

	it('non-leader positive gap = elapsedTime − leaderTime, "+"-prefixed', () => {
		const row = computeEffortLiveRow({
			data: sample(60, 8), finished: false, isLeader: false, leaderTime: 40, speedScale: stubScale(10), label: lb(), series: null, playbackRate: 1,
		});
		expect(row.gapText).toBe('+' + formatTime(20));
	});

	it('non-leader zero gap is still shown (>= boundary)', () => {
		const row = computeEffortLiveRow({
			data: sample(40, 8), finished: false, isLeader: false, leaderTime: 40, speedScale: stubScale(10), label: lb(), series: null, playbackRate: 1,
		});
		expect(row.gapText).toBe('+' + formatTime(0));
	});

	it('non-leader negative gap is blank (behind-the-clock efforts read empty)', () => {
		const row = computeEffortLiveRow({
			data: sample(30, 8), finished: false, isLeader: false, leaderTime: 40, speedScale: stubScale(10), label: lb(), series: null, playbackRate: 1,
		});
		expect(row.gapText).toBe('');
	});

	it('finished effort clamps live speed to 0 but keeps its time for the gap', () => {
		const row = computeEffortLiveRow({
			data: sample(50, 25), finished: true, isLeader: false, leaderTime: 40, speedScale: stubScale(30), label: lb(), series: null, playbackRate: 1,
		});
		expect(row.speed).toBe(0);
		expect(row.speedText).toBe('-- km/h');
		// elapsedTime still comes from data.time (50), so the gap vs leader (40) is +10.
		expect(row.gapText).toBe('+' + formatTime(10));
	});

	it('null data → speed 0, elapsedTime 0', () => {
		const row = computeEffortLiveRow({
			data: null, finished: false, isLeader: false, leaderTime: 40, speedScale: stubScale(30), label: lb(), series: null, playbackRate: 1,
		});
		expect(row.speed).toBe(0);
		expect(row.speedText).toBe('-- km/h');
		// elapsedTime 0 − leader 40 = −40 → blank.
		expect(row.gapText).toBe('');
	});

	it('zero live speed (not finished) still reads "-- km/h"', () => {
		const row = computeEffortLiveRow({
			data: sample(10, 0), finished: false, isLeader: true, leaderTime: 0, speedScale: stubScale(5), label: lb(), series: null, playbackRate: 1,
		});
		expect(row.speed).toBe(0);
		expect(row.speedText).toBe('-- km/h');
	});

	it('speed label is whole km/h — a decimal that nobody can read mid-flicker is not a readout', () => {
		const row = computeEffortLiveRow({
			data: sample(10, 23.456), finished: false, isLeader: true, leaderTime: 0, speedScale: stubScale(5), label: lb(), series: null, playbackRate: 1,
		});
		expect(row.speedText).toBe('23 km/h');
	});

	it('the label holds inside its deadband while the bar keeps tracking — two halves, two rules', () => {
		// Intent carried over from the metronome era: the bar is fed the unrounded measurement so it
		// sweeps, while the label only moves once the speed has left the number it is showing.
		const label = lb();
		const scale: SpeedScale = { isEmpty: false, fill: (s) => s };
		const args = { finished: false, isLeader: true, leaderTime: 0, speedScale: scale, label, series: null, playbackRate: 1 };

		const first = computeEffortLiveRow({ ...args, data: sample(10, 20) });
		const second = computeEffortLiveRow({ ...args, data: sample(11, 20.6) });

		expect(first.speedText).toBe('20 km/h');
		expect(second.speedText).toBe('20 km/h');   // inside the deadband
		expect(second.speed).toBe(20.6);            // the bar keeps moving
		expect(second.barWidthPct).toBe(20.6);
	});

	it('a re-entering rider reads his own speed, not a blend with the one he stopped at', () => {
		// The old design had to reset a sample window here. There is no window now — the measurement
		// is a function of the playhead — so the only thing that could carry over is the label, and a
		// real change clears its deadband by itself.
		const label = lb();
		const args = { isLeader: true, leaderTime: 0, speedScale: stubScale(50), label, series: null, playbackRate: 1 };

		computeEffortLiveRow({ ...args, data: sample(10, 40), finished: false });
		computeEffortLiveRow({ ...args, data: sample(11, 40), finished: true });
		const back = computeEffortLiveRow({ ...args, data: sample(12, 10), finished: false });

		expect(back.speed).toBe(10);
		expect(back.speedText).toBe('10 km/h');
	});

	it('bar width floors at 2% when moving but below the floor', () => {
		const row = computeEffortLiveRow({
			data: sample(10, 5), finished: false, isLeader: true, leaderTime: 0, speedScale: stubScale(0.5), label: lb(), series: null, playbackRate: 1,
		});
		expect(row.barWidthPct).toBe(2);
	});

	it('bar width passes through when at/above the floor', () => {
		const row = computeEffortLiveRow({
			data: sample(10, 5), finished: false, isLeader: true, leaderTime: 0, speedScale: stubScale(50), label: lb(), series: null, playbackRate: 1,
		});
		expect(row.barWidthPct).toBe(50);
	});

	it('no floor is applied when stopped (speed 0)', () => {
		// speed 0 → the stopped short-circuit skips the 2% floor; raw fill(0) passes through.
		const row = computeEffortLiveRow({
			data: sample(10, 0), finished: false, isLeader: true, leaderTime: 0, speedScale: stubScale(0.5), label: lb(), series: null, playbackRate: 1,
		});
		expect(row.barWidthPct).toBe(0.5);
	});

	it('empty scale → bar width 0 even when moving', () => {
		const row = computeEffortLiveRow({
			data: sample(10, 5), finished: false, isLeader: true, leaderTime: 0, speedScale: stubScale(99, true), label: lb(), series: null, playbackRate: 1,
		});
		expect(row.barWidthPct).toBe(0);
	});

	it('null scale → bar width 0', () => {
		const row = computeEffortLiveRow({
			data: sample(10, 5), finished: false, isLeader: true, leaderTime: 0, speedScale: null, label: lb(), series: null, playbackRate: 1,
		});
		expect(row.barWidthPct).toBe(0);
	});

	it('measures from the distance channel, not from the sampled speed', () => {
		// A constant 10 m/s effort whose speed channel says 5 km/h. The distance is the truth.
		const pts: EffortDataPoint[] = [];
		for (let i = 0; i <= 20; i++) pts.push(pt(i, 5, i * 10));
		const row = computeEffortLiveRow({
			data: sample(10, 5), finished: false, isLeader: true, leaderTime: 0,
			speedScale: stubScale(30), label: lb(), series: buildEffortDistanceSeries(effort(pts)), playbackRate: 1,
		});
		expect(row.speed).toBeCloseTo(36, 6);
		expect(row.speedText).toBe('36 km/h');
	});

	it('falls back to the stored speed channel when the effort has no usable distance', () => {
		const row = computeEffortLiveRow({
			data: sample(10, 23), finished: false, isLeader: true, leaderTime: 0,
			speedScale: stubScale(30), label: lb(), series: buildEffortDistanceSeries(effort([pt(0, 23)])), playbackRate: 1,
		});
		expect(row.speed).toBe(23);
	});

	it('measures the same number at the same playhead however it was reached', () => {
		// The property the old sample window could not hold. Asserted on `speed` (the measurement),
		// not `speedText` — the label is deliberately the one half that remembers anything.
		const pts: EffortDataPoint[] = [];
		for (let i = 0; i <= 60; i++) pts.push(pt(i, 1, i * i)); // accelerating, so the window matters
		const args = {
			finished: false, isLeader: true, leaderTime: 0, speedScale: stubScale(30),
			series: buildEffortDistanceSeries(effort(pts)), playbackRate: 1,
		};

		const direct = computeEffortLiveRow({ ...args, data: sample(30, 1), label: lb() });

		const label = lb();
		for (let t = 0; t <= 60; t += 0.25) computeEffortLiveRow({ ...args, data: sample(t, 1), label });
		const scrubbedBack = computeEffortLiveRow({ ...args, data: sample(30, 1), label });

		expect(scrubbedBack.speed).toBe(direct.speed);
	});

	it('widens the window with the playback rate, so a fast replay reads a broader average', () => {
		// 30 s at 10 m/s then 30 s at 20 m/s, sampled just after the step at t = 32.
		//   1x  → 5 s window  [29.5, 34.5] → 95 m / 5 s  = 19 m/s = 68.4 km/h
		//   10x → 20 s window [22, 42]     → 320 m / 20 s = 16 m/s = 57.6 km/h
		const pts: EffortDataPoint[] = [];
		for (let i = 0; i <= 60; i++) pts.push(pt(i, 1, i <= 30 ? i * 10 : 300 + (i - 30) * 20));
		const args = {
			data: sample(32, 1), finished: false, isLeader: true, leaderTime: 0,
			speedScale: stubScale(30), series: buildEffortDistanceSeries(effort(pts)),
		};
		expect(computeEffortLiveRow({ ...args, label: lb(), playbackRate: 1 }).speed).toBeCloseTo(68.4, 6);
		expect(computeEffortLiveRow({ ...args, label: lb(), playbackRate: 10 }).speed).toBeCloseTo(57.6, 6);
	});
});

describe('buildEffortDistanceSeries', () => {
	it('converts effort metres to the kilometres core/speed-window works in', () => {
		const s = buildEffortDistanceSeries(effort([pt(0, 0, 0), pt(10, 0, 2500)]));
		expect(s).not.toBeNull();
		expect(s?.secondsAt(1)).toBe(10);
		expect(s?.kmAt(1)).toBeCloseTo(2.5, 10);
	});

	it('is null for an effort too short to measure across', () => {
		expect(buildEffortDistanceSeries(effort([pt(0, 0, 0)]))).toBeNull();
		expect(buildEffortDistanceSeries(undefined)).toBeNull();
	});
});

describe('buildEffortLiveModel', () => {
	it('builds one label and one series per effort, lockstep with the list', () => {
		const model = buildEffortLiveModel([
			effort([pt(0, 10, 0), pt(10, 10, 100)]),
			effort([pt(0, 5)]), // too short to measure
		]);
		expect(model.labels).toHaveLength(2);
		expect(model.series).toHaveLength(2);
		expect(model.series[0]).not.toBeNull();
		expect(model.series[1]).toBeNull();
		// Latches are per row: one row's held number must not follow another's.
		expect(model.labels[0]).not.toBe(model.labels[1]);
		expect(model.speedScale.isEmpty).toBe(false);
	});
});
