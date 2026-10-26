import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { must } from '../core/invariant';
import { helpers as H } from '../maplibre/maplibre-helpers';
import { liveRecMapLibre } from './live-recording-map-maplibre';

// LR-03 characterization pins for the MapLibre live-recording engine. The engine
// survives the LR-01 refactor verbatim, so these lock its person-layer / setStyle /
// destroy behavior first. Pattern mirrors map-utils-maplibre.test.ts:
// stub `maplibregl` per test; the `helpers` import is a mutable export so we swap
// fetchStyleJson to force the deterministic catch → direct-URL setStyle path.

interface FakeSource {
    setData: ReturnType<typeof vi.fn>;
}

const flush = (): Promise<void> => new Promise((r) => setTimeout(r, 0));

class FakeMap {
    opts: Record<string, unknown>;
    calls: Array<[string, string]> = [];
    loadCb: (() => void) | null = null;
    styleLoadCb: (() => void) | null = null;
    idleCb: (() => void) | null = null;
    dragCb: (() => void) | null = null;
    setStyleArg: unknown = null;
    setStyleOpts: unknown = null;
    jumpToArg: unknown = null;
    removed = false;
    private _sources = new Map<string, FakeSource>();
    private _layers = new Set<string>();

    constructor(opts: Record<string, unknown>) {
        this.opts = opts;
        maps.push(this);
    }

    on(ev: string, cb: () => void): void {
        if (ev === 'load') this.loadCb = cb;
        else if (ev === 'dragstart') this.dragCb = cb;
    }
    once(ev: string, cb: () => void): void {
        if (ev === 'style.load') this.styleLoadCb = cb;
        else if (ev === 'idle') this.idleCb = cb;
    }
    off(ev: string, _cb: () => void): void {
        if (ev === 'style.load') this.styleLoadCb = null;
        else if (ev === 'idle') this.idleCb = null;
    }
    addSource(id: string, _spec: unknown): void {
        this.calls.push(['addSource', id]);
        this._sources.set(id, { setData: vi.fn() });
    }
    addLayer(spec: { id: string }): void {
        this.calls.push(['addLayer', spec.id]);
        this._layers.add(spec.id);
    }
    removeLayer(id: string): void {
        this.calls.push(['removeLayer', id]);
        this._layers.delete(id);
    }
    removeSource(id: string): void {
        this.calls.push(['removeSource', id]);
        this._sources.delete(id);
    }
    getSource(id: string): FakeSource | undefined {
        return this._sources.get(id);
    }
    getLayer(id: string): { id: string } | undefined {
        return this._layers.has(id) ? { id } : undefined;
    }
    setStyle(style: unknown, opts?: unknown): void {
        this.calls.push(['setStyle', typeof style === 'string' ? style : 'object']);
        this.setStyleArg = style;
        this.setStyleOpts = opts ?? null;
        // Real MapLibre wipes the style; drop tracked sources/layers so a replay is observable.
        this._sources.clear();
        this._layers.clear();
    }
    // Ported terrain-off-before-swap guard calls this; track the arg so the order (off → swap) is pinnable.
    setTerrain(v: unknown): void { this.calls.push(['setTerrain', v === null ? 'null' : 'set']); }
    jumpTo(cam: unknown): void { this.jumpToArg = cam; }
    panTo(): void {}
    flyTo(): void {}
    getCenter(): [number, number] { return [10, 20]; }
    getZoom(): number { return 14; }
    getBearing(): number { return 30; }
    getPitch(): number { return 45; }
    remove(): void { this.removed = true; }
}

let maps: FakeMap[] = [];
const map = (): FakeMap => must(maps[maps.length - 1], 'a FakeMap was constructed');
const STYLE_URL = 'https://example.test/style.json';

beforeEach(() => {
    maps = [];
    document.body.innerHTML = '<div id="lrmap"></div>';
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    (globalThis as any).maplibregl = { Map: FakeMap };
    // Force setStyle's catch → direct-URL path deterministically.
    H.fetchStyleJson = vi.fn().mockRejectedValue(new Error('offline'));
    // 3D is on by default now — the live map shares the overview/seg-stats terrain path. Mock
    // addHillshadeAndLight (real WebGL terrain has no place in a pure engine test) and assert WHICH DEM
    // provider + skipPitch the engine routes to it.
    H.addHillshadeAndLight = vi.fn();
});

