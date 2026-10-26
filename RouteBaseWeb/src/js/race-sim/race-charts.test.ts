import { beforeEach, describe, expect, it, vi } from 'vitest';
import { must } from '../core/invariant';

/**
 * The charts COORDINATOR — the thing that ties the strip to the map. It had no test at all, and it owns
 * three rules that are easy to break by tidying:
 *
 *   1. a hover on the strip drops a marker on the course, and a leave detaches it;
 *   2. the map→chart direction is chart-authoritative and playhead-disambiguated, because this course
 *      passes the same place up to seven times;
 *   3. `setLngLat` runs BEFORE `addTo` — the reverse throws inside maplibre's mousemove→render path and
 *      wedges the map's run loop.
 *
 * It used to own a fourth — keeping a SECOND, stacked field-speed strip in step with this one. That strip
 * is a trace on the profile now, so the rule is gone rather than broken: one canvas cannot disagree with
 * itself, and the tests that pinned the sync went with it.
 *
 * The renderer is stubbed: it has its own tests, and what is under test here is the wiring.
 */

const profileStub = {
	setCheckpoints: vi.fn(), setField: vi.fn(), setColors: vi.fn(), setEmphasis: vi.fn(), setFilter: vi.fn(),
	setPlayhead: vi.fn(), setLeadLine: vi.fn(), setHover: vi.fn(), setPlaceable: vi.fn(), setSelected: vi.fn(),
	setSpeedProfile: vi.fn(), tick: vi.fn(), destroy: vi.fn(),
};
/** What the coordinator passed to the renderer's factory — that is where the onHover wiring lands. */
let profileOpts: { onHover?: (m: number | null) => void } = {};

vi.mock('./race-profile', () => ({
	createRaceProfile: (o: typeof profileOpts) => { profileOpts = o; return profileStub; },
}));

import { createRaceCharts } from './race-charts';
import type { RaceCourseDto } from './race-api';

/** Records the order of the calls that the run-loop wedge depended on. */
class FakeMarker {
	static instances: FakeMarker[] = [];
	calls: string[] = [];
	lngLat: [number, number] | null = null;
	map: FakeMap | null = null;
	constructor() { FakeMarker.instances.push(this); }
	setLngLat(ll: [number, number]): this { this.calls.push('setLngLat'); this.lngLat = ll; return this; }
	addTo(map: FakeMap): this { this.calls.push('addTo'); this.map = map; return this; }
	remove(): this { this.calls.push('remove'); this.map = null; return this; }
}

class FakeMap {
	handlers = new Map<string, Array<(e: unknown) => void>>();
	on(event: string, cb: (e: unknown) => void): void {
		const list = this.handlers.get(event) ?? [];
		list.push(cb);
		this.handlers.set(event, list);
	}
	off(event: string, cb: (e: unknown) => void): void {
		this.handlers.set(event, (this.handlers.get(event) ?? []).filter((h) => h !== cb));
	}
	fire(event: string, e: unknown): void {
		for (const h of [...(this.handlers.get(event) ?? [])]) h(e);
	}
	count(event: string): number { return (this.handlers.get(event) ?? []).length; }
}

/**
 * A 1 km out-and-back: nodes 0–5 run east, nodes 6–10 come back over the SAME ground. So one lng/lat is
 * two chainages — the Ski to Sea problem in miniature.
 */
function course(): RaceCourseDto {
	const lng = [0, 1, 2, 3, 4, 5, 4, 3, 2, 1, 0].map((i) => 8.1 + i * 0.001);
	return {
		lat: lng.map(() => 47.9),
		lng,
		elevation: lng.map(() => 500),
		gradePercent: lng.map(() => 0),
		stepMeters: 100,
		totalMeters: 1000,
	};
}

function mount(map: FakeMap | null) {
	return createRaceCharts({
		profileCanvas: document.createElement('canvas'),
		course: course(),
		editable: false,
		map: () => map as unknown as maplibregl.Map | null,
	});
}

