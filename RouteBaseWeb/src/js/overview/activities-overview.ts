// Activities Overview - Multi-track Map Visualization
// Orchestrator: MapLibre track rendering, UI controls, public API.
// Imports from activities-overview-progressive and activities-overview-maplibre.
// Exports startActivitiesMap / setActivitiesFilter / setOverviewHighlight /
// getActivitiesMapShell, imported by the overview page bundles (page-init boots the
// map; grid + shape-filter drive it). Shared Rollup chunk → one instance per page.

import type { OverviewTrack } from './overview-types';
import type { QualityTier, MapShellInstance } from '../core/core-types';
import * as prog from './activities-overview-progressive';
import * as mlModule from './activities-overview-maplibre';
import { createMapShell } from '../core/map-shell';
import { defaultBasemapId } from '../core/basemap-registry';
import { LatestRun } from '../core/latest-run';
import { loadActivitiesChunked, fetchActivityViewBounds, isUsableLatLng, type ViewBounds } from './activities-gps-loader';

let shell: MapShellInstance | null = null;
let cachedTracksData: OverviewTrack[] | null = null;
// GPS cache keyed by activity id, holding the best tier fetched so far. Filtering redraws the
// visible subset from this cache (segment-stats pattern) instead of re-fetching + rebuilding the
// shell, so the map updates instantly. Seeded by every fetch (initial load, delta, zoom upgrade).
// LRU-capped so a long session browsing many filters can't grow browser memory unboundedly:
// re-set on every touch keeps Map insertion order ≈ recency, evict oldest past the cap.
const GPS_CACHE_MAX_TRACKS = 4000;
const gpsCacheById = new Map<number, OverviewTrack>();

function cacheTracks(tracks: OverviewTrack[]): void {
	tracks.forEach((t) => {
		if (!t || !t.points || t.points.length === 0) return;
		gpsCacheById.delete(t.id); // re-insert → moves to the back (most recent)
		gpsCacheById.set(t.id, t);
	});
	while (gpsCacheById.size > GPS_CACHE_MAX_TRACKS) {
		const oldest = gpsCacheById.keys().next().value as number | undefined;
		if (oldest === undefined) break;
		gpsCacheById.delete(oldest);
	}
}

// Aborts the in-flight chunked load when a newer load/reload/filter supersedes it, so a stale
// fetch never paints onto or re-inits a map that a newer operation already owns.
const loadRun = new LatestRun();

// The optional photo overlay (overview/activities-photos-overlay.ts) subscribes here to learn when the
// visible track SET changes, so it can re-filter its markers to what's currently drawn. Fired on chunk
// append and on filter changes — NOT on quality upgrades (same set, just denser geometry). One-directional:
// this module never imports the overlay, so there is no cycle.
const _visibleTracksSubs: Array<() => void> = [];
function _notifyVisibleTracks(): void {
	for (const cb of _visibleTracksSubs) {
		try { cb(); } catch (e: unknown) { console.warn('[overview] visible-tracks subscriber failed:', e); }
	}
}
/** Subscribe to visible-track-set changes (photo overlay). */
export function onVisibleTracksChanged(cb: () => void): void { _visibleTracksSubs.push(cb); }
/** Ids of the tracks currently drawn on the map (the photo overlay filters its markers to these). */
export function getVisibleTrackIds(): number[] { return (cachedTracksData ?? []).map((t) => t.id); }

// Viewport computed up front (from activity start/end points) so the camera is framed once at
// map creation and never jumps when tracks finish loading. Null when unavailable (then we fall
// back to fitting the first loaded chunk — see _pendingFallbackFit).
let _initialViewBounds: ViewBounds | null = null;
let _pendingFallbackFit = false;

// MapLibre has no per-feature append for a GeoJSON source, so progressive paint re-sets the whole
// (accumulating) collection — throttled, so K chunks cause only a few setData calls, not K.
let _mlRefreshTimer: ReturnType<typeof setTimeout> | null = null;
const ML_REFRESH_THROTTLE_MS = 400;

const colorPalette = [
	"#dc3545",
	"#007bff",
	"#28a745",
	"#ffc107",
	"#17a2b8",
	"#6f42c1",
	"#fd7e14",
	"#e83e8c",
	"#20c997",
	"#6610f2",
];

// ================================================================
// PROGRESSIVE QUALITY — delegates to prog module
// ================================================================

function onViewChanged(): void {
	prog.onViewChanged();
}

