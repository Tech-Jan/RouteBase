// missing-image-fallback.ts — silence MapLibre "Image 'X' could not be loaded" console warnings.
//
// Vector basemap styles (OpenMapTiles / Liberty, etc.) reference POI sprite icons by name (recycling,
// office, bollard, sports_centre, …). When the active sprite doesn't include one, MapLibre fires
// `styleimagemissing` and logs a warning per icon per style load. We register a 1×1 transparent
// placeholder on demand: the layer renders (icon simply absent) and the console stays clean. Harmless —
// these are basemap POI markers we don't depend on. `maplibregl` is a page global (CDN).

/** Register a transparent placeholder for any sprite image the style asks for but the sprite lacks. */
export function installMissingImageFallback(map: maplibregl.Map): void {
	map.on("styleimagemissing", (e: { id: string }) => {
		if (!e.id || map.hasImage(e.id)) return;
		map.addImage(e.id, { width: 1, height: 1, data: new Uint8Array(4) }); // 1×1 fully transparent
	});
}
