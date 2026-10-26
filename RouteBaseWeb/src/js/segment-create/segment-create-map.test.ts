import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { must } from '../core/invariant';
import { segCreateMap } from './segment-create-map';
import { helpers as H } from '../maplibre/maplibre-helpers';

// SC-02 characterization pins for the MapLibre segment-create engine. Stub
// `maplibregl` per test; fire `load` synchronously. Pattern mirrors
// live-recording-map-maplibre.test.ts.

interface FakeSource {
	setData: ReturnType<typeof vi.fn>;
}

class FakeCanvas {
	style: { cursor: string; background: string } = { cursor: '', background: '' };
}

class FakeMap {
	opts: Record<string, unknown>;
	calls: Array<[string, string]> = [];
	loadCb: (() => void) | null = null;
	paint: Array<[string, string, unknown]> = [];
	private _canvas = new FakeCanvas();
	private _container = document.createElement('div');
	private _sources = new Map<string, FakeSource>();
	private _layers = new Set<string>();

	constructor(opts: Record<string, unknown>) {
		this.opts = opts;
		maps.push(this);
	}

	on(ev: string, cb: () => void): void {
		if (ev === 'load') this.loadCb = cb;
	}
	off(): void {}
	addSource(id: string, _spec: unknown): void {
		this.calls.push(['addSource', id]);
		this._sources.set(id, { setData: vi.fn() });
	}
	addLayer(spec: { id: string }): void {
		this.calls.push(['addLayer', spec.id]);
		this._layers.add(spec.id);
	}
	getSource(id: string): FakeSource | undefined {
		return this._sources.get(id);
	}
	getLayer(id: string): { id: string } | undefined {
		return this._layers.has(id) ? { id } : undefined;
	}
	setPaintProperty(layer: string, prop: string, value: unknown): void {
		this.paint.push([layer, prop, value]);
	}
	getCanvas(): FakeCanvas {
		return this._canvas;
	}
	getContainer(): HTMLElement {
		return this._container;
	}
	setSky(): void {}
	remove(): void {}
}

let maps: FakeMap[] = [];
const map = (): FakeMap => must(maps[maps.length - 1], 'a FakeMap was constructed');
const STYLE_URL = 'https://example.test/style.json';

beforeEach(() => {
	maps = [];
	document.body.innerHTML = '<div id="map"></div>';
	// eslint-disable-next-line @typescript-eslint/no-explicit-any
	(globalThis as any).maplibregl = { Map: FakeMap };
	// BT-06.4: the drawing map now calls addHillshadeAndLight on style load so the
	// 3D toggle appears — mock it so the pin assertions stay on the app's own layers.
	H.addHillshadeAndLight = vi.fn();
});

afterEach(() => {
	segCreateMap.destroy();
	// eslint-disable-next-line @typescript-eslint/no-explicit-any
	delete (globalThis as any).maplibregl;
	document.body.innerHTML = '';
});

describe('segment-create MapLibre engine — SC-02 pins', () => {
	it('init adds 4 sources + 4 layers in z-order and applies background', () => {
		segCreateMap.init('map', STYLE_URL);
		map().loadCb!();

		const added = map().calls;
		expect(added).toEqual([
			['addSource', 'segcreate-activity-src'],
			['addLayer', 'segcreate-activity-line'],
			['addSource', 'segcreate-route-src'],
			['addLayer', 'segcreate-route-line'],
			['addSource', 'segcreate-preview-src'],
			['addLayer', 'segcreate-preview-line'],
			['addSource', 'segcreate-waypoints-src'],
			['addLayer', 'segcreate-waypoints-circle'],
		]);
		// applyMapBackground stamps the canvas + container background.
		expect(map().getCanvas().style.background).toBe('#c9d8e8');
	});

	it('setters flip [lat,lng] → [lng,lat] into setData', () => {
		segCreateMap.init('map', STYLE_URL);
		map().loadCb!();

		segCreateMap.setWaypoints([[47.5, 7.6]]);
		segCreateMap.setRoute([[47.5, 7.6]]);
		segCreateMap.setActivity([[47.5, 7.6]]);

		expect(map().getSource('segcreate-waypoints-src')!.setData).toHaveBeenCalledWith(
			expect.objectContaining({ geometry: { type: 'MultiPoint', coordinates: [[7.6, 47.5]] } }),
		);
		expect(map().getSource('segcreate-route-src')!.setData).toHaveBeenCalledWith(
			expect.objectContaining({ geometry: { type: 'LineString', coordinates: [[7.6, 47.5]] } }),
		);
		expect(map().getSource('segcreate-activity-src')!.setData).toHaveBeenCalledWith(
			expect.objectContaining({ geometry: { type: 'LineString', coordinates: [[7.6, 47.5]] } }),
		);
	});

	it('setPreview switches dasharray + opacity between straight and routed', () => {
		segCreateMap.init('map', STYLE_URL);
		map().loadCb!();

		segCreateMap.setPreview([[47.5, 7.6], [47.6, 7.7]], true);
		expect(map().paint).toContainEqual(['segcreate-preview-line', 'line-dasharray', [1.7, 3.3]]);
		expect(map().paint).toContainEqual(['segcreate-preview-line', 'line-opacity', 0.5]);

		map().paint.length = 0;
		segCreateMap.setPreview([[47.5, 7.6], [47.6, 7.7]], false);
		expect(map().paint).toContainEqual(['segcreate-preview-line', 'line-dasharray', [3.3, 1.7]]);
		expect(map().paint).toContainEqual(['segcreate-preview-line', 'line-opacity', 0.6]);
	});

	it('state set before ready replays on load; _addAllLayers is idempotent', () => {
		segCreateMap.init('map', STYLE_URL);
		// setRoute before load: state stored, no source yet.
		segCreateMap.setRoute([[47.5, 7.6], [47.6, 7.7]]);
		expect(map().calls).toHaveLength(0);

		map().loadCb!();
		// Source now exists; second load fires nothing new (idempotent guard).
		expect(map().getSource('segcreate-route-src')).toBeDefined();
		const countAfterFirst = map().calls.length;
		map().loadCb!();
		expect(map().calls.length).toBe(countAfterFirst);
	});

	it('routes the DEM provider by basemap id on style load (BT-06.4)', () => {
		segCreateMap.init('map', STYLE_URL, null, 'liberty');
		map().loadCb!();
		expect(H.addHillshadeAndLight).toHaveBeenCalledWith(
			expect.anything(), expect.any(Number), true, 'terrarium',
		);

		(H.addHillshadeAndLight as ReturnType<typeof vi.fn>).mockClear();
		segCreateMap.destroy();
		segCreateMap.init('map', STYLE_URL, null, 'streets'); // keyed style → maptiler DEM
		map().loadCb!();
		expect(H.addHillshadeAndLight).toHaveBeenCalledWith(
			expect.anything(), expect.any(Number), true, 'maptiler',
		);
	});

	it('setDrawingCursor toggles canvas crosshair', () => {
		segCreateMap.init('map', STYLE_URL);
		map().loadCb!();

		segCreateMap.setDrawingCursor(true);
		expect(map().getCanvas().style.cursor).toBe('crosshair');
		segCreateMap.setDrawingCursor(false);
		expect(map().getCanvas().style.cursor).toBe('');
	});
});
