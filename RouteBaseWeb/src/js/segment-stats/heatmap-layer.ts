/**
 * Segment Statistics — Heatmap tile layer
 *
 * Display side of the activity-heatmap feature. A heatmap is an optional raster overlay on a
 * shapes segment: its render is a sparse Web-Mercator PNG tile pyramid served by
 * `api/heatmaps/{id}/tiles/{layer}/{z}/{x}/{y}.png`. Both engines overzoom the pyramid natively,
 * so we hand them the URL template + native zoom range + geographic bounds and let them scale.
 *
 * `load()` fetches metadata once and, if the render is still queued/running, polls until it
 * settles (a user can open a heatmap link before generation finishes). The attach/detach helpers
 * wire the tiles into the MapLibre map; heatmap-controls.ts drives them from the map's
 * _applyLayers so the raster re-stacks below the effort/segment lines on every redraw.
 */

// Wire shape of GET /api/heatmaps/{id} — generated from C# HeatmapMetaResponse/HeatmapBoundsDto.
import type { HeatmapMetaResponse as HeatmapMeta } from '../generated/contracts';

const SRC_ID = "heatmap-tiles";
const LAYER_ID = "heatmap-tiles";

const POLL_INTERVAL_MS = 2000;
const POLL_MAX_ATTEMPTS = 300; // ~10 min ceiling for a direct-link visit to a running render

let _meta: HeatmapMeta | null = null;
// Poll-loop generation: each load() supersedes the previous one, so repeated generate() clicks
// (or a re-entered load) never stack concurrent loops fighting over _meta.
let _loadGen = 0;

function sleep(ms: number): Promise<void> {
	return new Promise((resolve) => setTimeout(resolve, ms));
}

// The `?v=` stamp busts the browser cache after a regenerate: tiles are served with a 7-day
// max-age (which suppresses ETag revalidation), so without a per-generation URL the map would show
// week-stale imagery until expiry. The server route ignores the query string.
function tileUrl(segmentId: number, layer: string, generatedAt?: string | null): string {
	return `/api/heatmaps/${segmentId}/tiles/${encodeURIComponent(layer)}/{z}/{x}/{y}.png?v=${encodeURIComponent(generatedAt ?? "0")}`;
}

async function fetchMeta(segmentId: number): Promise<HeatmapMeta | null> {
	try {
		const r = await fetch(`/api/heatmaps/${segmentId}`);
		if (!r.ok) return null;
		const body: HeatmapMeta = await r.json();
		return body;
	} catch (e) {
		console.warn("[heatmap-layer] metadata fetch failed:", e);
		return null;
	}
}

export const heatmapDisplay = {
	getMeta(): HeatmapMeta | null { return _meta; },

	/**
	 * Fetch heatmap metadata; if the render is still queued/running, poll until it reaches
	 * done/failed. `onStatus` fires on each intermediate poll so the caller can surface progress.
	 */
	async load(segmentId: number, onStatus?: (m: HeatmapMeta) => void): Promise<HeatmapMeta | null> {
		const gen = ++_loadGen;
		for (let i = 0; i < POLL_MAX_ATTEMPTS; i++) {
			const m = await fetchMeta(segmentId);
			if (gen !== _loadGen) return _meta; // superseded by a newer load() — stop this loop
			if (!m) return _meta;
			_meta = m;
			// Terminal states: done/stale (viewable tiles) and failed. Only pending/running poll on.
			if (m.status === "done" || m.status === "stale" || m.status === "failed") return m;
			onStatus?.(m);
			await sleep(POLL_INTERVAL_MS);
			if (gen !== _loadGen) return _meta;
		}
		return _meta;
	},

	attachMapLibre(map: maplibregl.Map, fit: boolean): void {
		if (!_meta || (_meta.status !== "done" && _meta.status !== "stale")) return;
		// setStyle(diff:false) on a style switch wipes all sources/layers, so re-adding is expected.
		try { if (map.getLayer(LAYER_ID)) map.removeLayer(LAYER_ID); } catch (_e) { /* absent */ }
		try { if (map.getSource(SRC_ID)) map.removeSource(SRC_ID); } catch (_e) { /* absent */ }
		const b = _meta.bounds;
		try {
			map.addSource(SRC_ID, {
				type: "raster",
				tiles: [tileUrl(_meta.segmentId, _meta.layer, _meta.generatedAt)],
				tileSize: 256,
				...(_meta.minZoom != null ? { minzoom: _meta.minZoom } : {}),
				...(_meta.maxZoom != null ? { maxzoom: _meta.maxZoom } : {}),
				attribution: 'Heatmap: <a href="https://github.com/moresamwilson/running-heatmap">running-heatmap</a> © moresamwilson',
				...(b ? { bounds: [b.west, b.south, b.east, b.north] as [number, number, number, number] } : {}),
			});
			map.addLayer({
				id: LAYER_ID,
				type: "raster",
				source: SRC_ID,
				paint: { "raster-opacity": 1, "raster-fade-duration": 0 },
			});
		} catch (e) {
			console.warn("[heatmap-layer] attachMapLibre failed:", e);
		}
		if (fit && b) {
			try {
				map.fitBounds([[b.west, b.south], [b.east, b.north]], { padding: 40 });
			} catch (e) { console.warn("[heatmap-layer] fitBounds failed:", e); }
		}
	},

	detachMapLibre(map: maplibregl.Map): void {
		try { if (map.getLayer(LAYER_ID)) map.removeLayer(LAYER_ID); } catch (_e) { /* absent */ }
		try { if (map.getSource(SRC_ID)) map.removeSource(SRC_ID); } catch (_e) { /* absent */ }
	},
};

export type { HeatmapMeta };
