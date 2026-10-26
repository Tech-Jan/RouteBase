import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { must } from '../core/invariant';
import type { GpsPoint } from '../core/core-types';

// maplibre-terrain is the shared 3D engine behind BOTH the live map and the race replay — and until
// now every test that touched it did `vi.mock('../maplibre/maplibre-terrain')`, so its own code had
// never executed in the suite. That is where the fullscreen-resize draw_terrain crash lived.
//
// Here the module runs FOR REAL. Only its leaf collaborators are faked (helpers, track layer, style
// swap, dispatcher), so what is exercised is this file's own decisions: how it resolves a centre, when
// a restored camera wins, whether it tears the old map down before building a new one, which of the two
// terrain paths a style takes, and the guards around style switching.

const helpers = {
	terrainExaggeration: vi.fn(() => 1.5),
	TERRAIN_PITCH: 60,
	DEFAULT_PITCH: 45,
	defaultMapOptions: {
		maxZoom: 20, maxPitch: 85, fadeDuration: 0,
		refreshExpiredTiles: false, renderWorldCopies: false, attributionControl: false,
	},
	centerFromGpsData: vi.fn((pts: GpsPoint[]) => [must(pts[0], 'fixture point').lng, must(pts[0], 'fixture point').lat] as [number, number]),
	buildInlineTerrainStyle: vi.fn(() => ({ version: 8, layers: [], sources: {} })),
	applyMapBackground: vi.fn(),
	applySky: vi.fn(),
	addHillshadeAndLight: vi.fn(),
	ensureNavAndTerrainControls: vi.fn(),
	destroyInstance: vi.fn(),
	fetchStyleJson: vi.fn(() => Promise.resolve({ version: 8, layers: [], sources: {} })),
};

const track = {
	bind: vi.fn(),
	addTrackToMap: vi.fn(),
	updateTrackColors: vi.fn(),
	destroy: vi.fn(),
};

const swapStyleWithTerrain = vi.fn();

vi.mock('./maplibre-helpers', () => ({ helpers }));
vi.mock('./maplibre-track', () => ({ api: track }));
vi.mock('./maplibre-style-swap', () => ({ swapStyleWithTerrain: (...args: unknown[]) => swapStyleWithTerrain(...args) }));
vi.mock('./missing-image-fallback', () => ({ installMissingImageFallback: vi.fn() }));
vi.mock('./style-filter-sanitizer', () => ({ installStyleFilterSanitizer: vi.fn() }));
vi.mock('../core/map-utils-maplibre', () => ({ MapLibreAdapter: class { constructor(..._a: unknown[]) {} } }));
vi.mock('../core/map-dispatcher', () => ({
	mapDispatcher: { register: vi.fn(), setupProximityHover: vi.fn() },
	getCurrentColorMode: () => 'none',
}));
vi.mock('../core/basemap-registry', () => ({ demProviderFor: vi.fn(() => 'terrarium') }));
vi.mock('../core/split-state', () => ({ getLockedSplit: () => null }));

interface MapOptions {
	container: string | HTMLElement;
	style: unknown;
	center: [number, number];
	zoom: number;
	pitch: number;
	bearing: number;
}

let built: FakeMap[] = [];

class FakeMap {
	options: MapOptions;
	loadHandlers: Array<() => void> = [];
	jumpTo = vi.fn();
	easeTo = vi.fn();
	setLight = vi.fn();
	remove = vi.fn();
	private _center: [number, number];

	constructor(options: MapOptions) {
		this.options = options;
		this._center = options.center;
		built.push(this);
	}

	once(event: string, fn: () => void): void {
		if (event === 'load') this.loadHandlers.push(fn);
	}

	/** Fire the load handler the module registered — the real map does this after style load. */
	fireLoad(): void {
		for (const fn of this.loadHandlers.splice(0)) fn();
	}

	getCenter(): { toArray(): [number, number] } { return { toArray: () => this._center }; }
	getZoom(): number { return this.options.zoom; }
	getPitch(): number { return this.options.pitch; }
	getBearing(): number { return this.options.bearing; }
}

async function loadModule(): Promise<typeof import('./maplibre-terrain')> {
	return import('./maplibre-terrain');
}

const gps: GpsPoint[] = [
	{ lat: 47.1, lng: 8.4 } as GpsPoint,
	{ lat: 47.2, lng: 8.5 } as GpsPoint,
];

beforeEach(() => {
	vi.resetModules();
	vi.clearAllMocks();
	built = [];
	document.body.innerHTML = '<div id="map"></div>';
	vi.stubGlobal('maplibregl', { Map: FakeMap });
});

