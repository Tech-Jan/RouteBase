import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { must } from '../core/invariant';
import { liveRecordingMap } from './live-recording-map';
import { liveRecMapLibre } from './live-recording-map-maplibre';
import { setMapTilerApiKey, resetMapTilerApiKey } from '../core/map-api-key';

// LR-01 facade pins. Drive the module through the same `maplibregl` stub as the
// engine pins. createMapShell is MapLibre-only and jsdom-safe, so switchToBasemap
// resolves via the registry and calls back into the engine.
// MAPTILER_API_KEY is left unset → keyless registry (liberty/positron/osm-raster).

/** MapLibre gesture handler — the follow seam flips these, and re-anchors some on enable. */
class FakeHandler {
    enabled = true;
    around: unknown = undefined;
    enable(options?: { around?: string }): void { this.enabled = true; this.around = options?.around; }
    disable(): void { this.enabled = false; }
}

/** Every field optional: the follow writes centre-only frames as well as the engage-time framing. */
interface FakeCamera { center?: [number, number]; zoom?: number; bearing?: number; pitch?: number }

class FakeMap {
    opts: Record<string, unknown>;
    idleCb: (() => void) | null = null;
    dragCb: (() => void) | null = null;
    panToArgs: Array<[number, number]> = [];
    jumpToArgs: FakeCamera[] = [];

    dragPan = new FakeHandler();
    dragRotate = new FakeHandler();
    scrollZoom = new FakeHandler();
    touchZoomRotate = new FakeHandler();
    touchPitch = new FakeHandler();
    keyboard = new FakeHandler();
    doubleClickZoom = new FakeHandler();
    isMoving(): boolean { return false; }

    private canvas = document.createElement('canvas');

    constructor(opts: Record<string, unknown>) {
        this.opts = opts;
        maps.push(this);
    }
    once(ev: string, cb: () => void): void { if (ev === 'idle') this.idleCb = cb; }
    on(ev: string, cb: () => void): void { if (ev === 'dragstart') this.dragCb = cb; }
    panTo(arg: [number, number]): void { this.panToArgs.push(arg); }
    jumpTo(cam: FakeCamera): void { this.jumpToArgs.push(cam); }
    getCanvas(): HTMLCanvasElement { return this.canvas; }
    getCenter(): [number, number] { return [0, 0]; }
    getZoom(): number { return 12; }
    // Non-zero on purpose: the LV-04 seed pin below would be vacuous against 0/0.
    getBearing(): number { return 33; }
    getPitch(): number { return 44; }
    getSource(): undefined { return undefined; }
    getLayer(): undefined { return undefined; }
    remove(): void {}
}

let maps: FakeMap[] = [];
const map = (): FakeMap => must(maps[maps.length - 1], 'a FakeMap was constructed');
const LIBERTY_URL = 'https://tiles.openfreemap.org/styles/liberty';

/** Init + fire the MapShell idle callback so onMapLibreReady wires the drag break. */
function initAndReady(): void {
    liveRecordingMap.init({ containerId: 'lv-map', styleSwitcherId: 'lv-style-switcher' });
    map().idleCb!();
}

beforeEach(() => {
    maps = [];
    document.body.innerHTML = '<div id="lv-map"></div><div id="lv-style-switcher"></div>';
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    (globalThis as any).maplibregl = { Map: FakeMap };
    resetMapTilerApiKey(); // keyless registry (liberty/positron/osm-raster) unless a test sets a key
});

afterEach(() => {
    // destroy() now empties the person store itself (LV-06), so the test no longer reaches into an
    // underscore-prefixed engine internal to reset module state (LV-08 deleted _clearPersonStore).
    liveRecordingMap.destroy();
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    delete (globalThis as any).maplibregl;
    document.body.innerHTML = '';
});

