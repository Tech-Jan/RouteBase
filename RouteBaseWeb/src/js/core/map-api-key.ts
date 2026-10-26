/**
 * core/map-api-key.ts — the page's MapTiler API key, as an explicitly-initialised module seam.
 *
 * Replaces the former `window.MAPTILER_API_KEY` global. The key is a PUBLIC client-side value — it ships in the page HTML by design
 * (see Share/View.cshtml) — but it is PAGE-SCOPED: a page that has a key calls setMapTilerApiKey() once at
 * boot, before any map is created, from the server-rendered value (a JSON island or its page config).
 *
 * The KEYLESS majority (dashboard, races, record, segments index/create, photos, the track editor, and
 * BOTH share views) never calls it and reads the empty-string default. That is a valid state, not an
 * error: core/basemap-registry hides the keyRequired basemaps and falls defaultBasemapId() back to
 * 'liberty' when the key is '', so a keyless page renders the OpenFreeMap liberty basemap — never a blank
 * map. (The seam therefore does NOT throw on an un-initialised read: a hard fail would regress every
 * keyless page, and there is no blank-map failure mode for it to guard against.)
 *
 * Why a seam and not `window`: readers (basemap-registry, maplibre-helpers) now import getMapTilerApiKey()
 * instead of reaching into `window`, so they unit-test with an explicit setMapTilerApiKey(), no global stub
 * — and the client-side key surface is greppable and no longer an ambient window property.
 */

// '' = no key (keyless). Set once per page at boot from the server-rendered value.
let _mapTilerApiKey = '';

/** Set the page's MapTiler API key. Call once at page boot; pass '' (or nullish) for a keyless page. */
export function setMapTilerApiKey(key: string | null | undefined): void {
	_mapTilerApiKey = key ?? '';
}

/** The page's MapTiler API key, or '' when the page is keyless. */
export function getMapTilerApiKey(): string {
	return _mapTilerApiKey;
}

/** Test/teardown helper — reset to the keyless default so a set key cannot leak across cases. */
export function resetMapTilerApiKey(): void {
	_mapTilerApiKey = '';
}