afterEach(() => {
	vi.unstubAllGlobals();
	document.body.innerHTML = '';
});

describe('initMapLibreTerrain — centre resolution', () => {
	it('derives the centre from a GPS array', async () => {
		const { initMapLibreTerrain } = await loadModule();

		initMapLibreTerrain('map', gps, 13, undefined, 'terrain');

		expect(helpers.centerFromGpsData).toHaveBeenCalled();
		expect(must(built[0], 'one map built').options.center).toEqual([8.4, 47.1]);
	});

	it('takes an explicit [lng, lat] pair as-is — no GPS derivation', async () => {
		const { initMapLibreTerrain } = await loadModule();

		initMapLibreTerrain('map', [9.9, 46.6], 13, undefined, 'terrain');

		expect(helpers.centerFromGpsData).not.toHaveBeenCalled();
		expect(must(built[0], 'one map built').options.center).toEqual([9.9, 46.6]);
	});

	it('accepts a {lat,lng} object and flips it to [lng, lat]', async () => {
		const { initMapLibreTerrain } = await loadModule();

		initMapLibreTerrain('map', { lat: 46.6, lng: 9.9 }, 13, undefined, 'terrain');

		expect(must(built[0], 'one map built').options.center).toEqual([9.9, 46.6]);
	});

	it('a restored camera wins over the GPS-derived centre, pitch and bearing (MS-02)', async () => {
		const { initMapLibreTerrain } = await loadModule();

		initMapLibreTerrain('map', gps, 13, undefined, 'terrain', null, undefined, {
			center: [1.1, 2.2], zoom: 17, pitch: 30, bearing: 90,
		});

		const opts = must(built[0], 'one map built').options;
		expect(opts.center).toEqual([1.1, 2.2]);
		expect(opts.zoom).toBe(17);
		expect(opts.pitch).toBe(30);
		expect(opts.bearing).toBe(90);
	});
});

describe('initMapLibreTerrain — lifecycle', () => {
	it('tears the previous map down before building a new one', async () => {
		const { initMapLibreTerrain } = await loadModule();

		initMapLibreTerrain('map', gps, 13, undefined, 'terrain');
		expect(helpers.destroyInstance).not.toHaveBeenCalled();

		initMapLibreTerrain('map', gps, 13, undefined, 'terrain');

		// A second live map would keep the old one's DEM/terrain work running against a dead canvas.
		expect(helpers.destroyInstance).toHaveBeenCalledTimes(1);
		expect(built).toHaveLength(2);
	});

	it('returns null instead of building when the terrain style is unbuildable (no key)', async () => {
		helpers.buildInlineTerrainStyle.mockReturnValueOnce(null as never);
		const { initMapLibreTerrain } = await loadModule();

		const map = initMapLibreTerrain('map', gps, 13, undefined, 'terrain');

		expect(map).toBeNull();
		expect(built).toHaveLength(0);
	});

	it('destroyMapLibre releases the track layer and unpublishes the style delegator', async () => {
		const mod = await loadModule();
		mod.initMapLibreTerrain('map', gps, 13, undefined, 'https://tiles.example/a.json');

		mod.destroyMapLibre();

		expect(helpers.destroyInstance).toHaveBeenCalled();
		expect(track.destroy).toHaveBeenCalled(); // MS-07: the track module's dead map + caches

		// The module-level delegators must go inert with the map they closed over — a style switch
		// arriving after teardown would otherwise drive a destroyed map.
		await mod.setMaplibreStyle('https://tiles.example/b.json');
		expect(swapStyleWithTerrain).not.toHaveBeenCalled();
	});
});

