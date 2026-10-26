import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { must } from '../core/invariant';
import { helpers as H } from './maplibre-helpers';
import { setMapTilerApiKey, resetMapTilerApiKey } from '../core/map-api-key';
import { setAccountSettings, resetAccountSettings } from '../core/account-settings';

// BT-04 provider-routing pins for addHillshadeAndLight. Post-C4 behavior:
//   (a) provider 'terrarium' (the default) + no key → adds terrainSource with
//       encoding 'terrarium' + calls setTerrain (keyless 3D now works).
//   (b) provider 'maptiler' + no key → no-op (today's key gate, scoped to maptiler).
//   (c) provider 'maptiler' + key → MapTiler terrain-rgb DEM as before.
//   (d) provider switch re-adds terrainSource (id stays 'terrainSource', invariant 5).

interface Recorded { op: string; id?: string; spec?: unknown; arg?: unknown; }

class FakeMap {
    calls: Recorded[] = [];
    private _sources = new Set<string>();
    private _terrain: unknown = null;

    getSource(id: string): object | undefined { return this._sources.has(id) ? {} : undefined; }
    addSource(id: string, spec: unknown): void { this.calls.push({ op: 'addSource', id, spec }); this._sources.add(id); }
    removeSource(id: string): void { this.calls.push({ op: 'removeSource', id }); this._sources.delete(id); }
    getTerrain(): unknown { return this._terrain; }
    setTerrain(arg: unknown): void { this.calls.push({ op: 'setTerrain', arg }); this._terrain = arg; }
    getLayer(): undefined { return undefined; }
    addLayer(spec: { id: string }): void { this.calls.push({ op: 'addLayer', id: spec.id, spec }); }
    removeLayer(): void {}
    setPaintProperty(): void {}
    setLight(): void {}
    addControl(): void { this.calls.push({ op: 'addControl' }); }
    easeTo(): void {}
}

function setKey(key: string | undefined): void {
    // The key now lives in the core/map-api-key seam, not window.MAPTILER_API_KEY.
    if (key === undefined) resetMapTilerApiKey();
    else setMapTilerApiKey(key);
}

beforeEach(() => {
    setKey(undefined);
    H.clearStyleCache(); // isolate the inline/style caches per test (MP2-03/06)
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    resetAccountSettings();
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    (globalThis as any).maplibregl = { NavigationControl: class {}, TerrainControl: class {} };
    // Control singletons are window globals; reset so each test exercises a clean add.
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    delete (window as any)._maplibreNavControl;
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    delete (window as any)._maplibreTerrainControl;
    vi.spyOn(console, 'error').mockImplementation(() => {});
    vi.spyOn(console, 'warn').mockImplementation(() => {});
});

afterEach(() => {
    setKey(undefined);
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    delete (globalThis as any).maplibregl;
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    resetAccountSettings();
    vi.restoreAllMocks();
});

/* eslint-disable @typescript-eslint/no-explicit-any */
describe('ensureNavAndTerrainControls — MP2-01 per-map control tracking', () => {
    it('adds exactly one nav + one terrain control per map (idempotent per instance)', () => {
        const map = new FakeMap();
        H.ensureNavAndTerrainControls(map as unknown as maplibregl.Map, 1.0);
        H.ensureNavAndTerrainControls(map as unknown as maplibregl.Map, 1.0);
        expect(map.calls.filter((c) => c.op === 'addControl').length).toBe(2);
    });

    it('does not cross-contaminate two different maps', () => {
        const m1 = new FakeMap();
        const m2 = new FakeMap();
        H.ensureNavAndTerrainControls(m1 as unknown as maplibregl.Map, 1.0);
        H.ensureNavAndTerrainControls(m2 as unknown as maplibregl.Map, 1.0);
        expect(m1.calls.filter((c) => c.op === 'addControl').length).toBe(2);
        expect(m2.calls.filter((c) => c.op === 'addControl').length).toBe(2);
    });
});

