import type { OverviewTrack } from './overview-types';
import { must } from '../core/invariant';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { helpers as H } from '../maplibre/maplibre-helpers';
import * as ml from './activities-overview-maplibre';

// OV-07 characterization pins for the MapLibre overview engine. The engine survives
// the OV-01/02 refactor verbatim, so these lock its source/layer build, highlight
// paint expression, hover, popup, in-place style switch and destroy behavior first.
// Pattern mirrors live-recording-map-maplibre.test.ts: stub `maplibregl` per test and
// swap the mutable `helpers` export so fetchStyleJson forces the deterministic catch →
// direct-URL setStyle path, and hillshade/background are inert.

const flush = (): Promise<void> => new Promise((r) => setTimeout(r, 0));

interface FakeSource {
    setData: ReturnType<typeof vi.fn>;
}

type LayerHandler = (ev: unknown) => void;

class FakeMap {
    opts: Record<string, unknown>;
    calls: Array<[string, string]> = [];                 // add/removeSource + add/removeLayer, in order
    paint: Array<[string, string, unknown]> = [];        // setPaintProperty(layer, prop, value)
    loadCb: (() => void) | null = null;
    styleLoadCbs: Array<() => void> = [];              // MC-05: collect all once('style.load') callbacks
    layerHandlers: Record<string, LayerHandler> = {};    // keyed by event type (mouseenter/mouseleave/click)
    viewHandlers: Record<string, () => void> = {};       // zoomend/moveend
    offLayerCalls: Array<{ type: string; layer: string }> = [];
    offViewCalls: string[] = [];
    setStyleArg: unknown = null;
    styleLoaded = true;                                  // MC-02: default true → existing tests untouched
    sourceSpecs: Record<string, unknown> = {};           // MC-02: capture addSource spec for assertions
    jumpToArg: unknown = null;
    fitBoundsArgs: unknown[] = [];
    setTerrainCalls = 0;
    removed = false;
    private _sources = new Map<string, FakeSource>();
    private _layers = new Set<string>();

    constructor(opts: Record<string, unknown>) {
        this.opts = opts;
        maps.push(this);
    }

    on(ev: string, layerOrCb: string | (() => void), cb?: LayerHandler): void {
        if (typeof layerOrCb === 'function') {
            if (ev === 'load') this.loadCb = layerOrCb;
            else if (ev === 'zoomend' || ev === 'moveend') this.viewHandlers[ev] = layerOrCb;
        } else if (cb) {
            this.layerHandlers[ev] = cb; // mouseenter/mouseleave/click
        }
    }
    off(ev: string, layerOrCb: string | (() => void), _cb?: LayerHandler): void {
        if (typeof layerOrCb === 'string') this.offLayerCalls.push({ type: ev, layer: layerOrCb });
        else this.offViewCalls.push(ev);
    }
    idleCbs: Array<() => void> = [];                     // once('idle') callbacks (stranded-stash flush)
    repaints = 0;
    once(ev: string, cb: () => void): void {
        if (ev === 'style.load') this.styleLoadCbs.push(cb);
        else if (ev === 'idle') this.idleCbs.push(cb);
    }
    triggerRepaint(): void { this.repaints++; }
    /** Run and clear every collected idle callback (mirrors one real idle event). */
    fireIdle(): void {
        const cbs = this.idleCbs;
        this.idleCbs = [];
        cbs.forEach((cb) => cb());
    }
    /** Run and clear every collected style.load callback (mirrors a single real style.load event). */
    fireStyleLoad(): void {
        const cbs = this.styleLoadCbs;
        this.styleLoadCbs = [];
        cbs.forEach((cb) => cb());
    }
    addControl(): void {}
    addSource(id: string, _spec: unknown): void {
        this.calls.push(['addSource', id]);
        this.sourceSpecs[id] = _spec;
        this._sources.set(id, { setData: vi.fn() });
    }
    isStyleLoaded(): boolean { return this.styleLoaded; }
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
    getSource(id: string): FakeSource | undefined { return this._sources.get(id); }
    getLayer(id: string): { id: string } | undefined { return this._layers.has(id) ? { id } : undefined; }
    setPaintProperty(layer: string, prop: string, value: unknown): void { this.paint.push([layer, prop, value]); }
    setStyle(style: unknown): void {
        this.setStyleArg = style;
        this._sources.clear(); // real MapLibre wipes the style; drop tracked state so a replay is observable
        this._layers.clear();
    }
    setTerrain(): void { this.setTerrainCalls++; }
    fitBounds(...args: unknown[]): void { this.fitBoundsArgs.push(args); }
    jumpTo(cam: unknown): void { this.jumpToArg = cam; }
    getCanvas(): { style: Record<string, string> } { return { style: {} }; }
    getCenter(): { toArray: () => [number, number] } { return { toArray: () => [10, 20] }; }
    getZoom(): number { return 14; }
    getPitch(): number { return 45; }
    getBearing(): number { return 30; }
    remove(): void { this.removed = true; }
}

