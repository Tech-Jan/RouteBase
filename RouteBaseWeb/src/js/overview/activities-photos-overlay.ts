// activities-photos-overlay.ts — Activities overview adapter for the shared photo map overlay.
//
// Off by default. A camera toggle (#actPhotosToggle) turns it on; only then are markers fetched
// (GET /api/photos/map-markers — the caller's own placed photos). The shared PhotoMapOverlay does all the
// rendering/clustering/viewer work; this file only supplies the overview specifics: the map (from the
// overview shell), the data fetch, and live re-filtering to the currently-visible tracks (grid filter /
// streamed chunks). `maplibregl` is a page global on the Activities page (CDN).

import { getActivitiesMapShell, getVisibleTrackIds, onVisibleTracksChanged } from "./activities-overview";
import { PhotoMapOverlay } from "../photos/photo-map-overlay";
import type { PhotoMarker } from "../photos/photo-map-types";
import type { PhotoMarkersResponse } from "../generated/contracts";

let overlay: PhotoMapOverlay | null = null;
let enabled = false;
let allMarkers: PhotoMarker[] | null = null; // own markers, fetched once and cached across toggles
let fetchPromise: Promise<void> | null = null;

export function setupPhotosOverlay(): void {
	const btn = document.getElementById("actPhotosToggle") as HTMLButtonElement | null;
	if (!btn) return;
	overlay = new PhotoMapOverlay({ getMap: () => getActivitiesMapShell()?.getMapLibreMap() ?? null });
	btn.addEventListener("click", () => void toggle(btn));
	// Visible track set changed (chunk streamed in, or grid filter applied) — re-filter our markers.
	onVisibleTracksChanged(() => { if (enabled) applyVisibleFilter(); });
}

async function toggle(btn: HTMLButtonElement): Promise<void> {
	enabled = !enabled;
	btn.classList.toggle("active", enabled);
	btn.setAttribute("aria-pressed", String(enabled));
	if (enabled) {
		await ensureData();
		if (!enabled) return; // toggled back off while fetching
		applyVisibleFilter();
		overlay?.enable(); // null only if ensureData failed — nothing to show
	} else {
		overlay?.disable();
	}
}

async function ensureData(): Promise<void> {
	if (allMarkers != null) return;
	if (!fetchPromise) {
		fetchPromise = (async () => {
			try {
				const resp = await fetch("/api/photos/map-markers");
				const markersBody: PhotoMarkersResponse | null = resp.ok ? await resp.json() : null;
				allMarkers = markersBody?.items ?? [];
			} catch {
				allMarkers = [];
			} finally {
				fetchPromise = null;
			}
		})();
	}
	await fetchPromise;
}

function applyVisibleFilter(): void {
	if (!overlay || !allMarkers) return;
	const visible = new Set(getVisibleTrackIds());
	overlay.setMarkers(allMarkers.filter((m) => visible.has(m.trackId)));
}
