// @vitest-environment node
import { describe, it, expect } from 'vitest';
import { setBuildingExtrusionsVisible } from './map-building-extrusions';

// BT-11: style-agnostic show/hide of every fill-extrusion layer. Records the
// setLayoutProperty calls a fake map receives; asserts non-extrusion layers are
// untouched and extrusion-less styles produce no calls.

interface Recorded { id: string; prop: string; value: unknown; }

function fakeMap(layers: { id: string; type: string }[]) {
    const calls: Recorded[] = [];
    return {
        calls,
        getStyle: () => ({ layers }),
        setLayoutProperty: (id: string, prop: string, value: unknown) => calls.push({ id, prop, value }),
    };
}

describe('setBuildingExtrusionsVisible (BT-11)', () => {
    it('hides only fill-extrusion layers when visible=false', () => {
        const m = fakeMap([
            { id: 'building-3d', type: 'fill-extrusion' },
            { id: 'roads', type: 'line' },
            { id: 'extrude-2', type: 'fill-extrusion' },
        ]);
        setBuildingExtrusionsVisible(m as unknown as maplibregl.Map, false);
        expect(m.calls).toEqual([
            { id: 'building-3d', prop: 'visibility', value: 'none' },
            { id: 'extrude-2', prop: 'visibility', value: 'none' },
        ]);
    });

    it('shows fill-extrusion layers when visible=true', () => {
        const m = fakeMap([{ id: 'building-3d', type: 'fill-extrusion' }]);
        setBuildingExtrusionsVisible(m as unknown as maplibregl.Map, true);
        expect(m.calls).toEqual([{ id: 'building-3d', prop: 'visibility', value: 'visible' }]);
    });

    it('no-ops on styles without extrusion layers', () => {
        const m = fakeMap([
            { id: 'osm', type: 'raster' },
            { id: 'roads', type: 'line' },
        ]);
        setBuildingExtrusionsVisible(m as unknown as maplibregl.Map, false);
        expect(m.calls).toEqual([]);
    });
});