function getCurrentZoom(): number {
	try {
		const ml = shell?.getMapLibreMap();
		if (ml) return ml.getZoom();
	} catch (e: unknown) { console.warn('[overview] getCurrentZoom failed:', e); }
	return 0;
}

function getCurrentBounds(): {
	south: number;
	west: number;
	north: number;
	east: number;
} | null {
	try {
		const ml = shell?.getMapLibreMap();
		if (ml) {
			const b = ml.getBounds();
			return { south: b.getSouth(), west: b.getWest(), north: b.getNorth(), east: b.getEast() };
		}
	} catch (e: unknown) { console.warn('[overview] getCurrentBounds failed:', e); }
	return null;
}

/** Redraw the current `cachedTracksData` on the MapLibre source, in place — no shell teardown. */
function redrawCurrentTracks(): void {
	if (!cachedTracksData) return;
	if (shell?.getMapLibreMap()) mlModule.refresh(cachedTracksData);
}

function handleTracksUpgraded(validTracks: OverviewTrack[], tier: QualityTier): void {
	cacheTracks(validTracks); // keep the cache at the best tier fetched so far
	if (cachedTracksData) {
		cachedTracksData = prog.mergeUpgradedTracks(cachedTracksData, validTracks, tier);
	}
	// A quality upgrade only changes the point density of a handful of already-drawn tracks; the
	// track *set* is unchanged. MapLibre re-sets the whole GeoJSON source (setData is cheap) so the
	// upgraded geometry shows without any per-track teardown.
	if (shell?.getMapLibreMap()) mlModule.refresh(cachedTracksData ?? []);
}

/**
 * Dim every track except the given matching ids; pass null to clear. Updates the MapLibre
 * layer's one line-opacity paint expression — no re-render, cheap with hundreds of tracks.
 * The engine preserves the null-vs-`[]` distinction (`[]` = active, nothing matches → dim all).
 */
function setTrackHighlight(ids: number[] | null): void {
	mlModule.setTrackHighlight(ids);
}

// ================================================================
// BOUNDS HELPER
// ================================================================

function _computeBounds(
	tracksData: OverviewTrack[],
): { minLat: number; maxLat: number; minLng: number; maxLng: number } | null {
	let minLat = Infinity, maxLat = -Infinity, minLng = Infinity, maxLng = -Infinity;
	tracksData.forEach((track: OverviewTrack) => {
		track.points.forEach((p) => {
			if (!isUsableLatLng(p)) return;
			const lat = p[0], lng = p[1];
			if (lat < minLat) minLat = lat;
			if (lat > maxLat) maxLat = lat;
			if (lng < minLng) minLng = lng;
			if (lng > maxLng) maxLng = lng;
		});
	});
	return minLat === Infinity ? null : { minLat, maxLat, minLng, maxLng };
}

/** Fit the MapLibre map to a bounding box. Used for the rare fallback when the cheap bounds
 *  query was unavailable (the up-front camera otherwise frames the map on init). */
function fitActiveEngine(b: ViewBounds): void {
	const ml = shell?.getMapLibreMap();
	if (ml) {
		try {
			ml.fitBounds([[b.minLng, b.minLat], [b.maxLng, b.maxLat]], { padding: 50, duration: 0 });
		} catch (e: unknown) { console.warn('[overview] fitActiveEngine failed:', e); }
	}
}

// ================================================================
// PROGRESSIVE PAINT — append each loaded chunk without a full redraw
// ================================================================

/** Throttled full re-set of the MapLibre source (it has no per-feature append). */
function scheduleMaplibreRefresh(): void {
	if (_mlRefreshTimer != null) return;
	_mlRefreshTimer = setTimeout(() => {
		_mlRefreshTimer = null;
		if (cachedTracksData && shell?.getMapLibreMap()) mlModule.refresh(cachedTracksData);
	}, ML_REFRESH_THROTTLE_MS);
}

/** Flush any pending throttled MapLibre refresh immediately (call when the load completes). */
function flushMaplibreRefresh(): void {
	if (_mlRefreshTimer != null) { clearTimeout(_mlRefreshTimer); _mlRefreshTimer = null; }
	if (cachedTracksData && shell?.getMapLibreMap()) {
		mlModule.refresh(cachedTracksData);
		// Load is complete: data on hand must end up painted — repaint a stranded deferral,
		// banner if even that fails (the "tracks invisible until style switch" bug class).
		if (cachedTracksData.length > 0) mlModule.assertTracksPainted();
	}
}

