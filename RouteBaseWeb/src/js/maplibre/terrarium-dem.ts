// maplibre/terrarium-dem.ts — Keyless AWS terrarium DEM source (BT-03).
//
// The project's core seam: a raster-dem source that needs no MapTiler key, so 3D
// terrain works on every keyless basemap. `encoding: 'terrarium'` is native to
// MapLibre (GPU decode, no JS cost). Tile URL lives here and nowhere else.
import type { RasterDEMSourceSpecification } from 'maplibre-gl';

// US bucket only — EU 403s (project.md Feasibility). Virtual-hosted-style (bucket in the host, SVC-11) not
// path-style: it lets the share-page CSP allow exactly `elevation-tiles-prod.s3.amazonaws.com` instead of
// all of `s3.amazonaws.com`. The bucket name has no dots, so it matches the `*.s3.amazonaws.com` TLS cert.
export const TERRARIUM_TILE_URL = 'https://elevation-tiles-prod.s3.amazonaws.com/terrarium/{z}/{x}/{y}.png';

export const TERRARIUM_ATTRIBUTION =
    'Terrain: <a href="https://registry.opendata.aws/terrain-tiles/">Mapzen terrain-tiles</a> (SRTM, EU-DEM, USGS…)';

export const TERRARIUM_MAX_ZOOM = 15;

/** RasterDEM source spec for the keyless terrarium DEM. Source id at add time stays `terrainSource` (invariant 5). */
export function terrariumDemSource(): RasterDEMSourceSpecification {
    return {
        type: 'raster-dem',
        encoding: 'terrarium',
        tiles: [TERRARIUM_TILE_URL],
        tileSize: 256,
        maxzoom: TERRARIUM_MAX_ZOOM,
        attribution: TERRARIUM_ATTRIBUTION,
    };
}
