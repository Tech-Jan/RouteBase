import type { GpsPoint } from './core-types';
import { describe, it, expect, vi } from 'vitest';
import { must } from './invariant';
import { advanceByDistance, advanceByTime, interpolatePosition, computeCumulativeTime, createMarkerManager } from './replay-engine-base';

// Minimal typed GPS point builder — only the fields the math helpers read. Explicit undefined is
// deliberate (the missing-distance cases override the base value with absence).
function pt(partial: { [K in keyof GpsPoint]?: GpsPoint[K] | undefined }): GpsPoint {
	return { lat: 0, lng: 0, altitude: 0, speed: 0, distance: 0, ...partial } as GpsPoint;
}

describe('advanceByDistance', () => {
	const track = [pt({ distance: 0 }), pt({ distance: 1 }), pt({ distance: 2 }), pt({ distance: 3 })];

	it('interpolates a fractional index within a segment', () => {
		const r = advanceByDistance(track, 0, 0.5);
		expect(r.index).toBeCloseTo(0.5, 10);
		expect(r.finished).toBe(false);
	});

	it('falls back to advance-by-1 when .distance is missing', () => {
		const noDist = [pt({ distance: undefined }), pt({ distance: undefined }), pt({ distance: undefined })];
		const r = advanceByDistance(noDist, 0, 5);
		expect(r.index).toBe(1);
		expect(r.finished).toBe(false);
	});

	it('clamps at the end of the track with finished=true', () => {
		const r = advanceByDistance(track, 3, 1);
		expect(r.index).toBe(3); // last index
		expect(r.finished).toBe(true);
	});

	it('a distance gap inside the search window yields a finite integer index, not NaN (RP-07)', () => {
		const gap = [pt({ distance: 0 }), pt({ distance: 1 }), pt({ distance: undefined }), pt({ distance: 3 })];
		const r = advanceByDistance(gap, 0, 2.5);
		expect(Number.isFinite(r.index)).toBe(true);
		expect(r.index).toBe(3);
	});
});

// RP-03: the idle-fallback layer add must be cancellable — a fast exit before the map goes
// idle would otherwise re-add a stuck replay marker onto the normal (non-replay) map.
class MarkerFakeMap {
	sources = new Map<string, { setData: ReturnType<typeof vi.fn> }>();
	layers = new Set<string>();
	idleCbs: (() => void)[] = [];
	failNextAddSource = false;
	getSource(id: string): { setData: ReturnType<typeof vi.fn> } | undefined { return this.sources.get(id); }
	addSource(id: string): void {
		if (this.failNextAddSource) { this.failNextAddSource = false; throw new Error('style not ready'); }
		this.sources.set(id, { setData: vi.fn() });
	}
	addLayer(spec: { id: string }): void { this.layers.add(spec.id); }
	removeLayer(id: string): void { this.layers.delete(id); }
	removeSource(id: string): void { this.sources.delete(id); }
	getLayer(id: string): { id: string } | undefined { return this.layers.has(id) ? { id } : undefined; }
	once(ev: string, cb: () => void): void { if (ev === 'idle') this.idleCbs.push(cb); }
	off(ev: string, cb: () => void): void { if (ev === 'idle') this.idleCbs = this.idleCbs.filter((c) => c !== cb); }
	fireIdle(): void { const cbs = this.idleCbs.slice(); this.idleCbs = []; cbs.forEach((c) => c()); }
}

describe('createMarkerManager — RP-03 pending idle cancellation', () => {
	const asMap = (m: MarkerFakeMap): maplibregl.Map => m as unknown as maplibregl.Map;

	it('remove() before the idle fallback fires cancels the pending layer add', () => {
		const map = new MarkerFakeMap();
		map.failNextAddSource = true; // synchronous add throws → idle fallback armed
		const mgr = createMarkerManager(asMap(map), { sourceId: 'm', borderLayerId: 'mb', fillLayerId: 'mf' });

		mgr.init(1, 2);
		expect(mgr.isAdded()).toBe(false);
		expect(map.idleCbs.length).toBe(1);

		mgr.remove();
		expect(map.idleCbs.length).toBe(0); // pending idle detached

		map.fireIdle(); // nothing left to fire
		expect(map.sources.has('m')).toBe(false);
		expect(mgr.isAdded()).toBe(false);
	});

	it('remove() when never added clears the retained feature so setPosition is a no-op', () => {
		const map = new MarkerFakeMap();
		map.failNextAddSource = true;
		const mgr = createMarkerManager(asMap(map), { sourceId: 'm', borderLayerId: 'mb', fillLayerId: 'mf' });

		mgr.init(1, 2);
		mgr.remove();
		mgr.setPosition(9, 9);

		expect(map.sources.has('m')).toBe(false); // no re-add, no setData
	});
});