class FakePopup {
    html: string | null = null;
    setLngLat(): this { return this; }
    setHTML(html: string): this { this.html = html; popups.push(this); return this; }
    addTo(): this { return this; }
}

let maps: FakeMap[] = [];
let popups: FakePopup[] = [];
const map = (): FakeMap => must(maps[maps.length - 1], 'a FakeMap was constructed');
const STYLE_URL = 'https://example.test/style.json';
const PALETTE = ['#dc3545', '#007bff', '#28a745'];
const BOUNDS = { minLat: 1, maxLat: 3, minLng: 10, maxLng: 12 };

function track(id: number): OverviewTrack {
    return {
        id, name: 'Run ' + id, date: '2026-01-0' + id, type: 'Run', distance: id, ascent: id * 10,
        points: [[1 + id, 10 + id], [2 + id, 11 + id]],
    } as OverviewTrack;
}

beforeEach(() => {
    maps = [];
    popups = [];
    document.body.innerHTML = '<div id="activitiesMap"></div>';
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    (globalThis as any).maplibregl = { Map: FakeMap, Popup: FakePopup, NavigationControl: class {} };
    H.addHillshadeAndLight = vi.fn();
    H.applyMapBackground = vi.fn();
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    H.fetchStyleJson = vi.fn().mockRejectedValue(new Error('offline')) as any;
});

afterEach(() => {
    ml.destroy();
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    delete (globalThis as any).maplibregl;
    document.body.innerHTML = '';
});