describe('initMapLibreTerrain — the two terrain paths at load', () => {
	it('an INLINE terrain style applies light/controls and never re-runs addHillshadeAndLight', async () => {
		const { initMapLibreTerrain } = await loadModule();

		initMapLibreTerrain('map', gps, 13, undefined, 'terrain');
		must(built[0], 'one map built').fireLoad();

		// Calling addHillshadeAndLight here would setTerrain a second time — restarting the DEM fetch
		// cycle and adding duplicate layers, which is what the inline path exists to avoid.
		expect(helpers.addHillshadeAndLight).not.toHaveBeenCalled();
		expect(helpers.applySky).toHaveBeenCalled();
		expect(helpers.ensureNavAndTerrainControls).toHaveBeenCalled();
		expect(must(built[0], 'one map built').setLight).toHaveBeenCalled();
		expect(must(built[0], 'one map built').easeTo).toHaveBeenCalledWith(expect.objectContaining({ pitch: helpers.TERRAIN_PITCH }));
	});

	it('a VECTOR style adds terrain sources through addHillshadeAndLight instead', async () => {
		const { initMapLibreTerrain } = await loadModule();

		initMapLibreTerrain('map', gps, 13, undefined, 'https://tiles.example/style.json');
		must(built[0], 'one map built').fireLoad();

		expect(helpers.addHillshadeAndLight).toHaveBeenCalledTimes(1);
		expect(must(built[0], 'one map built').setLight).not.toHaveBeenCalled();
	});

	it('draws the track on load, and suppresses the initial fit when a camera was restored', async () => {
		const { initMapLibreTerrain } = await loadModule();

		initMapLibreTerrain('map', gps, 13, undefined, 'terrain');
		must(built[0], 'one map built').fireLoad();
		expect(track.addTrackToMap).toHaveBeenCalledWith(gps, false); // no saved camera → fit to track

		vi.clearAllMocks();
		initMapLibreTerrain('map', gps, 13, undefined, 'terrain', null, undefined, {
			center: [1.1, 2.2], zoom: 17, pitch: 30, bearing: 90,
		});
		must(built[1], 'second map built').fireLoad();
		// Fitting here would re-frame the whole track and throw away the viewport Back just restored.
		expect(track.addTrackToMap).toHaveBeenCalledWith(gps, true);
	});

	it('calls onMapReady after the load work, not before', async () => {
		const order: string[] = [];
		helpers.addHillshadeAndLight.mockImplementation(() => { order.push('terrain'); });
		const { initMapLibreTerrain } = await loadModule();

		initMapLibreTerrain('map', gps, 13, undefined, 'https://tiles.example/style.json', null, {
			onMapReady: () => { order.push('ready'); },
		});
		must(built[0], 'one map built').fireLoad();

		expect(order).toEqual(['terrain', 'ready']);
	});

	it('a throwing hook cannot take the map down with it', async () => {
		const { initMapLibreTerrain } = await loadModule();

		initMapLibreTerrain('map', gps, 13, undefined, 'terrain', null, {
			onMapReady: () => { throw new Error('page blew up'); },
		});

		expect(() => must(built[0], 'one map built').fireLoad()).not.toThrow();
	});
});

describe('setMaplibreStyle', () => {
	it('is a no-op when the style is already active', async () => {
		const mod = await loadModule();
		mod.initMapLibreTerrain('map', gps, 13, undefined, 'https://tiles.example/a.json');

		await mod.setMaplibreStyle('https://tiles.example/a.json');

		expect(swapStyleWithTerrain).not.toHaveBeenCalled();
	});

	it('swaps to a different basemap', async () => {
		const mod = await loadModule();
		mod.initMapLibreTerrain('map', gps, 13, undefined, 'https://tiles.example/a.json');

		await mod.setMaplibreStyle('https://tiles.example/b.json');

		expect(swapStyleWithTerrain).toHaveBeenCalledTimes(1);
	});

	it('keeps the current view when the requested terrain style cannot be built', async () => {
		const mod = await loadModule();
		mod.initMapLibreTerrain('map', gps, 13, undefined, 'https://tiles.example/a.json');
		helpers.buildInlineTerrainStyle.mockReturnValueOnce(null as never);

		await mod.setMaplibreStyle('terrain');

		// Resolving BEFORE the destructive swap is the point: a keyless terrain request must not
		// strand the user on a bare map.
		expect(swapStyleWithTerrain).not.toHaveBeenCalled();
	});

	it('does nothing at all when no map is live', async () => {
		const mod = await loadModule();

		await expect(mod.setMaplibreStyle('https://tiles.example/b.json')).resolves.toBeUndefined();
		expect(swapStyleWithTerrain).not.toHaveBeenCalled();
	});

	it('a superseded switch does not re-add its layers (generation guard)', async () => {
		const mod = await loadModule();
		mod.initMapLibreTerrain('map', gps, 13, undefined, 'https://tiles.example/a.json');

		// Hold the first fetch open so the second switch overtakes it.
		let releaseFirst: (v: unknown) => void = () => {};
		helpers.fetchStyleJson
			.mockReturnValueOnce(new Promise((resolve) => { releaseFirst = resolve; }) as never)
			.mockReturnValueOnce(Promise.resolve({ version: 8, layers: [], sources: {} }) as never);

		const first = mod.setMaplibreStyle('https://tiles.example/b.json');
		const second = mod.setMaplibreStyle('https://tiles.example/c.json');
		await second;
		releaseFirst({ version: 8, layers: [], sources: {} });
		await first;

		// Only the winner may touch the map; the stale one bails at its generation check.
		expect(swapStyleWithTerrain).toHaveBeenCalledTimes(1);
	});
});