describe('advanceByTime', () => {
	const track = [
		pt({ _cumulativeTime: 0 }),
		pt({ _cumulativeTime: 10 }),
		pt({ _cumulativeTime: 20 }),
		pt({ _cumulativeTime: 30 }),
	];

	it('fast-forwards from startIndex and interpolates within the time segment', () => {
		expect(advanceByTime(track, 5, 0).index).toBeCloseTo(0.5, 10);
		expect(advanceByTime(track, 25, 0).index).toBeCloseTo(2.5, 10);
	});

	it('returns the last index finished=true once startIndex is at/past the end', () => {
		const r = advanceByTime(track, 100, 3);
		expect(r.index).toBe(3);
		expect(r.finished).toBe(true);
	});

	it('a zero-length time segment yields an integer index', () => {
		const flat = [pt({ _cumulativeTime: 0 }), pt({ _cumulativeTime: 5 }), pt({ _cumulativeTime: 5 })];
		const r = advanceByTime(flat, 5, 0);
		expect(r.index).toBe(1); // seg t2-t1 == 0 -> newIdx = lo
		expect(Number.isInteger(r.index)).toBe(true);
	});
});

describe('interpolatePosition', () => {
	const track = [pt({ lat: 0, lng: 0 }), pt({ lat: 10, lng: 20 })];

	it('fraction 0 returns the exact floor point', () => {
		expect(interpolatePosition(track, 1)).toEqual({ lat: 10, lng: 20 });
	});

	it('out-of-range indices return null', () => {
		expect(interpolatePosition(track, -1)).toBeNull();
		expect(interpolatePosition(track, 2)).toBeNull();
	});

	it('midpoint interpolates lat/lng linearly', () => {
		expect(interpolatePosition(track, 0.5)).toEqual({ lat: 5, lng: 10 });
	});
});

describe('computeCumulativeTime', () => {
	it('parses HH:MM:SS with midnight rollover (+86400)', () => {
		const track = [pt({ time: '23:59:50' }), pt({ time: '00:00:10' })];
		const total = computeCumulativeTime(track, 0);
		expect(must(track[0], 'fixture point 0')._cumulativeTime).toBe(0);
		expect(must(track[1], 'fixture point 1')._cumulativeTime).toBe(20); // rolled over past midnight
		expect(total).toBe(20); // adopts track total when provided total is 0
	});

	it('adopts the track total when within 10% of the provided total, keeps it otherwise', () => {
		const near = [pt({ time: '00:00:00' }), pt({ time: '00:01:40' })]; // last = 100s
		expect(computeCumulativeTime(near, 105)).toBe(100); // |100-105| < 10.5 -> adopt

		const far = [pt({ time: '00:00:00' }), pt({ time: '00:01:40' })];
		expect(computeCumulativeTime(far, 200)).toBe(200); // |100-200| >= 20 -> keep
	});

	it('linear-interpolates when no time strings but total > 0; index-based when neither', () => {
		const linear = [pt({}), pt({}), pt({}), pt({}), pt({})]; // 5 points, no time
		expect(computeCumulativeTime(linear, 40)).toBe(40);
		expect(linear.map((p) => p._cumulativeTime)).toEqual([0, 10, 20, 30, 40]);

		const indexed = [pt({}), pt({}), pt({}), pt({}), pt({})];
		expect(computeCumulativeTime(indexed, 0)).toBe(5); // total becomes length
		expect(indexed.map((p) => p._cumulativeTime)).toEqual([0, 1, 2, 3, 4]);
	});
});
