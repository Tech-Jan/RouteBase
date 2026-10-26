import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { createMapShell } from './map-shell';

// Pins the single-engine MapLibre lifecycle of MapShell (post-D6 collapse — no
// Leaflet half remains). destroy() is the teardown trigger that tears down the
// active MapLibre map and bumps the generation guard.

interface FakeMap {
    id: string;
    once: (ev: string, cb: () => void) => void;
}

beforeEach(() => {
    document.body.innerHTML = '<div id="map"></div>';
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    delete (window as any).MAPTILER_API_KEY;
});
afterEach(() => { document.body.innerHTML = ''; });

describe('MapShell.switchToMapLibre — teardown before create', () => {
    it('destroys the previous MapLibre map before creating the next one', () => {
        const events: string[] = [];
        const shell = createMapShell({
            containerId: 'map',
            deferInit: true,
            handleBfcache: false,
            destroyMapLibre: (m) => events.push('destroy:' + (m as unknown as FakeMap).id),
            createMapLibre: (_id, styleUrl) => {
                events.push('create:' + styleUrl);
                return { id: styleUrl, once: () => {} } as unknown as maplibregl.Map;
            },
            onMapLibreReady: () => {},
        });

        shell.switchToMapLibre('A');
        shell.destroy(); // tears down MapLibre (fires destroyMapLibre)
        shell.switchToMapLibre('B');

        expect(events).toEqual(['create:A', 'destroy:A', 'create:B']);
    });
});

describe('MapShell — stale idle callback is ignored (generation guard)', () => {
    it('does not run onMapLibreReady for an idle callback from a superseded style', () => {
        const holder: { cb: (() => void) | null } = { cb: null };
        const onMapLibreReady = vi.fn();
        const shell = createMapShell({
            containerId: 'map',
            deferInit: true,
            handleBfcache: false,
            createMapLibre: (_id, styleUrl) =>
                ({
                    once: (ev: string, cb: () => void) => { if (ev === 'idle') holder.cb = cb; },
                    id: styleUrl,
                } as unknown as maplibregl.Map),
            onMapLibreReady,
        });

        shell.switchToMapLibre('A'); // captures idle cb at generation 1
        shell.destroy();             // bumps generation to 2, superseding A
        holder.cb?.();               // stale idle fires late

        expect(onMapLibreReady).not.toHaveBeenCalled();
    });
});

describe('MapShell — basemap id propagation (BT-05)', () => {
    it('passes the basemap id to createMapLibre on switchToBasemap', () => {
        const received: (string | null | undefined)[] = [];
        const shell = createMapShell({
            containerId: 'map',
            deferInit: true,
            handleBfcache: false,
            createMapLibre: (_id, _url, _cam, basemapId) => {
                received.push(basemapId);
                return { once: () => {} } as unknown as maplibregl.Map;
            },
            onMapLibreReady: () => {},
        });

        shell.switchToBasemap('liberty'); // keyless — resolves without a MapTiler key

        expect(received).toEqual(['liberty']);
    });

    it('passes the basemap id to switchMapLibreStyle on an in-place switch', () => {
        const received: (string | null | undefined)[] = [];
        const shell = createMapShell({
            containerId: 'map',
            deferInit: true,
            handleBfcache: false,
            createMapLibre: () => ({ once: () => {} } as unknown as maplibregl.Map),
            switchMapLibreStyle: (_m, _url, basemapId) => { received.push(basemapId); },
            onMapLibreReady: () => {},
        });

        shell.switchToBasemap('liberty');  // creates the map → engine becomes maplibre
        shell.switchToBasemap('positron'); // in-place → switchMapLibreStyle

        expect(received).toEqual(['positron']);
    });

    it('direct switchToMapLibre without an id passes null through', () => {
        const received: (string | null | undefined)[] = [];
        const shell = createMapShell({
            containerId: 'map',
            deferInit: true,
            handleBfcache: false,
            createMapLibre: (_id, _url, _cam, basemapId) => {
                received.push(basemapId);
                return { once: () => {} } as unknown as maplibregl.Map;
            },
            onMapLibreReady: () => {},
        });

        shell.switchToMapLibre('some-url');

        expect(received).toEqual([null]);
    });

    it('bfcache restore re-inits with the remembered basemap id', () => {
        vi.useFakeTimers();
        const received: (string | null | undefined)[] = [];
        const shell = createMapShell({
            containerId: 'map',
            deferInit: true, // bfcache handler enabled (handleBfcache defaults true)
            createMapLibre: (_id, _url, _cam, basemapId) => {
                received.push(basemapId);
                return { once: () => {} } as unknown as maplibregl.Map;
            },
            onMapLibreReady: () => {},
        });

        shell.switchToBasemap('liberty'); // engine now maplibre, remembered id = 'liberty'

        const evt = new Event('pageshow') as PageTransitionEvent;
        Object.defineProperty(evt, 'persisted', { value: true });
        window.dispatchEvent(evt);
        vi.runAllTimers(); // flush the setTimeout(0) re-init

        expect(received).toEqual(['liberty', 'liberty']);

        shell.destroy();
        vi.useRealTimers();
    });
});