/** Accumulate a freshly-loaded chunk into the cache and paint it onto the active engine. */
function appendChunkToMap(newTracks: OverviewTrack[]): void {
	if (newTracks.length === 0) return;
	cachedTracksData = (cachedTracksData ?? []).concat(newTracks);
	cacheTracks(newTracks);

	scheduleMaplibreRefresh();
	_notifyVisibleTracks(); // the track set grew — let the photo overlay re-filter its markers

	// Cheap-bounds query was unavailable → frame the camera off the first chunk we paint, once.
	if (_pendingFallbackFit) {
		const b = _computeBounds(cachedTracksData);
		if (b) { fitActiveEngine(b); _pendingFallbackFit = false; }
	}
}

// ================================================================
// MAP SHELL FACTORY
// ================================================================

function _createShell(deferInit?: boolean): MapShellInstance {
	return createMapShell({
		containerId: 'activitiesMap',
		styleSwitcher: { containerId: 'actMapStyleSwitcher', btnClass: 'act-dd-item' },
		sizeControls: {
			growBtnId: 'actMapGrowBtn',
			shrinkBtnId: 'actMapShrinkBtn',
			resetBtnId: 'actMapResetBtn',
			fullscreenBtnId: 'actMapFullscreenBtn',
			fullscreenCardId: 'activitiesMapCard',
			defaultHeight: 700,
			step:          200,
			maxHeight:     4000,
		},
		deferInit,

		createMapLibre(_containerId, styleUrl, savedCam, basemapId) {
			// cachedTracksData may be empty (progressive load); MapLibre still builds the map and
			// frames it from the up-front viewport, then chunks fill in via appendChunkToMap.
			return mlModule.init(cachedTracksData ?? [], styleUrl, savedCam, colorPalette, _initialViewBounds, basemapId);
		},

		onMapLibreReady(_map, _styleUrl, _savedCam) {
			mlModule.attachViewListeners(onViewChanged);
			setTrackHighlight(_effectiveHighlight()); // re-apply the highlight after a style switch (destroy reset it)
		},

		switchMapLibreStyle(_map, styleUrl, basemapId) {
			if (!cachedTracksData) return;
			mlModule.switchStyle(styleUrl, cachedTracksData, basemapId);
		},

		destroyMapLibre(_map) {
			mlModule.destroy();
		},
	});
}

// ================================================================
// PUBLIC API — imported by the overview page bundles (page-init boots the
// map; grid + shape-filter drive filter/highlight). Exported before the map
// boots behave as today's window shims did — no-op / null until `shell` exists.
// ================================================================

// Expose the live map shell so optional add-ons (e.g. the shape-filter overlay) can
// reach the active engine without this module knowing about them.
export function getActivitiesMapShell(): MapShellInstance | null { return shell; }

// Three independent writers compose the one track highlight: the editor-pick checkbox selection ('pick'),
// the shape-filter paint tool ('paint') and the grid display-mode controller ('grid'). Per source:
// null = inactive (fall through); [] = active but nothing matches (dim every track). Priority
// pick > paint > grid: once the user checks a track for the editor, the picks own the map until cleared;
// with no picks it falls through to today's paint-over-grid behavior.
const _highlightSources: { grid: number[] | null; paint: number[] | null; pick: number[] | null } =
	{ grid: null, paint: null, pick: null };
function _effectiveHighlight(): number[] | null {
	return _highlightSources.pick ?? _highlightSources.paint ?? _highlightSources.grid;
}
export function setOverviewHighlight(source: 'grid' | 'paint' | 'pick', ids: number[] | null): void {
	_highlightSources[source] = ids;
	setTrackHighlight(_effectiveHighlight());
	_notifyHighlight();
}

// ── TrackEditor entry-picker seam ──
// A read/observe bridge, mirroring the photo-overlay subscription (:48-57). One-directional: this
// module never imports track-edit code — the picker imports getOverviewSelectionBridge. No equality
// diffing here (subscribers guard themselves against the grid's 150 ms re-pushes). Filter/paint/grid
// compositor (_highlightSources) is untouched.
const _highlightSubs: Array<(ids: number[] | null) => void> = [];
function _notifyHighlight(): void {
	const ids = _effectiveHighlight();
	for (const cb of _highlightSubs) {
		try { cb(ids); } catch (e: unknown) { console.warn('[overview] highlight subscriber failed:', e); }
	}
}

