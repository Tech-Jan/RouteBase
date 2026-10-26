// @vitest-environment node
import { describe, expect, it, vi } from 'vitest';
import { must } from '../core/invariant';
import { RaceField, createThinSource, type CourseGrid } from './race-field';
import {
	LABEL_LAYER_ID,
	LABEL_SOURCE_ID,
	RIDER_FILL_LAYER_ID,
	RIDER_HALO_LAYER_ID,
	RIDER_SOURCE_ID,
	createRaceMarkers,
	type RiderMeta,
} from './race-markers';

interface FakeSource {
	setData: ReturnType<typeof vi.fn>;
	data?: { features: Array<{ properties: Record<string, unknown>; geometry: { coordinates: number[] } }> };
}

/** Mirrors the FakeMap in segment-stats-replay-maplibre.test.ts. */
class FakeMap {
	calls: Array<[string, string]> = [];
	private _sources = new Map<string, FakeSource>();
	private _layers = new Set<string>();

	addSource(id: string, _spec: unknown): void {
		this.calls.push(['addSource', id]);
		const src: FakeSource = { setData: vi.fn((d) => { src.data = d; }) };
		this._sources.set(id, src);
	}
	/** Specs are kept, not just ids: the radius and the sort key ARE the emphasis feature. */
	layerSpecs = new Map<string, { layout?: Record<string, unknown>; paint?: Record<string, unknown> }>();
	addLayer(spec: { id: string; layout?: Record<string, unknown>; paint?: Record<string, unknown> }): void {
		this.calls.push(['addLayer', spec.id]);
		this._layers.add(spec.id);
		this.layerSpecs.set(spec.id, spec);
	}
	getSource(id: string): FakeSource | undefined { return this._sources.get(id); }
	getLayer(id: string): unknown { return this._layers.has(id) ? { id } : undefined; }
	removeLayer(id: string): void { this.calls.push(['removeLayer', id]); this._layers.delete(id); }
	removeSource(id: string): void { this.calls.push(['removeSource', id]); this._sources.delete(id); }

	filters = new Map<string, unknown>();
	setFilter(layerId: string, filter: unknown): void { this.filters.set(layerId, filter); }
}

const STEP = 10;

function course(n: number): CourseGrid {
	const distance = new Float64Array(n);
	const lat = new Float64Array(n);
	const lng = new Float64Array(n);
	for (let i = 0; i < n; i++) {
		distance[i] = i * STEP;
		lat[i] = 47.9;
		lng[i] = 8.1 + i * 0.0001;
	}
	return {
		distance, lat, lng,
		elevation: new Float64Array(n),
		gradePercent: new Float64Array(n),
		stepMeters: STEP,
	};
}

function evenArrivals(n: number, total: number): Float32Array {
	const a = new Float32Array(n);
	for (let i = 0; i < n; i++) a[i] = (total * i) / (n - 1);
	return a;
}

function setup(riderCount: number, reals: number[] = [], emphasised: number[] = []) {
	const g = course(101);
	const field = new RaceField(
		Array.from({ length: riderCount }, (_, i) => createThinSource(g, evenArrivals(101, 1000 + i * 10))),
	);
	const meta: RiderMeta[] = Array.from({ length: riderCount }, (_, i) => ({
		name: `Rider ${i}`,
		color: '#ff0000',
		isReal: reals.includes(i),
		emphasised: emphasised.includes(i),
	}));
	const map = new FakeMap();
	let clock = 0;
	const markers = createRaceMarkers(
		map as unknown as maplibregl.Map, field, meta,
		{ maxUpdatesPerSecond: 30, now: () => clock },
	);
	return { map, field, markers, tick: (ms: number) => { clock += ms; } };
}