describe('activities overview MapLibre engine — OV-07 pins', () => {
    it('init builds one tracks source and layer on load', () => {
        ml.init([track(1), track(2)], STYLE_URL, null, PALETTE, BOUNDS);
        map().loadCb!();

        const added = map().calls.filter(([op]) => op === 'addSource' || op === 'addLayer');
        expect(added).toEqual([['addSource', 'act-tracks'], ['addLayer', 'act-tracks-line']]);

        const opacity = map().paint.find(([l, p]) => l === 'act-tracks-line' && p === 'line-opacity');
        expect(opacity?.[2]).toBe(0.7); // base opacity, no highlight
    });

    it('init frames initial bounds only without savedCam', () => {
        ml.init([track(1)], STYLE_URL, null, PALETTE, BOUNDS);
        map().loadCb!();
        expect(map().fitBoundsArgs[0]).toEqual([
            [[BOUNDS.minLng, BOUNDS.minLat], [BOUNDS.maxLng, BOUNDS.maxLat]],
            { padding: 50, duration: 0 },
        ]);

        ml.destroy();
        const cam = { center: [10, 20], zoom: 12, pitch: 0, bearing: 0 };
        ml.init([track(1)], STYLE_URL, cam, PALETTE, BOUNDS);
        map().loadCb!();
        expect(map().fitBoundsArgs).toHaveLength(0);
    });

    it('refresh sets source data and empty set clears', () => {
        ml.init([track(1)], STYLE_URL, null, PALETTE, BOUNDS);
        map().loadCb!();
        const src = map().getSource('act-tracks')!;

        ml.refresh([]);
        expect(src.setData).toHaveBeenCalledWith(expect.objectContaining({ features: [] }));
    });

    it('refresh before style load defers tracks to the load handler', () => {
        ml.init([], STYLE_URL, null, PALETTE, BOUNDS);
        map().styleLoaded = false; // style not yet loaded → addSource would throw
        ml.refresh([track(1)]);
        expect(map().calls).not.toContainEqual(['addSource', 'act-tracks']);

        map().styleLoaded = true;
        map().loadCb!();
        expect(map().calls).toContainEqual(['addSource', 'act-tracks']);
        const spec = map().sourceSpecs['act-tracks'] as { data: GeoJSON.FeatureCollection };
        expect(spec.data.features).toHaveLength(1); // deferred track won over the empty init array
    });

    it('buildTrackFeatures maps [lat,lng] pairs to GeoJSON [lng,lat]', () => {
        ml.init([track(1)], STYLE_URL, null, PALETTE, BOUNDS);
        map().loadCb!();
        const src = map().getSource('act-tracks')!;

        ml.refresh([track(1)]); // points [[2,11],[3,12]] as [lat,lng]
        const fc = src.setData.mock.calls.at(-1)![0] as GeoJSON.FeatureCollection;
        const coords = (must(fc.features[0], 'one feature').geometry as GeoJSON.LineString).coordinates;
        expect(coords).toEqual([[11, 2], [12, 3]]); // [lng,lat]
    });

    it('keeps equator/prime-meridian points but drops null island', () => {
        ml.init([track(1)], STYLE_URL, null, PALETTE, BOUNDS);
        map().loadCb!();
        const src = map().getSource('act-tracks')!;

        ml.refresh([{ ...track(1), points: [[0, 11], [2, 0], [0, 0]] }]);
        const fc = src.setData.mock.calls.at(-1)![0] as GeoJSON.FeatureCollection;
        const coords = (must(fc.features[0], 'one feature').geometry as GeoJSON.LineString).coordinates;
        expect(coords).toEqual([[11, 0], [0, 2]]); // (0,0) dropped; single-axis zeros kept, as [lng,lat]
    });

    it('setTrackHighlight: ids dim rest, [] dims all, null restores base', () => {
        ml.init([track(1), track(2)], STYLE_URL, null, PALETTE, BOUNDS);
        map().loadCb!();

        const lastOpacity = (): unknown =>
            [...map().paint].reverse().find(([l, p]) => l === 'act-tracks-line' && p === 'line-opacity')?.[2];

        ml.setTrackHighlight([1, 2]);
        expect(lastOpacity()).toEqual(
            ['case', ['in', ['get', 'id'], ['literal', [1, 2]]], 0.95, 0.22]);

        ml.setTrackHighlight([]);
        expect(lastOpacity()).toEqual(
            ['case', ['in', ['get', 'id'], ['literal', []]], 0.95, 0.22]);

        ml.setTrackHighlight(null);
        expect(lastOpacity()).toBe(0.7);
    });

    it('hover enter/leave adjust width and opacity, restore highlight-aware base', () => {
        ml.init([track(1), track(2)], STYLE_URL, null, PALETTE, BOUNDS);
        map().loadCb!();

        must(map().layerHandlers['mouseenter'], 'mouseenter handler wired')({ features: [{ properties: { _index: 1 } }] });
        const width = [...map().paint].reverse().find(([l, p]) => l === 'act-tracks-line' && p === 'line-width');
        expect(width?.[2]).toEqual(['case', ['==', ['get', '_index'], 1], 5, 3]);

        map().paint.length = 0;
        must(map().layerHandlers['mouseleave'], 'mouseleave handler wired')({});
        expect(map().paint).toContainEqual(['act-tracks-line', 'line-width', 3]);
        expect(map().paint).toContainEqual(['act-tracks-line', 'line-opacity', 0.7]);
    });

    it('click opens popup with details link', () => {
        ml.init([track(1)], STYLE_URL, null, PALETTE, BOUNDS);
        map().loadCb!();

        must(map().layerHandlers['click'], 'click handler wired')({
            lngLat: [10, 1],
            features: [{ properties: { id: 42, name: 'Run', date: '2026', type: 'Run', distance: 5, ascent: 100 } }],
        });
        expect(must(popups[0], 'one popup').html).toContain('/Activities/Details/42');
        expect(must(popups[0], 'one popup').html).toContain('<span class="chip">'); // OV-05: adopts the surviving default look
        expect(must(popups[0], 'one popup').html).toContain('btn-cockpit');
    });

    it('click popup escapes hostile name/date/type and number-coerces id', () => {
        ml.init([track(1)], STYLE_URL, null, PALETTE, BOUNDS);
        map().loadCb!();

        must(map().layerHandlers['click'], 'click handler wired')({
            lngLat: [10, 1],
            features: [{ properties: {
                id: '42', name: '<img src=x onerror=alert(1)>', date: '<b>d</b>',
                type: '"><script>x</script>', distance: 5, ascent: 100,
            } }],
        });
        const html = must(popups[0], 'one popup').html!;
        expect(html).toContain('&lt;img');
        expect(html).toContain('/Activities/Details/42');
        expect(html).not.toContain('<img');
        expect(html).not.toContain('<script>');
    });

    it('switchStyle re-adds tracks and restores camera after style.load', async () => {
        ml.init([track(1)], STYLE_URL, null, PALETTE, BOUNDS);
        map().loadCb!();

        ml.switchStyle('https://example.test/other.json', [track(1)]);
        await flush(); // fetchStyleJson rejects → setStyle runs on the catch microtask
        expect(map().setStyleArg).toBe('https://example.test/other.json');

        map().calls.length = 0; // isolate the post-style-load replay
        map().fireStyleLoad();

        expect(map().jumpToArg).toEqual({ center: [10, 20], zoom: 14, pitch: 45, bearing: 30 });
        const replayed = map().calls.filter(([op]) => op === 'addSource' || op === 'addLayer');
        expect(replayed).toEqual([['addSource', 'act-tracks'], ['addLayer', 'act-tracks-line']]);
    });

    it('rapid double switchStyle applies only the latest style and adds tracks once', async () => {
        ml.init([track(1)], STYLE_URL, null, PALETTE, BOUNDS);
        map().loadCb!();

        const URL_A = 'https://example.test/a.json';
        const URL_B = 'https://example.test/b.json';
        ml.switchStyle(URL_A, [track(1)]);
        ml.switchStyle(URL_B, [track(1)]);
        await flush(); // both fetches reject → both take the direct setStyle path

        map().calls.length = 0; // isolate the post-style-load replay
        map().fireStyleLoad(); // both once-callbacks fire on the same event; only the latest runs

        const added = map().calls.filter(([op, id]) => op === 'addSource' && id === 'act-tracks');
        expect(added).toHaveLength(1);
        expect(map().setStyleArg).toBe(URL_B);
    });

    it('routes the DEM provider by basemap id on style load (BT-06.3)', async () => {
        ml.init([track(1)], STYLE_URL, null, PALETTE, BOUNDS, 'liberty');
        map().loadCb!();
        expect(H.addHillshadeAndLight).toHaveBeenCalledWith(
            expect.anything(), 1.0, true, 'terrarium',
        );

        (H.addHillshadeAndLight as ReturnType<typeof vi.fn>).mockClear();
        ml.switchStyle('https://example.test/streets.json', [track(1)], 'streets');
        await flush(); // fetchStyleJson rejects → setStyle + once('style.load') on the catch microtask
        map().fireStyleLoad();
        expect(H.addHillshadeAndLight).toHaveBeenCalledWith(
            expect.anything(), 1.0, true, 'maptiler',
        );
    });

    it('destroy removes layer handlers, view listeners and the map', () => {
        ml.init([track(1)], STYLE_URL, null, PALETTE, BOUNDS);
        map().loadCb!();
        ml.attachViewListeners(() => {});
        const first = map();

        ml.destroy();

        expect(first.offLayerCalls.map(c => c.type).sort()).toEqual(['click', 'mouseenter', 'mouseleave']);
        expect(first.offViewCalls.sort()).toEqual(['moveend', 'zoomend']);
        expect(first.removed).toBe(true);
        expect(ml.getMap()).toBeNull();
    });
});