describe('buildInlineTerrainStyle — MP2-06 / MP2-03', () => {
    it('returns null and logs when no API key', () => {
        const style = H.buildInlineTerrainStyle(1.0);
        expect(style).toBeNull();
        expect(console.error).toHaveBeenCalled();
    });

    it('omits the hills layer when hillshadeExaggeration is 0, includes it when > 0', () => {
        setKey('abc');
        setAccountSettings({ map: { hillshadeExaggeration: 0 } });
        const off = H.buildInlineTerrainStyle(1.0)!;
        expect((off.layers as any[]).some((l) => l.id === 'hills')).toBe(false);

        H.clearStyleCache();
        setAccountSettings({ map: { hillshadeExaggeration: 0.5 } });
        const on = H.buildInlineTerrainStyle(1.0)!;
        const hills = (on.layers as any[]).find((l) => l.id === 'hills');
        expect(hills).toBeDefined();
        expect(hills.paint['hillshade-shadow-color']).toBe('#473B24');
    });

    it('returns a fresh clone each call (not the cached instance)', () => {
        setKey('abc');
        setAccountSettings({ map: { hillshadeExaggeration: 0 } });
        const a = H.buildInlineTerrainStyle(1.0)!;
        const b = H.buildInlineTerrainStyle(1.0)!;
        expect(a).not.toBe(b);
        expect(a).toEqual(b);
    });

    it('clearStyleCache resets the inline terrain style cache (MP2-03)', () => {
        setKey('abc');
        setAccountSettings({ map: { hillshadeExaggeration: 0 } });
        const stale = H.buildInlineTerrainStyle(1.0)!;
        expect((stale.layers as any[]).some((l) => l.id === 'hills')).toBe(false);

        // Change the setting WITHOUT clearing — cache keeps the stale (no-hills) decision.
        setAccountSettings({ map: { hillshadeExaggeration: 1 } });
        const cached = H.buildInlineTerrainStyle(1.0)!;
        expect((cached.layers as any[]).some((l) => l.id === 'hills')).toBe(false);

        // After clearStyleCache the inline cache is dropped → fresh decision includes hills.
        H.clearStyleCache();
        const fresh = H.buildInlineTerrainStyle(1.0)!;
        expect((fresh.layers as any[]).some((l) => l.id === 'hills')).toBe(true);
    });
});

describe('stripBuiltInTerrain — MP2-06', () => {
    it('removes raster-dem sources + their layers and viewport/terrain keys, keeps others', () => {
        const style = {
            version: 8,
            center: [1, 2], zoom: 5, bearing: 10, pitch: 20,
            terrain: { source: 'dem' },
            sources: { dem: { type: 'raster-dem' }, streets: { type: 'vector' } },
            layers: [
                { id: 'hillshade', type: 'hillshade', source: 'dem' },
                { id: 'roads', type: 'line', source: 'streets' },
            ],
        } as any;
        const out = H.stripBuiltInTerrain(style) as any;
        expect(out.terrain).toBeUndefined();
        expect(out.center).toBeUndefined();
        expect(out.zoom).toBeUndefined();
        expect(out.bearing).toBeUndefined();
        expect(out.pitch).toBeUndefined();
        expect(out.sources.dem).toBeUndefined();
        expect(out.sources.streets).toBeDefined();
        expect(out.layers.map((l: any) => l.id)).toEqual(['roads']);
    });
});

describe('centerFromGpsData / coordsFromGps — MP2-06', () => {
    it('averages mixed lat/lng + latitude/longitude points; null on empty/all-invalid', () => {
        const center = H.centerFromGpsData([
            { lat: 0, lng: 0 },
            { latitude: 2, longitude: 4 },
        ] as any);
        expect(center).toEqual([2, 1]); // [avgLng, avgLat]
        expect(H.centerFromGpsData([] as any)).toBeNull();
        expect(H.centerFromGpsData([{ foo: 1 } as any])).toBeNull();
    });

    it('maps mixed-shape points to [lng,lat] pairs and filters NaN entries', () => {
        const coords = H.coordsFromGps([
            { lat: 1, lng: 2 },
            { latitude: 3, longitude: 4 },
            { lat: NaN, lng: 5 },
        ] as any);
        expect(coords).toEqual([[2, 1], [4, 3]]);
    });
});

