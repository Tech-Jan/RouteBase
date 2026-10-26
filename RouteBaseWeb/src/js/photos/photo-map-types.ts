// photo-map-types.ts — shared shape for photos drawn on a MapLibre map (Details + Activities overview).
// The Details page maps its richer PhotoDto down to this; the overview fetches it directly from
// /api/photos/map-markers (GET → PhotoMarkersResponse.items).

import type { PhotoMarkerDto } from "../generated/contracts";

/** One placed photo for a map overlay — generated from C# PhotoMarkerDto (Models/Photos/PhotoContracts.cs).
 *  Coordinates are the photo's single stored lat/lng (resolved at import/placement — no client-side
 *  interpolation). locationSource: 'exif' | 'interpolated' | 'manual' | 'none' — carried through, but
 *  every photo draws the same. Image bytes: /api/photos/{photoId}/image?tier=…&v={v}. */
export type PhotoMarker = PhotoMarkerDto;
