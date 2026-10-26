import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import {
    availableBasemaps,
    getBasemap,
    resolveStyleUrl,
    defaultBasemapId,
} from './basemap-registry';
import { createMapShell } from './map-shell';
import { setMapTilerApiKey, resetMapTilerApiKey } from './map-api-key';

// The registry reads the MapTiler key from the core/map-api-key seam at call time; each test controls it.
function setKey(key: string | undefined): void {
    if (key === undefined) resetMapTilerApiKey();
    else setMapTilerApiKey(key);
}

beforeEach(() => setKey(undefined));
afterEach(() => setKey(undefined));

describe('basemap-registry — CM-01', () => {
    it('keyless: availableBasemaps returns only the keyless entries', () => {
        expect(availableBasemaps().map(b => b.id)).toEqual(['liberty', 'bright', 'positron', 'osm-raster', 'satellite-esri']);
        expect(defaultBasemapId('satellite')).toBe('liberty'); // profile default unresolvable → liberty
    });

    it('with key: maptiler entries appear and {key} is substituted', () => {
        setKey('abc');
        expect(availableBasemaps()).toHaveLength(8);
        const url = resolveStyleUrl('streets');
        expect(url).toContain('key=abc');
        expect(url).not.toContain('{key}');
    });

    it('bright resolves keyless (OpenFreeMap, no key needed)', () => {
        expect(resolveStyleUrl('bright')).toBe('https://tiles.openfreemap.org/styles/bright');
    });

    it('satellite-esri is keyless-available and resolves to the local style JSON', () => {
        expect(availableBasemaps().map(b => b.id)).toContain('satellite-esri');
        expect(resolveStyleUrl('satellite-esri')).toBe('/map-styles/esri-satellite.style.json');
    });

    it('terrain resolves to the sentinel only with a key', () => {
        expect(resolveStyleUrl('terrain')).toBeNull();
        setKey('abc');
        expect(resolveStyleUrl('terrain')).toBe('terrain');
    });

    it('unknown id resolves to null', () => {
        expect(getBasemap('nope')).toBeNull();
        expect(resolveStyleUrl('nope')).toBeNull();
    });
});

describe('MapShell styleSwitcher — CM-01 integration', () => {
    let recordedStyleUrl: string | null;

    function mountShell() {
        document.body.innerHTML = '<div id="map"></div><div id="switcher"></div>';
        recordedStyleUrl = null;
        return createMapShell({
            containerId: 'map',
            deferInit: true,
            handleBfcache: false,
            styleSwitcher: { containerId: 'switcher' },
            createMapLibre: (_id, styleUrl) => {
                recordedStyleUrl = styleUrl;
                return { once: () => {} } as unknown as maplibregl.Map;
            },
            onMapLibreReady: () => {},
        });
    }

    afterEach(() => { document.body.innerHTML = ''; });

    it('renders one button per available basemap (keyless → 5)', () => {
        mountShell();
        // Basemap buttons only ([data-basemap-id]) — excludes the BT-11 "3D buildings" toggle.
        expect(document.querySelectorAll('#switcher button[data-basemap-id]')).toHaveLength(5);
    });

    it('clicking a basemap button switches to its resolved URL and moves .active', () => {
        mountShell();
        const positron = document.querySelector('[data-basemap-id="positron"]') as HTMLButtonElement;
        positron.click();

        expect(recordedStyleUrl).toBe('https://tiles.openfreemap.org/styles/positron');
        expect(positron.classList.contains('active')).toBe(true);
        // Only the clicked basemap button is active (the BT-11 toggle carries its own
        // independent .active state, so scope to [data-basemap-id]).
        expect(document.querySelectorAll('#switcher button[data-basemap-id].active')).toHaveLength(1);
    });
});