describe('applySky — MP2-06', () => {
    it('setSky({}) when fogDistance is 0', () => {
        setAccountSettings({ map: { fogDistance: 0 } });
        const calls: unknown[] = [];
        const map = { setSky: (c: unknown) => calls.push(c) } as any;
        H.applySky(map);
        expect(calls).toEqual([{}]);
    });

    it('clamps fog-ground-blend into [0.1,1.0] at the default fogDistance', () => {
        setAccountSettings({ map: { fogDistance: 10 } });
        let sky: any = null;
        const map = { setSky: (c: any) => { sky = c; } } as any;
        H.applySky(map);
        expect(sky['fog-ground-blend']).toBeGreaterThanOrEqual(0.1);
        expect(sky['fog-ground-blend']).toBeLessThanOrEqual(1.0);
        expect(sky['fog-ground-blend']).toBeCloseTo(0.5, 6); // 10/20
    });

    it('falls back to setFog when setSky is unavailable', () => {
        setAccountSettings({ map: { fogDistance: 10 } });
        let fog: any = null;
        const map = { setFog: (c: any) => { fog = c; } } as any;
        H.applySky(map);
        expect(fog).not.toBeNull();
        expect(fog.range).toEqual([Math.max(0.5, 1), 10]);
    });
});
/* eslint-enable @typescript-eslint/no-explicit-any */

describe('addHillshadeAndLight — BT-04 provider routing', () => {
    it('(a) provider terrarium (default) + no key: adds terrarium terrainSource and calls setTerrain', () => {
        const map = new FakeMap();
        // Default provider (no 4th arg) is 'terrarium' — keyless.
        H.addHillshadeAndLight(map as unknown as maplibregl.Map, 1.0, true);

        const src = map.calls.find(c => c.op === 'addSource' && c.id === 'terrainSource');
        expect(src).toBeDefined();
        expect(src!.spec).toMatchObject({ type: 'raster-dem', encoding: 'terrarium', tileSize: 256 });

        const terrain = map.calls.find(c => c.op === 'setTerrain');
        expect(terrain).toBeDefined();
        expect(terrain!.arg).toMatchObject({ source: 'terrainSource' });
    });

    it('(b) provider maptiler + no key: hard no-op — no source added, no setTerrain', () => {
        const map = new FakeMap();
        H.addHillshadeAndLight(map as unknown as maplibregl.Map, 1.0, true, 'maptiler');

        expect(map.calls.find(c => c.op === 'addSource')).toBeUndefined();
        expect(map.calls.find(c => c.op === 'setTerrain')).toBeUndefined();
    });

    it('(c) provider maptiler + key: adds the MapTiler terrain-rgb DEM source', () => {
        setKey('abc');
        const map = new FakeMap();
        H.addHillshadeAndLight(map as unknown as maplibregl.Map, 1.0, true, 'maptiler');

        const src = map.calls.find(c => c.op === 'addSource' && c.id === 'terrainSource');
        expect(src).toBeDefined();
        expect(src!.spec).toMatchObject({ type: 'raster-dem', tileSize: 256 });
        expect((src!.spec as { url: string }).url).toContain('api.maptiler.com');

        const terrain = map.calls.find(c => c.op === 'setTerrain');
        expect(terrain).toBeDefined();
        expect(terrain!.arg).toMatchObject({ source: 'terrainSource' });
    });

    it('(d) provider switch: re-adds terrainSource for the new provider (id unchanged)', () => {
        setKey('abc');
        const map = new FakeMap();

        // Start on a keyless terrarium basemap.
        H.addHillshadeAndLight(map as unknown as maplibregl.Map, 1.0, true, 'terrarium');
        // Switch to a MapTiler basemap → the terrarium source must be removed + re-added.
        H.addHillshadeAndLight(map as unknown as maplibregl.Map, 1.0, true, 'maptiler');

        const adds = map.calls.filter(c => c.op === 'addSource' && c.id === 'terrainSource');
        expect(adds).toHaveLength(2);
        expect(must(adds[0], 'first add').spec).toMatchObject({ encoding: 'terrarium' });
        expect((must(adds[1], 'second add').spec as { url: string }).url).toContain('api.maptiler.com');

        // The stale terrarium source was removed between the two adds.
        expect(map.calls.find(c => c.op === 'removeSource' && c.id === 'terrainSource')).toBeDefined();
    });
});
