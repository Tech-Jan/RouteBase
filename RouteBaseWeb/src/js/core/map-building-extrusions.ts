// core/map-building-extrusions.ts — 3D building extrusion visibility toggle (BT-11).
//
// The buildings toggle is owned by core/map-shell.ts; this module and its control
// (core/map-building-extrusions-control.ts) are its implementation. Moved here from maplibre/ in
// ARCH-F1 so map-shell no longer reaches UP into maplibre/ — the buildings-toggle unit now lives
// together in core/, and core→maplibre value edges are gone (the one-way rule is gated by eslint
// `no-restricted-imports`; see eslint.config.mjs → "MAP-ENGINE LAYERING"). Style-manipulation only;
// depends on no other map module.
//
// Liberty ships `building-3d`; MapTiler styles may ship their own extrusion layers.
// Style-agnostic: iterates the live style, no hardcoded per-style layer list, so it
// also covers future styles and no-ops on extrusion-less ones (Bright, rasters).

type StyleSpec = import('maplibre-gl').StyleSpecification;

/**
 * Show/hide every `fill-extrusion` layer in the map's current style.
 * No-op on styles without extrusion layers, or if the style isn't loaded yet.
 */
export function setBuildingExtrusionsVisible(map: maplibregl.Map, visible: boolean): void {
    let style: StyleSpec | undefined;
    try { style = map.getStyle(); } catch { return; } // style not ready
    if (!style || !Array.isArray(style.layers)) return;

    const vis = visible ? 'visible' : 'none';
    for (const layer of style.layers) {
        if (layer && layer.type === 'fill-extrusion') {
            try { map.setLayoutProperty(layer.id, 'visibility', vis); }
            catch { /* layer removed mid-iteration */ }
        }
    }
}

/** True when the current style ships any `fill-extrusion` layer (i.e. 3D buildings are possible).
 *  Used to hide the buildings toggle on styles that have none (Bright, rasters). */
export function hasBuildingExtrusions(map: maplibregl.Map): boolean {
    let style: StyleSpec | undefined;
    try { style = map.getStyle(); } catch { return false; } // style not ready
    if (!style || !Array.isArray(style.layers)) return false;
    return style.layers.some(layer => layer && layer.type === 'fill-extrusion');
}

// 3D buildings show by default on every device (incl. mobile); the per-map buildings
// control toggles them off. Previously device-gated (desktop on / mobile off) — unwound
// so the default is uniform and behaves like the terrain control.
export const EXTRUSIONS_DEFAULT_VISIBLE = true;