describe('LiveView map facade — LR-01 pins', () => {
    it('init renders registry switcher buttons keyless and switches to liberty', () => {
        liveRecordingMap.init({ containerId: 'lv-map', styleSwitcherId: 'lv-style-switcher' });

        // Basemap buttons only ([data-basemap-id]) — excludes the BT-11 "3D buildings" toggle
        // MapShell appends to the same container.
        const btns = Array.from(
            document.querySelectorAll<HTMLButtonElement>('#lv-style-switcher button[data-basemap-id]'),
        );
        expect(btns.map(b => b.textContent)).toEqual(['Liberty', 'Bright', 'Muted', 'OSM', 'Satellite']);

        const active = document.querySelector<HTMLButtonElement>('#lv-style-switcher button.active');
        expect(active?.dataset.basemapId).toBe('liberty');

        // Engine created with the resolved liberty style URL.
        expect(map().opts.style).toBe(LIBERTY_URL);
    });

    it('keyed user defaults to liberty, not streets (BT-07 behavior change)', () => {
        setMapTilerApiKey('test-key');
        liveRecordingMap.init({ containerId: 'lv-map', styleSwitcherId: 'lv-style-switcher' });

        const active = document.querySelector<HTMLButtonElement>('#lv-style-switcher button.active');
        expect(active?.dataset.basemapId).toBe('liberty'); // was 'streets' before defaults unification
        resetMapTilerApiKey();
    });

    // Follow owns the CENTRE and nothing else: panning goes away, every gesture handler stays live
    // (re-anchored on the followed rider). Replaces the old drag-breaks-follow / updateTrack-pans
    // contract, and the "all native handlers off" one that came after it.

    it('following takes panning only; the gesture handlers stay live, centre-anchored', () => {
        initAndReady();
        liveRecordingMap.addPerson('a', '#2a78d6', '#2a78d6');

        liveRecordingMap.setFollowTarget('a');
        expect(liveRecordingMap.getFollowTarget()).toBe('a');
        expect(map().dragPan.enabled).toBe(false);
        expect(map().scrollZoom.enabled).toBe(true);
        expect(map().scrollZoom.around).toBe('center');
        expect(map().touchPitch.enabled).toBe(true); // 2-finger tilt over the rider

        liveRecordingMap.setFollowTarget(null);
        expect(liveRecordingMap.getFollowTarget()).toBeNull();
        expect(map().dragPan.enabled).toBe(true);
        // Un-anchored again: a free camera zooms about the pointer, not the centre.
        expect(map().scrollZoom.around).toBeUndefined();
    });

    it('updateTrack no longer pans — the follow camera owns the centre', () => {
        initAndReady();
        liveRecordingMap.addPerson('a', '#2a78d6', '#2a78d6');
        liveRecordingMap.setFollowTarget('a');

        liveRecordingMap.updateTrack('a', [[1, 2], [3, 4]]);

        expect(map().panToArgs).toEqual([]);
    });

    /** The one-off engage-time framing jump (the per-frame ones carry a centre and nothing else). */
    const framingJump = (): FakeCamera => map().jumpToArgs.find((j) => j.bearing !== undefined)!;

    // LV-04: the third copy of the canvas-ownership ritual lived here, and it was the only one that
    // seeded the camera from the map's current view. That seed is the drift the shared seam must keep
    // — now expressed as "an omitted seed field keeps what the map has".
    it('frames the follow from the map current view (bearing/pitch, not 0)', () => {
        initAndReady();
        liveRecordingMap.addPerson('a', '#2a78d6', '#2a78d6');
        liveRecordingMap.updateTrack('a', [[7, 8]]);

        liveRecordingMap.setFollowTarget('a');

        const jump = framingJump();
        expect(jump.bearing).toBe(33);
        expect(jump.pitch).toBe(44);
        expect(jump.zoom).toBe(12); // no explicit zoom → the map's own
    });

    it('follow centres on the target and adopts the requested zoom', () => {
        initAndReady();
        liveRecordingMap.addPerson('a', '#2a78d6', '#2a78d6');
        liveRecordingMap.updateTrack('a', [[7, 8]]);

        liveRecordingMap.setFollowTarget('a', { zoom: 15 });

        // Zoom is named once, at engage; the idle loop then only ever writes the centre.
        expect(framingJump().zoom).toBe(15);
        expect(map().jumpToArgs.at(-1)).toEqual({ center: [7, 8] });
    });

    // LV-06: the engine's person store used to survive destroy() — every rider's full coordinate array
    // stayed resident after the map was gone.
    it('destroy() empties the person store', () => {
        initAndReady();
        liveRecordingMap.addPerson('a', '#2a78d6', '#2a78d6');
        liveRecordingMap.updateTrack('a', [[7, 8]]);
        expect(liveRecMapLibre.getLastPosition('a')).toEqual([7, 8]);

        liveRecordingMap.destroy();

        expect(liveRecMapLibre.getLastPosition('a')).toBeNull();
    });
});