describe('createRaceCharts', () => {
	/** The map-hover throttle reads performance.now directly; a test fires faster than 30 Hz. */
	let nowMs = 0;

	beforeEach(() => {
		vi.clearAllMocks();
		FakeMarker.instances = [];
		nowMs = 0;
		vi.spyOn(performance, 'now').mockImplementation(() => nowMs);
		vi.stubGlobal('maplibregl', { Marker: FakeMarker });
	});

	it('a hover on the strip draws its own line and marks the course', () => {
		const map = new FakeMap();
		mount(map);

		profileOpts.onHover?.(300);

		expect(profileStub.setHover).toHaveBeenCalledWith(300);

		const marker = must(FakeMarker.instances[0], 'one marker');
		// 300 m at a 100 m step = node 3.
		expect(marker.lngLat).toEqual([course().lng[3], course().lat[3]]);
		// The wedge: adding a marker with no position throws in Marker._update, inside the render path.
		expect(marker.calls).toEqual(['setLngLat', 'addTo']);
	});

	/**
	 * The speed chart under the map is the inline surface now, and the strip it used to hover is
	 * fullscreen-only — so the dot has to be reachable from outside. The marker half of syncHover never
	 * touches the strip's canvas, which is why this still works while the strip is display:none.
	 */
	it('marks the course for a hover that came from the speed chart, not the strip', () => {
		const charts = mount(new FakeMap());

		charts.hoverAt(300);

		const marker = must(FakeMarker.instances[0], 'one marker');
		expect(marker.lngLat).toEqual([course().lng[3], course().lat[3]]);
		expect(marker.calls).toEqual(['setLngLat', 'addTo']);
		// The strip's own hover line is driven too, so entering fullscreen mid-hover is not a jump.
		expect(profileStub.setHover).toHaveBeenCalledWith(300);
	});

	it('detaches the course marker when the speed chart says the pointer left', () => {
		const charts = mount(new FakeMap());
		charts.hoverAt(300);

		charts.hoverAt(null);

		expect(must(FakeMarker.instances[0], 'one marker').map).toBeNull();
		expect(profileStub.setHover).toHaveBeenLastCalledWith(null);
	});

	it('the field-speed aggregate goes to the profile — it is a trace on it, not a chart of its own', () => {
		const charts = mount(new FakeMap());
		const profile = { bins: [], totalMeters: 1000, maxSpeedKmh: 0 };

		charts.setSpeedProfile(profile);
		expect(profileStub.setSpeedProfile).toHaveBeenCalledWith(profile);
	});

	it('leaving the strip clears the line and detaches the course marker', () => {
		const map = new FakeMap();
		const charts = mount(map);
		charts.setPlayhead(100);
		profileOpts.onHover?.(300);

		profileOpts.onHover?.(null);

		expect(profileStub.setHover).toHaveBeenLastCalledWith(null);
		expect(must(FakeMarker.instances[0], 'one marker').map).toBeNull();
	});

	it('a map hover resolves to the pass the PLAYHEAD is on, not merely the nearest node', () => {
		const map = new FakeMap();
		const charts = mount(map);
		charts.attachMap();

		// The point where the course doubles back on itself: node 3 (300 m) and node 7 (700 m) are the
		// same ground. Which one the viewer means depends on where the race is.
		const doubled = { lngLat: { lng: course().lng[3], lat: 47.9 } };

		charts.setPlayhead(750);
		map.fire('mousemove', doubled);
		expect(profileStub.setHover).toHaveBeenLastCalledWith(700);

		nowMs += 100;   // clear the 30 Hz map-hover cap
		charts.setPlayhead(250);
		map.fire('mousemove', doubled);
		expect(profileStub.setHover).toHaveBeenLastCalledWith(300);
	});

	it('following a rider withholds the on-map dot but keeps the chart lines live', () => {
		const map = new FakeMap();
		const charts = mount(map);
		profileOpts.onHover?.(300);
		expect(must(FakeMarker.instances[0], 'one marker').map).toBe(map);

		charts.setMapMarkerSuppressed(true);
		// Dropped at once — not on the next hover.
		expect(must(FakeMarker.instances[0], 'one marker').map).toBeNull();

		profileOpts.onHover?.(500);
		expect(must(FakeMarker.instances[0], 'one marker').map).toBeNull();
		expect(profileStub.setHover).toHaveBeenLastCalledWith(500);
	});

	it('attachMap binds one listener pair per map, however often it is called', () => {
		const map = new FakeMap();
		const charts = mount(map);

		charts.attachMap();
		charts.attachMap();   // a style switch calls it again with the SAME map
		expect(map.count('mousemove')).toBe(1);
		expect(map.count('mouseout')).toBe(1);

		charts.destroy();
		expect(map.count('mousemove')).toBe(0);
		expect(map.count('mouseout')).toBe(0);
	});

	it('the playhead, the leader line and the tick reach the strip', () => {
		const charts = mount(new FakeMap());

		charts.setPlayhead(420);
		expect(profileStub.setPlayhead).toHaveBeenCalledWith(420);

		charts.setLeadLine(900);
		expect(profileStub.setLeadLine).toHaveBeenCalledWith(900);
		charts.setLeadLine(null);
		expect(profileStub.setLeadLine).toHaveBeenLastCalledWith(null);

		charts.tick(true);
		expect(profileStub.tick).toHaveBeenCalledWith(true);
	});

	it('survives a hover before the map exists — the payload can land first', () => {
		const charts = mount(null);
		expect(() => profileOpts.onHover?.(300)).not.toThrow();
		expect(profileStub.setHover).toHaveBeenCalledWith(300);
		expect(FakeMarker.instances).toHaveLength(0);
		charts.destroy();
	});
});