afterEach(() => {
    liveRecMapLibre.destroy();
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    delete (globalThis as any).maplibregl;
    document.body.innerHTML = '';
});

describe('live-recording MapLibre engine — LR-03 pins', () => {
    it('addPerson before load queues layers and replays them on load', () => {
        liveRecMapLibre.init('lrmap', STYLE_URL);
        liveRecMapLibre.addPerson('self', '#e74c3c', '#3498db');
        expect(map().calls).toHaveLength(0); // queued, not yet ready

        map().loadCb!();

        const added = map().calls.filter(([op]) => op === 'addSource' || op === 'addLayer');
        expect(added).toEqual([
            ['addSource', 'live-track-self'],
            ['addLayer', 'live-track-line-self'],
            ['addSource', 'live-pos-self'],
            ['addLayer', 'live-pos-dot-self'],
        ]);
    });

    it('updateTrack sets line data and moves the dot to the last coord', () => {
        liveRecMapLibre.init('lrmap', STYLE_URL);
        liveRecMapLibre.addPerson('self', '#e74c3c', '#3498db');
        map().loadCb!();

        const coords: [number, number][] = [[1, 2], [3, 4]];
        liveRecMapLibre.updateTrack('self', coords);

        const trackSrc = map().getSource('live-track-self')!;
        expect(trackSrc.setData).toHaveBeenCalledWith(
            expect.objectContaining({ geometry: { type: 'LineString', coordinates: coords } }),
        );
        const posSrc = map().getSource('live-pos-self')!;
        expect(posSrc.setData).toHaveBeenCalledWith(
            expect.objectContaining({ geometry: { type: 'Point', coordinates: [3, 4] } }),
        );
    });

    it('setStyle replays persons and restores camera after style.load', async () => {
        liveRecMapLibre.init('lrmap', STYLE_URL);
        liveRecMapLibre.addPerson('self', '#e74c3c', '#3498db');
        map().loadCb!();

        liveRecMapLibre.setStyle('https://example.test/other.json');
        await flush(); // fetchStyleJson rejects → applyStyle runs on the catch microtask

        expect(map().setStyleArg).toBe('https://example.test/other.json');

        map().calls.length = 0; // isolate the post-style-load replay
        map().styleLoadCb!();

        expect(map().jumpToArg).toEqual({ center: [10, 20], zoom: 14, bearing: 30, pitch: 45 });
        // Person sources + layers re-added after the style wipe.
        const replayed = map().calls.filter(([op]) => op === 'addSource' || op === 'addLayer');
        expect(replayed).toEqual([
            ['addSource', 'live-track-self'],
            ['addLayer', 'live-track-line-self'],
            ['addSource', 'live-pos-self'],
            ['addLayer', 'live-pos-dot-self'],
        ]);
    });

    it('removePerson removes layers before sources', () => {
        liveRecMapLibre.init('lrmap', STYLE_URL);
        liveRecMapLibre.addPerson('self', '#e74c3c', '#3498db');
        map().loadCb!();
        map().calls.length = 0; // isolate the removal sequence

        liveRecMapLibre.removePerson('self');

        expect(map().calls).toEqual([
            ['removeLayer', 'live-track-line-self'],
            ['removeLayer', 'live-pos-dot-self'],
            ['removeSource', 'live-track-self'],
            ['removeSource', 'live-pos-self'],
        ]);
    });

    it('applies keyless 3D terrain on load for a default basemap — terrarium DEM, pitch eased (3D-on-by-default)', () => {
        liveRecMapLibre.init('lrmap', STYLE_URL, null, 'liberty');
        expect(H.addHillshadeAndLight).not.toHaveBeenCalled(); // deferred until the map's load event
        map().loadCb!();
        // Contract change (scope decision 2026-07-22): the old "default basemaps stay 2D" workaround is
        // gone — every basemap now runs the shared engine's terrain path, routed to the keyless terrarium
        // DEM (no MapTiler key on the public share). skipPitch=false ⇒ a fresh open eases into the 3D pitch.
        expect(H.addHillshadeAndLight).toHaveBeenCalledWith(
            expect.anything(), H.terrainExaggeration(), false, 'terrarium',
        );
    });

    it('routes the DEM provider by basemap id — a MapTiler basemap gets the maptiler DEM', () => {
        liveRecMapLibre.init('lrmap', STYLE_URL, null, 'streets'); // registry: dem 'maptiler'
        map().loadCb!();
        expect(H.addHillshadeAndLight).toHaveBeenCalledWith(
            expect.anything(), H.terrainExaggeration(), false, 'maptiler',
        );
    });

    it('bfcache re-init adopts the restored pitch/bearing and does NOT re-tilt (skipPitch)', () => {
        const cam = { center: [10, 20] as [number, number], zoom: 14, pitch: 50, bearing: 12 };
        liveRecMapLibre.init('lrmap', STYLE_URL, cam, 'liberty');
        // Constructor takes the restored camera so Back returns to the exact viewport…
        expect(map().opts.pitch).toBe(50);
        expect(map().opts.bearing).toBe(12);
        map().loadCb!();
        // …and the terrain path leaves it flat (skipPitch=true) instead of easing back to the default pitch.
        expect(H.addHillshadeAndLight).toHaveBeenCalledWith(
            expect.anything(), H.terrainExaggeration(), true, 'terrarium',
        );
    });

    it('setStyle disables terrain before a { diff:false } swap, then re-applies 3D on idle (shared helper)', async () => {
        liveRecMapLibre.init('lrmap', STYLE_URL, null, 'liberty');
        map().loadCb!();
        (H.addHillshadeAndLight as ReturnType<typeof vi.fn>).mockClear();

        liveRecMapLibre.setStyle('https://example.test/other.json', 'bright');
        await flush(); // fetchStyleJson rejects → doSwap runs on the catch microtask

        // Shared swapStyleWithTerrain: terrain off BEFORE setStyle destroys the shader programs, and a
        // full clean rebuild ({ diff:false }) so the diff engine can't strand terrain OR the rider layers.
        const off = map().calls.findIndex(([op, arg]) => op === 'setTerrain' && arg === 'null');
        const swap = map().calls.findIndex(([op]) => op === 'setStyle');
        expect(off).toBeGreaterThanOrEqual(0);
        expect(swap).toBeGreaterThan(off);
        expect(map().setStyleOpts).toEqual({ diff: false });

        // style.load restores camera + riders, but terrain waits for the new tiles to settle…
        map().styleLoadCb!();
        expect(H.addHillshadeAndLight).not.toHaveBeenCalled();

        // …3D re-applies on the first idle after the swap; skipPitch keeps the camera restored by jumpTo.
        map().idleCb!();
        expect(H.addHillshadeAndLight).toHaveBeenLastCalledWith(
            expect.anything(), H.terrainExaggeration(), true, 'terrarium',
        );
    });

    it('destroy keeps the person store so re-init replays', () => {
        liveRecMapLibre.init('lrmap', STYLE_URL);
        liveRecMapLibre.addPerson('self', '#e74c3c', '#3498db');
        map().loadCb!();

        const first = map();
        liveRecMapLibre.destroy();
        expect(first.removed).toBe(true);
        expect(liveRecMapLibre.isReady()).toBe(false);

        // bfcache path: re-init with a fresh map replays the retained person store.
        liveRecMapLibre.init('lrmap', STYLE_URL);
        map().loadCb!();

        const added = map().calls.filter(([op]) => op === 'addSource' || op === 'addLayer');
        expect(added).toEqual([
            ['addSource', 'live-track-self'],
            ['addLayer', 'live-track-line-self'],
            ['addSource', 'live-pos-self'],
            ['addLayer', 'live-pos-dot-self'],
        ]);
    });
});