describe('MapShell — 3D buildings default (BT-11)', () => {
    it('shows fill-extrusion layers on style ready even when matchMedia reports touch', async () => {
        // Buildings now default visible on every device. Mock matchMedia as touch (hover: none) and
        // re-import fresh to prove the old mobile-off gating is gone.
        vi.resetModules();
        const prevMatchMedia = window.matchMedia;
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        (window as any).matchMedia = (q: string) => ({ matches: q.includes('hover: none') });

        const { createMapShell: freshCreateMapShell } = await import('./map-shell');

        const layout: { id: string; vis: unknown }[] = [];
        const holder: { cb: (() => void) | null } = { cb: null };
        const fakeMap = {
            once: (ev: string, cb: () => void) => { if (ev === 'idle') holder.cb = cb; },
            getStyle: () => ({ layers: [
                { id: 'building-3d', type: 'fill-extrusion' },
                { id: 'roads', type: 'line' },
            ] }),
            setLayoutProperty: (id: string, _prop: string, vis: unknown) => layout.push({ id, vis }),
        };

        const shell = freshCreateMapShell({
            containerId: 'map',
            deferInit: true,
            handleBfcache: false,
            createMapLibre: () => fakeMap as unknown as maplibregl.Map,
            onMapLibreReady: () => {},
        });

        shell.switchToMapLibre('A');
        holder.cb?.(); // style ready → applies the default (visible everywhere)

        expect(layout).toEqual([{ id: 'building-3d', vis: 'visible' }]);

        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        (window as any).matchMedia = prevMatchMedia;
        vi.resetModules();
    });
});

describe('MapShell — bfcache camera stash (MS-01)', () => {
    function camFakeMap(): maplibregl.Map {
        return {
            once: () => {},
            getCenter: () => ({ toArray: () => [8, 47] }),
            getZoom: () => 13,
            getPitch: () => 45,
            getBearing: () => 90,
        } as unknown as maplibregl.Map;
    }

    it('bfcache restore passes the pre-destroy camera to createMapLibre', () => {
        vi.useFakeTimers();
        const cams: (unknown)[] = [];
        const shell = createMapShell({
            containerId: 'map',
            deferInit: true,
            createMapLibre: (_id, _url, savedCam) => { cams.push(savedCam); return camFakeMap(); },
            onMapLibreReady: () => {},
        });

        shell.switchToBasemap('liberty'); // first create — no prior camera
        const evt = new Event('pageshow') as PageTransitionEvent;
        Object.defineProperty(evt, 'persisted', { value: true });
        window.dispatchEvent(evt);
        vi.runAllTimers(); // flush the setTimeout(0) re-init

        expect(cams[0]).toBeNull();
        expect(cams[1]).toEqual({ center: [8, 47], zoom: 13, pitch: 45, bearing: 90 });

        shell.destroy();
        vi.useRealTimers();
    });

    it('destroy clears the stashed bfcache camera', () => {
        const cams: (unknown)[] = [];
        const shell = createMapShell({
            containerId: 'map',
            deferInit: true,
            handleBfcache: true,
            createMapLibre: (_id, _url, savedCam) => { cams.push(savedCam); return camFakeMap(); },
            onMapLibreReady: () => {},
        });

        shell.switchToBasemap('liberty');
        const evt = new Event('pageshow') as PageTransitionEvent;
        Object.defineProperty(evt, 'persisted', { value: true });
        window.dispatchEvent(evt); // stashes the camera (setTimeout not flushed)
        shell.destroy();            // must clear the stash
        shell.switchToMapLibre('X');

        expect(cams[cams.length - 1]).toBeNull();
    });
});