// Stranded-deferral regression (2026-07-11 field bug): tracks arrived while the post-load
// DEM/hillshade work made isStyleLoaded() report false — the stash waited for the one-shot
// `load` event that had already fired, and stayed invisible until a manual style switch.
describe('stranded deferral — tracks paint without a style switch', () => {
    it('paints immediately when refresh lands post-load while isStyleLoaded() is false (the field bug)', () => {
        ml.init([], STYLE_URL, null, PALETTE, BOUNDS);
        map().loadCb!();           // load fired with no data → no source created
        map().styleLoaded = false; // DEM/hillshade tiles in flight reopen the false window

        ml.refresh([track(1)]);    // old code: stashed forever; new code: style is ready → add

        expect(map().calls.filter(([op]) => op === 'addSource')).toEqual([['addSource', 'act-tracks']]);
    });

    it('a pre-load stash arms an idle flush and never double-adds', () => {
        ml.init([], STYLE_URL, null, PALETTE, BOUNDS);
        map().styleLoaded = false;
        ml.refresh([track(1)]);                    // pre-load → stash + arm (repaint forces an idle)
        expect(map().repaints).toBeGreaterThan(0);
        expect(map().calls.filter(([op]) => op === 'addSource')).toHaveLength(0);

        map().loadCb!();                           // load handler paints the stash (MC-02 path)
        map().fireIdle();                          // idle flush then finds nothing left to do

        expect(map().calls.filter(([op]) => op === 'addSource')).toEqual([['addSource', 'act-tracks']]);
    });

    it('idle flush re-arms while the style is still not ready, then the load handler paints', () => {
        ml.init([], STYLE_URL, null, PALETTE, BOUNDS);
        map().styleLoaded = false;
        ml.refresh([track(1)]);
        map().fireIdle();                          // style still not ready → re-stash + re-arm
        expect(map().idleCbs.length).toBeGreaterThan(0);
        expect(map().calls.filter(([op]) => op === 'addSource')).toHaveLength(0);

        map().loadCb!();
        expect(map().calls.filter(([op]) => op === 'addSource')).toEqual([['addSource', 'act-tracks']]);
    });

    it('assertTracksPainted is silent when everything painted', () => {
        ml.init([track(1)], STYLE_URL, null, PALETTE, BOUNDS);
        map().loadCb!();
        ml.assertTracksPainted();
        map().fireIdle();
        expect(document.querySelector('.map-error-banner')).toBeNull();
    });

    it('assertTracksPainted does not banner while the style is legitimately still loading', () => {
        ml.init([], STYLE_URL, null, PALETTE, BOUNDS);
        map().styleLoaded = false;
        ml.refresh([track(1)]);                    // stash pre-load
        ml.assertTracksPainted();
        map().fireIdle();                          // flush re-arms; assert sees not-ready → no banner

        expect(document.querySelector('.map-error-banner')).toBeNull();
        map().loadCb!();                           // load handler still owns the paint
        expect(map().calls.filter(([op]) => op === 'addSource')).toEqual([['addSource', 'act-tracks']]);
    });
});