export interface OverviewSelectionBridge {
	/** Effective highlight ids (paint ?? grid — :269-271); null = no source active. */
	getHighlightIds(): number[] | null;
	/** Fires immediately with the current value, then after every setOverviewHighlight. */
	onHighlightChanged(cb: (ids: number[] | null) => void): void;
	/** name + date ("dd.MM.yyyy HH:mm") from the GPS cache (:25); null if never drawn. */
	getTrackMeta(id: number): { name: string | null; date: string } | null;
}

export function getOverviewSelectionBridge(): OverviewSelectionBridge {
	return {
		getHighlightIds: () => _effectiveHighlight(),
		onHighlightChanged: (cb) => { _highlightSubs.push(cb); cb(_effectiveHighlight()); },
		getTrackMeta: (id) => {
			const t = gpsCacheById.get(id);
			return t ? { name: t.name, date: t.date } : null;
		},
	};
}

/**
 * Chunked GPS load shared by the initial load and reloads. Assumes the shell already exists and
 * is positioned. Streams chunks onto the map (progressive paint), updates the progress counter,
 * then registers progressive-quality once the full set is in. Cancellable via `signal`.
 */
async function streamChunksIntoMap(ids: number[], signal: AbortSignal): Promise<void> {
	const mapStatus = document.getElementById('mapStatus');
	const loadingSpinner = document.getElementById('mapLoadingSpinner');
	if (loadingSpinner) loadingSpinner.style.display = 'block';
	if (mapStatus) mapStatus.textContent = '(Loading 0 / ' + ids.length + ' tracks...)';

	try {
		await loadActivitiesChunked({
			ids,
			onChunk: (tracks) => { if (!signal.aborted) appendChunkToMap(tracks); },
			onProgress: (loaded, total) => {
				if (mapStatus && !signal.aborted)
					mapStatus.textContent = '(Loading ' + loaded + ' / ' + total + ' tracks...)';
			},
			signal,
		});
		if (signal.aborted) return;

		flushMaplibreRefresh(); // paint the final (throttled) chunk on MapLibre

		// Now that the whole set is loaded, scope progressive quality to it for zoom upgrades.
		prog.init(ids, {
			onTracksUpgraded: handleTracksUpgraded,
			getZoom: getCurrentZoom,
			getBounds: getCurrentBounds,
			getCachedTracks: () => cachedTracksData,
		});

		// Zoom/pan during the load was ignored (progressive quality wasn't scoped yet). Trigger one
		// upgrade pass now so the current viewport reaches the right tier without waiting for the user
		// to nudge the map. No-op when the view is zoomed out enough that tier 0 already suffices.
		onViewChanged();

		const count = cachedTracksData ? cachedTracksData.length : 0;
		if (count === 0) {
			// Leave the live MapLibre container intact (empty source already renders nothing);
			// wiping innerHTML here tore out the WebGL canvas under a live map (MC-09).
			if (mapStatus) mapStatus.textContent = '(No GPS data)';
		} else if (mapStatus) {
			mapStatus.textContent = '(' + count + ' tracks)';
		}
	} catch (err: unknown) {
		if ((err as DOMException)?.name === 'AbortError') return; // superseded — stay silent
		console.error('streamChunksIntoMap error:', err);
		if (mapStatus) mapStatus.textContent = '(Error loading tracks)';
	} finally {
		if (!signal.aborted && loadingSpinner) loadingSpinner.style.display = 'none';
	}
}

/**
 * Initial map load. Fetches the cheap up-front viewport, builds the shell (deferred init), then
 * switches it to the page's default basemap framed on that viewport (so the camera never jumps),
 * then streams the GPS in chunks with live progress.
 */
export function startActivitiesMap(allIds: number[], profileDefaultId?: string): void {
	if (!allIds || allIds.length === 0) return;
	void _runInitialLoad(allIds, loadRun.begin(), profileDefaultId);
}