describe('MapShell — in-place style.load re-apply + commit-on-success (MS-04/06)', () => {
    function styleLoadFakeMap(rec: { layout: unknown[]; once: Record<string, (() => void)[]> }): maplibregl.Map {
        return {
            once: (ev: string, cb: () => void) => { (rec.once[ev] ||= []).push(cb); },
            getStyle: () => ({ layers: [{ id: 'building-3d', type: 'fill-extrusion' }] }),
            setLayoutProperty: (id: string, prop: string, vis: unknown) => rec.layout.push([id, prop, vis]),
            addControl: () => {},
        } as unknown as maplibregl.Map;
    }

    it('re-applies extrusion state on style.load after an in-place switch', () => {
        const rec = { layout: [] as unknown[], once: {} as Record<string, (() => void)[]> };
        const shell = createMapShell({
            containerId: 'map',
            deferInit: true,
            handleBfcache: false,
            createMapLibre: () => styleLoadFakeMap(rec),
            switchMapLibreStyle: () => {},
            onMapLibreReady: () => {},
        });

        shell.switchToBasemap('liberty');   // create path
        rec.once['idle']?.forEach(cb => cb()); // fire create-path idle → applies default + adds control
        rec.layout.length = 0;               // clear the create-path apply

        shell.switchToBasemap('positron');   // in-place switch → registers a style.load cb
        expect(rec.layout).toEqual([]);      // nothing applied yet (idle can't fire pre-style)

        rec.once['style.load']?.forEach(cb => cb());
        expect(rec.layout).toEqual([['building-3d', 'visibility', 'visible']]);
    });

    it('same-basemap in-place switch is a no-op', () => {
        const rec = { layout: [] as unknown[], once: {} as Record<string, (() => void)[]> };
        const spy = vi.fn();
        const shell = createMapShell({
            containerId: 'map',
            deferInit: true,
            handleBfcache: false,
            createMapLibre: () => styleLoadFakeMap(rec),
            switchMapLibreStyle: spy,
            onMapLibreReady: () => {},
        });

        shell.switchToBasemap('liberty');  // create (spy not called on create path)
        shell.switchToBasemap('positron'); // in-place → spy once
        shell.switchToBasemap('positron'); // same basemap → early return, spy not called again

        expect(spy).toHaveBeenCalledTimes(1);
    });

    it('createMapLibre returning null leaves the style uncommitted', () => {
        const shell = createMapShell({
            containerId: 'map',
            deferInit: true,
            handleBfcache: false,
            createMapLibre: () => null, // create branch fails
            onMapLibreReady: () => {},
        });

        shell.switchToMapLibre('B'); // never applied → must not commit 'B'

        expect(shell.getStyleUrl()).toBeNull();
    });

    it('a thrown in-place switch leaves getStyleUrl unchanged', () => {
        const shell = createMapShell({
            containerId: 'map',
            deferInit: true,
            handleBfcache: false,
            createMapLibre: () => ({ once: () => {} } as unknown as maplibregl.Map),
            switchMapLibreStyle: () => { throw new Error('boom'); },
            onMapLibreReady: () => {},
        });

        shell.switchToBasemap('liberty');   // create path commits the resolved url
        const committed = shell.getStyleUrl();
        shell.switchToBasemap('positron');  // in-place throws → must not commit the new style

        expect(shell.getStyleUrl()).toBe(committed);
    });
});

describe('MapShell.destroy — bfcache listener cleanup', () => {
    it('removes the pageshow listener it registered', () => {
        const addSpy = vi.spyOn(window, 'addEventListener');
        const removeSpy = vi.spyOn(window, 'removeEventListener');

        const shell = createMapShell({
            containerId: 'map',
            deferInit: true,
            createMapLibre: () => ({ once: () => {} } as unknown as maplibregl.Map),
            onMapLibreReady: () => {},
        });

        const pageshowAdd = addSpy.mock.calls.find(c => c[0] === 'pageshow');
        expect(pageshowAdd).toBeDefined();
        const listener = pageshowAdd![1];

        shell.destroy();

        expect(removeSpy).toHaveBeenCalledWith('pageshow', listener);
        addSpy.mockRestore();
        removeSpy.mockRestore();
    });
});
