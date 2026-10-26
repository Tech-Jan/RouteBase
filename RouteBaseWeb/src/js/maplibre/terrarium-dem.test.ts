// @vitest-environment node
import { describe, it, expect } from 'vitest';
import {
    TERRARIUM_TILE_URL,
    TERRARIUM_ATTRIBUTION,
    TERRARIUM_MAX_ZOOM,
    terrariumDemSource,
} from './terrarium-dem';
import { demProviderFor } from '../core/basemap-registry';

describe('terrarium-dem — BT-03 source spec', () => {
    it('terrariumDemSource has the terrarium raster-dem shape', () => {
        const src = terrariumDemSource();
        expect(src.type).toBe('raster-dem');
        expect(src.encoding).toBe('terrarium');
        expect(src.tiles).toEqual([TERRARIUM_TILE_URL]);
        expect(src.tileSize).toBe(256);
        expect(src.maxzoom).toBe(TERRARIUM_MAX_ZOOM);
        expect(TERRARIUM_MAX_ZOOM).toBe(15);
        expect(src.attribution).toBe(TERRARIUM_ATTRIBUTION);
        expect(src.attribution!.length).toBeGreaterThan(0);
    });
});

describe('demProviderFor — BT-03 routing', () => {
    it('keyless OpenFreeMap entry routes to terrarium', () => {
        expect(demProviderFor('bright')).toBe('terrarium');
    });

    it('MapTiler entries route to maptiler', () => {
        expect(demProviderFor('streets')).toBe('maptiler');
        expect(demProviderFor('satellite')).toBe('maptiler');
        expect(demProviderFor('terrain')).toBe('maptiler');
    });

    it('unknown id defaults to terrarium', () => {
        expect(demProviderFor('nope')).toBe('terrarium');
    });
});