describe('createRaceMarkers', () => {
	it('creates one circle source for the field and a SEPARATE source for labels', () => {
		// The split is the whole point: symbol layers run text collision detection, which is superlinear
		// in label count. 424 labels would drown the map, so labels get their own small source.
		const { map } = setup(424);

		expect(map.getSource(RIDER_SOURCE_ID)).toBeDefined();
		expect(map.getSource(LABEL_SOURCE_ID)).toBeDefined();
		expect(map.getLayer(RIDER_HALO_LAYER_ID)).toBeDefined();
		expect(map.getLayer(RIDER_FILL_LAYER_ID)).toBeDefined();
		expect(map.getLayer(LABEL_LAYER_ID)).toBeDefined();
	});

	it('the label source stays empty until someone is named — 424 riders, 0 labels', () => {
		const { map, field, markers, tick } = setup(424);

		field.advanceTo(500);
		tick(100);
		markers.flush();

		expect(map.getSource(RIDER_SOURCE_ID)!.data!.features).toHaveLength(424);
		expect(map.getSource(LABEL_SOURCE_ID)!.data!.features).toHaveLength(0);
	});

	it('labels only the named few, and follows them', () => {
		const { map, field, markers, tick } = setup(424);

		markers.setLabelled([3, 7]);
		field.advanceTo(500);
		tick(100);
		markers.flush();

		const labels = map.getSource(LABEL_SOURCE_ID)!.data!.features;
		expect(labels).toHaveLength(2);
		const label0 = must(labels[0], 'one label');
		expect(label0.properties.name).toBe('Rider 3');
		expect(label0.geometry.coordinates[1]).toBeCloseTo(field.lats[3] ?? NaN, 9);
	});

	it('one flush is one setData per source, however many riders moved', () => {
		const { map, field, markers, tick } = setup(424);
		const src = map.getSource(RIDER_SOURCE_ID)!;
		src.setData.mockClear();

		field.advanceTo(500);
		tick(100);
		markers.flush();

		expect(src.setData).toHaveBeenCalledTimes(1);   // 424 moves coalesce into one upload
	});

	it('throttles: a 60 fps caller does not push 60 uploads a second', () => {
		// The position maths is free; MapLibre re-tiling the source is not. This is the dial that matters.
		const { map, field, markers, tick } = setup(424);
		const src = map.getSource(RIDER_SOURCE_ID)!;
		src.setData.mockClear();

		for (let frame = 0; frame < 60; frame++) {
			field.advanceTo(frame * 10);
			tick(1000 / 60);
			markers.flush();
		}

		// 1 s of 60 fps playback at a 30 Hz cap.
		expect(src.setData.mock.calls.length).toBeLessThanOrEqual(31);
		expect(src.setData.mock.calls.length).toBeGreaterThan(25);
	});

	it('flushNow ignores the throttle — after a seek the markers must not lag the playhead', () => {
		const { map, field, markers, tick } = setup(10);
		const src = map.getSource(RIDER_SOURCE_ID)!;

		tick(100);
		markers.flush();
		src.setData.mockClear();

		field.advanceTo(900);
		markers.flushNow();          // no tick: the throttle would have swallowed a flush()

		expect(src.setData).toHaveBeenCalledTimes(1);
		expect(must(must(src.setData.mock.calls[0], 'one setData')[0].features[0], 'one feature').geometry.coordinates[0])
			.toBeCloseTo(field.lngs[0] ?? NaN, 9);
	});

	it('carries real/finished/highlighted through to the paint properties', () => {
		const { map, field, markers, tick } = setup(3, [1]);

		markers.setHighlight(2, true);
		field.advanceTo(99999);       // everyone home
		tick(100);
		markers.flush();

		const f = map.getSource(RIDER_SOURCE_ID)!.data!.features;
		expect(must(f[1], 'rider 1').properties.real).toBe(true);     // the real rider is styled apart
		expect(must(f[0], 'rider 0').properties.real).toBe(false);
		expect(must(f[2], 'rider 2').properties.highlighted).toBe(true);
		expect(must(f[0], 'rider 0').properties.finished).toBe(true);
	});

	it('a flush after the style dropped the source is survivable, not a crash', () => {
		const { map, field, markers, tick } = setup(10);

		map.removeSource(RIDER_SOURCE_ID);
		field.advanceTo(500);
		tick(100);

		expect(() => markers.flush()).not.toThrow();
	});

	it('destroy removes every layer before its source', () => {
		const { map, markers } = setup(10);

		markers.destroy();

		for (const srcId of [RIDER_SOURCE_ID, LABEL_SOURCE_ID]) {
			const srcIdx = map.calls.findIndex(([op, id]) => op === 'removeSource' && id === srcId);
			expect(srcIdx).toBeGreaterThanOrEqual(0);
			const layersAfter = map.calls.slice(srcIdx).filter(([op]) => op === 'removeLayer');
			expect(layersAfter).toHaveLength(0);
		}
		expect(map.getLayer(RIDER_FILL_LAYER_ID)).toBeUndefined();
	});

	it('destroy removes both sources and all three layers', () => {
		// destroy() had NO production caller — the layers simply outlived the map on every teardown path.
		const { map, markers } = setup(10);

		markers.destroy();

		for (const id of [RIDER_HALO_LAYER_ID, RIDER_FILL_LAYER_ID, LABEL_LAYER_ID]) {
			expect(map.getLayer(id), id).toBeUndefined();
		}
		for (const id of [RIDER_SOURCE_ID, LABEL_SOURCE_ID]) {
			expect(map.getSource(id), id).toBeUndefined();
		}
	});

	it('a flush after destroy is survivable, not a crash', () => {
		// The bfcache path: the map goes, a stale rAF frame still lands.
		const { markers, field, tick } = setup(10);

		markers.destroy();
		field.advanceTo(500);
		tick(100);

		expect(() => markers.flush()).not.toThrow();
		expect(() => markers.flushNow()).not.toThrow();
	});

	it('destroy() then createRaceMarkers() on the same map re-adds both sources and both layers', () => {
		// The style-switch re-attach: the new style dropped our sources, so the page rebuilds the markers
		// on the SAME map object. Nothing may be left over to collide with.
		const { map, field, markers } = setup(10);
		const meta: RiderMeta[] = Array.from({ length: 10 }, (_, i) => ({
			name: `Rider ${i}`, color: '#ff0000', isReal: false, emphasised: false,
		}));

		markers.destroy();
		map.calls.length = 0;

		const reattached = createRaceMarkers(map as unknown as maplibregl.Map, field, meta);

		expect(map.calls.filter(([op, id]) => op === 'addSource' && id === RIDER_SOURCE_ID)).toHaveLength(1);
		expect(map.calls.filter(([op, id]) => op === 'addSource' && id === LABEL_SOURCE_ID)).toHaveLength(1);
		for (const id of [RIDER_HALO_LAYER_ID, RIDER_FILL_LAYER_ID, LABEL_LAYER_ID]) {
			expect(map.getLayer(id), id).toBeDefined();
		}
		expect(() => reattached.flushNow()).not.toThrow();
	});

	it('reuses the label feature objects across flushes — a flush allocates nothing', () => {
		// The file's own header promises "coordinates mutated in place so a frame allocates nothing", but
		// writePositions cleared the label array and pushed a fresh literal per labelled rider per flush.
		const { map, field, markers, tick } = setup(10, [0, 1]);
		markers.setLabelled([0, 1]);
		markers.flushNow();

		const src = map.getSource(LABEL_SOURCE_ID)!;
		const first = must(src.data!.features[0], 'one feature');
		const firstCoords = first.geometry.coordinates;

		field.advanceTo(600);
		tick(100);
		markers.flush();

		// Same objects, moved — not new ones.
		expect(src.data!.features[0]).toBe(first);
		expect(must(src.data!.features[0], 'one feature').geometry.coordinates).toBe(firstCoords);
		expect(firstCoords[0]).toBeCloseTo(field.lngs[0] ?? NaN, 9);
		expect(firstCoords[1]).toBeCloseTo(field.lats[0] ?? NaN, 9);
	});

	it('setLabelled resizes the pool and re-stamps the names', () => {
		const { map, markers } = setup(10, [0]);

		markers.setLabelled([0, 1, 2]);
		markers.flushNow();
		const src = map.getSource(LABEL_SOURCE_ID)!;
		expect(src.data!.features).toHaveLength(3);
		expect(src.data!.features.map((f) => f.properties.name)).toEqual(['Rider 0', 'Rider 1', 'Rider 2']);

		markers.setLabelled([7]);
		markers.flushNow();
		expect(src.data!.features).toHaveLength(1);
		expect(must(src.data!.features[0], 'one feature').properties.name).toBe('Rider 7');
	});

	it('rejects a meta list that does not match the field', () => {
		const g = course(101);
		const field = new RaceField([createThinSource(g, evenArrivals(101, 1000))]);
		const map = new FakeMap();

		expect(() =>
			createRaceMarkers(map as unknown as maplibregl.Map, field, [], {}),
		).toThrow(/meta entries/);
	});

	// ── the results table's three seams: paint, dim, isolate ──────────────────────────────────────

	describe('setColors', () => {
		it('repaints by index and rides along with the next position flush', () => {
			const { map, markers } = setup(4);

			markers.setColors(new Map([[1, '#e64980'], [3, '#e64980']]));
			markers.flushNow();

			const features = map.getSource(RIDER_SOURCE_ID)!.data!.features;
			expect(features.map((f) => f.properties.color))
				.toEqual(['#ff0000', '#e64980', '#ff0000', '#e64980']);
		});

		it('ignores an index outside the field rather than throwing', () => {
			const { map, markers } = setup(2);

			markers.setColors(new Map([[99, '#e64980'], [-1, '#e64980']]));
			markers.flushNow();

			expect(map.getSource(RIDER_SOURCE_ID)!.data!.features.map((f) => f.properties.color))
				.toEqual(['#ff0000', '#ff0000']);
		});
	});

	describe('setEmphasis', () => {
		it('flags by index and rides along with the next position flush', () => {
			const { map, markers } = setup(4);

			markers.setEmphasis(new Map([[1, true], [3, true]]));
			markers.flushNow();

			expect(map.getSource(RIDER_SOURCE_ID)!.data!.features.map((f) => f.properties.emphasised))
				.toEqual([false, true, false, true]);
		});

		it('turns emphasis back off again', () => {
			const { map, markers } = setup(3);
			markers.setEmphasis(new Map([[0, true], [1, true]]));

			markers.setEmphasis(new Map([[1, false]]));
			markers.flushNow();

			expect(map.getSource(RIDER_SOURCE_ID)!.data!.features.map((f) => f.properties.emphasised))
				.toEqual([true, false, false]);
		});

		it('ignores an index outside the field rather than throwing', () => {
			const { map, markers } = setup(2);

			markers.setEmphasis(new Map([[99, true], [-1, true]]));
			markers.flushNow();

			expect(map.getSource(RIDER_SOURCE_ID)!.data!.features.every((f) => f.properties.emphasised === false))
				.toBe(true);
		});

		it('carries a BUILD-time emphasis onto the feature — a style switch rebuilds from meta', () => {
			const { map, markers } = setup(3, [], [1]);
			markers.flushNow();

			expect(map.getSource(RIDER_SOURCE_ID)!.data!.features.map((f) => f.properties.emphasised))
				.toEqual([false, true, false]);
		});

		/**
		 * Size and draw order have to agree. A rider drawn 10px but sorted UNDER the field, or sorted to
		 * the front while still 4px, reads as a rendering bug — and neither is visible in a unit test that
		 * only checks one of the two expressions.
		 */
		it('the radius and the sort key rank riders in the SAME order', () => {
			const { map } = setup(1);
			const fill = must(map.layerSpecs.get(RIDER_FILL_LAYER_ID), 'the fill layer was added');

			const order = (expression: unknown): string[] =>
				(expression as unknown[]).filter((token): token is string[] =>
					Array.isArray(token) && token[0] === 'get').map((token) => String(token[1]));

			expect(order(fill.paint?.['circle-radius'])).toEqual(order(fill.layout?.['circle-sort-key']));
			expect(order(fill.paint?.['circle-radius'])).toEqual(['dimmed', 'emphasised', 'real', 'highlighted']);
		});

		/**
		 * Emphasis is the SAME size as a real rider — a third, larger tier read as three levels of
		 * importance when there are only two. So the sort key is the ONLY thing separating them, which is
		 * exactly why it is asserted here rather than left implicit.
		 */
		it('an emphasised rider matches a real rider in size, and is sorted above him', () => {
			const { map } = setup(1);
			const fill = must(map.layerSpecs.get(RIDER_FILL_LAYER_ID), 'the fill layer was added');
			const valueFor = (expression: unknown, property: string): number => {
				const tokens = expression as unknown[];
				const at = tokens.findIndex((t) => Array.isArray(t) && t[0] === 'get' && t[1] === property);
				return Number(tokens[at + 1]);
			};
			const radius = (property: string): number => valueFor(fill.paint?.['circle-radius'], property);
			const sortKey = (property: string): number => valueFor(fill.layout?.['circle-sort-key'], property);

			expect(radius('emphasised')).toBe(radius('real'));
			expect(radius('emphasised')).toBeGreaterThan(radius('highlighted'));
			expect(sortKey('emphasised')).toBeGreaterThan(sortKey('real'));
		});
	});

	describe('setDimmed (Highlight mode)', () => {
		it('dims everyone EXCEPT the filtered set', () => {
			const { map, markers } = setup(4);

			markers.setDimmed([1, 2]);
			markers.flushNow();

			expect(map.getSource(RIDER_SOURCE_ID)!.data!.features.map((f) => f.properties.dimmed))
				.toEqual([true, false, false, true]);
		});

		it('null clears the dimming', () => {
			const { map, markers } = setup(3);
			markers.setDimmed([0]);

			markers.setDimmed(null);
			markers.flushNow();

			expect(map.getSource(RIDER_SOURCE_ID)!.data!.features.every((f) => f.properties.dimmed === false))
				.toBe(true);
		});

		it('an empty filter dims the WHOLE field — the honest rendering of "nothing matched"', () => {
			const { map, markers } = setup(3);

			markers.setDimmed([]);
			markers.flushNow();

			expect(map.getSource(RIDER_SOURCE_ID)!.data!.features.every((f) => f.properties.dimmed === true))
				.toBe(true);
		});
	});

	describe('setVisible (Hide mode)', () => {
		it('filters the LAYERS rather than rebuilding the features', () => {
			// The field keeps advancing and flushing for hidden riders, so flipping back to All is instant
			// instead of re-running the pacing model for 424 of them.
			const { map, markers } = setup(4);

			markers.setVisible([2, 3]);

			expect(map.filters.get(RIDER_FILL_LAYER_ID))
				.toEqual(['in', ['get', 'idx'], ['literal', [2, 3]]]);
			expect(map.filters.get(RIDER_HALO_LAYER_ID))
				.toEqual(['in', ['get', 'idx'], ['literal', [2, 3]]]);

			// The features themselves are untouched — all four still there, still moving.
			markers.flushNow();
			expect(map.getSource(RIDER_SOURCE_ID)!.data!.features).toHaveLength(4);
		});

		it('null clears the filter', () => {
			const { map, markers } = setup(3);
			markers.setVisible([0]);

			markers.setVisible(null);

			expect(map.filters.get(RIDER_FILL_LAYER_ID)).toBeNull();
		});

		/**
		 * Hiding a rider must hide his NAME too.
		 *
		 * The filter was applied to the halo and the fill but not to the label layer — and real riders are
		 * always labelled (race-replay labels every `isReal` rider), while carrying no category, gender or
		 * club, so ANY column filter excludes them. Hide mode then took the rider's dot away and left his
		 * name behind, still being repositioned by writePositions() on every flush: a name gliding along
		 * the course with nothing underneath it.
		 */
		it('hides the labels of hidden riders, not just their dots', () => {
			const { map, markers } = setup(4);
			markers.setLabelled([1, 3]);

			markers.setVisible([2, 3]);

			expect(map.filters.get(LABEL_LAYER_ID))
				.toEqual(['in', ['get', 'idx'], ['literal', [2, 3]]]);
		});

		it('labels carry the rider index, or the layer filter has nothing to match on', () => {
			const { map, markers } = setup(4);

			markers.setLabelled([1, 3]);
			markers.flushNow();

			const labels = map.getSource(LABEL_SOURCE_ID)!.data!.features;
			expect(labels.map((f) => f.properties.idx)).toEqual([1, 3]);
		});
	});
});