async function _runInitialLoad(
	allIds: number[],
	signal: AbortSignal,
	profileDefaultId?: string,
): Promise<void> {
	cachedTracksData = [];
	prog.reset();
	_initialViewBounds = null;
	_pendingFallbackFit = false;

	const loadingSpinner = document.getElementById('mapLoadingSpinner');
	const mapStatus = document.getElementById('mapStatus');
	if (loadingSpinner) loadingSpinner.style.display = 'block';
	if (mapStatus) mapStatus.textContent = '(Loading GPS data...)';

	try {
		_initialViewBounds = await fetchActivityViewBounds(allIds, signal);
	} catch (err: unknown) {
		if ((err as DOMException)?.name === 'AbortError') return;
		console.warn('startActivitiesMap: bounds fetch failed; continuing without up-front camera:', err);
		_initialViewBounds = null;
	}
	if (signal.aborted) return;
	_pendingFallbackFit = _initialViewBounds == null; // no up-front box → fit the first chunk once

	shell = _createShell(true); // deferInit — go straight to the default basemap (MapLibre-only)
	// Server passes the profile's default basemap id; fall back to the registry default (BT-07).
	const id = profileDefaultId ?? defaultBasemapId();
	shell.switchToBasemap(id); // frames to _initialViewBounds via createMapLibre → mlModule.init
	shell.setActiveBasemap(id);

	await streamChunksIntoMap(allIds, signal);
}

// Instant filter: redraw the map to show only `visibleIds`, drawn from the in-memory GPS cache.
// Never tears down the shell or re-fetches already-cached tracks, so it's cheap enough to run live
// on every grid filter change. Only ids missing from the cache (a broadened filter) trigger a delta
// fetch. The camera is left untouched.
export function setActivitiesFilter(visibleIds: number[]): void {
	if (!shell) return; // map not built yet — initial load handles the first paint

	const signal = loadRun.begin();

	const mapStatus = document.getElementById('mapStatus');
	const loadingSpinner = document.getElementById('mapLoadingSpinner');

	const ids = visibleIds ?? [];
	const visibleSet = new Set(ids);

	// Show the already-cached portion of the new filter immediately (instant, from memory). This one
	// redraw also drops any tracks the new filter removed — they simply aren't redrawn. The slow part
	// (fetching ids the filter newly revealed) is then painted progressively below, not all at once.
	const cachedVisible = ids
		.map((id) => gpsCacheById.get(id))
		.filter((t): t is OverviewTrack => !!t && !!t.points && t.points.length > 0);
	cachedTracksData = cachedVisible;
	// Re-scope progressive quality to the full target set (cached + about-to-fetch) so zoom upgrades
	// target everything that will be shown.
	prog.init(ids, {
		onTracksUpgraded: handleTracksUpgraded,
		getZoom: getCurrentZoom,
		getBounds: getCurrentBounds,
		getCachedTracks: () => cachedTracksData,
	});
	redrawCurrentTracks();
	_notifyVisibleTracks(); // filter changed — photo overlay drops/keeps markers to match

	const missing = ids.filter((id) => !gpsCacheById.has(id));
	if (missing.length === 0) {
		// This run owns the spinner (loadRun.begin above) — a superseded in-flight load's finally
		// skips hiding when aborted, so hide it here or it spins forever (MC-04).
		if (loadingSpinner) loadingSpinner.style.display = 'none';
		if (mapStatus) mapStatus.textContent = '(' + cachedVisible.length + ' tracks)';
		return;
	}

	// Broadened filter — chunk-fetch the missing ids and PAINT each chunk as it arrives (appending
	// onto the cached portion already shown via appendChunkToMap), instead of one redraw at the end.
	if (loadingSpinner) loadingSpinner.style.display = 'block';
	if (mapStatus) mapStatus.textContent = '(Loading 0 / ' + missing.length + ' tracks...)';

	loadActivitiesChunked({
		ids: missing,
		onChunk: (tracks) => {
			if (signal.aborted) return;
			// Guard against a track that left the filter between request and response.
			appendChunkToMap(tracks.filter((t) => visibleSet.has(t.id)));
		},
		onProgress: (loaded, total) => {
			if (mapStatus && !signal.aborted)
				mapStatus.textContent = '(Loading ' + loaded + ' / ' + total + ' tracks...)';
		},
		signal,
	})
		.then(() => {
			if (signal.aborted) return;
			flushMaplibreRefresh(); // paint the final (throttled) chunk on MapLibre
			if (mapStatus)
				mapStatus.textContent = '(' + (cachedTracksData ? cachedTracksData.length : 0) + ' tracks)';
		})
		.catch((err: unknown) => {
			if ((err as DOMException)?.name === 'AbortError') return;
			console.error('setActivitiesFilter error:', err);
			if (mapStatus) mapStatus.textContent = '(Error loading tracks)';
		})
		.finally(() => { if (!signal.aborted && loadingSpinner) loadingSpinner.style.display = 'none'; });
}
