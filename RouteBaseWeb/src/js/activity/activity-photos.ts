// activity-photos.ts — Details Photos panel + map overlay (§4). Owns the collapsible photo grid (view /
// place / move / unpin / restore) and feeds the shared PhotoMapOverlay (clustering + zoom-to-thumbnail +
// fullscreen viewer), which a toolbar toggle shows/hides. `maplibregl` is a page global (CDN); map comes
// from state.
//
// The grid, the map markers and the full-size viewer all page ONE array in ONE order (allPhotos, oldest
// first) — so "photo 3" means the same thing in all three, and the viewer's ‹ › walk the ride.

import { thumbHashToDataURL } from "../photos/photo-thumbhash";
import { openPhotoFullscreen, type LightboxPhoto } from "../photos/photo-lightbox";
import { getMaplibreMap } from "../core/map-state";
import { PhotoMapOverlay, THUMB_ZOOM } from "../photos/photo-map-overlay";
import type { PhotoMarker } from "../photos/photo-map-types";
import { closeOtherActionPanels, registerActionPanel } from "./details-shared";
import { escapeHtml } from "../core/html";
import { photoImageUrl, setPhotoApiBase } from "../photos/photo-image-url";
import { dismissPopupOnMapGesture } from "../core/map-user-gesture";
import type { PhotoDto, PhotoListResponse } from "../generated/contracts";

// The subset of the generated PhotoDto this panel reads/mutates (D4): placement + captions, no grid-only
// fields (trackName, dimensions, camera). Tied to the source of truth — a rename/removal in C# breaks here.
type ActivityPhoto = Pick<PhotoDto,
	'photoId' | 'trackId' | 'thumbHashBase64' | 'markerLat' | 'markerLng' | 'locationSource' |
	'canRestore' | 'capturedUtc' | 'tzOffsetHours' | 'origFilename' | 'description' | 'v'>;

let overlay: PhotoMapOverlay | null = null; // clustered map markers (toolbar toggle), fed from allPhotos
let popup: maplibregl.Popup | null = null;

// The activity's photos (kept so placement can mutate one + re-render) and whether the viewer owns them
// (only owners may hand-place — the API enforces it per-photo regardless).
let allPhotos: ActivityPhoto[] = [];
let canPlace = false;

// The server's true count (PhotoListResponse.Total), vs. allPhotos.length which is capped at the page
// size. Used only to show the "showing first N" notice below the grid — never to page further; the share
// surface's photo endpoint stays a single fixed-size request (the share photo cap).
let totalPhotos = 0;

// Where the photo READ endpoints live for this render — see photos/photo-image-url. Held here too because
// the LIST fetch below needs it, while rendition URLs come from the shared builder so this grid and the
// map overlay get the same base. Write endpoints are not rebased: they are owner-only and unreachable on
// a share view, where canPlace is false.
let photosApiBase = "/api/photos";

// Active "click the map to place this photo" session, or null. Holds the map/keyboard handlers + hint
// banner so cancel/commit can tear them all down.
interface Placing {
	photo: ActivityPhoto;
	onMapClick: (e: maplibregl.MapMouseEvent) => void;
	onKey: (e: KeyboardEvent) => void;
	hint: HTMLElement;
}
let placing: Placing | null = null;

export function setupActivityPhotos(
	trackId: number,
	canPlacePhotos: boolean,
	apiBase = "/api/photos",
): void {
	const grid = document.getElementById("detPhotosGrid");
	if (!grid) return;
	canPlace = canPlacePhotos;
	photosApiBase = apiBase;
	// Rebase the SHARED builder too, so the map overlay and the viewer resolve to the same place.
	setPhotoApiBase(apiBase);

	overlay = new PhotoMapOverlay({ getMap: getMaplibreMap });
	setupPhotosMapToggle();

	// Photos tab in the shared Details action bar (grouped with Create Segment); opening it closes the
	// segment panel and vice-versa (details-shared accordion).
	const tab = document.getElementById("detPhotosTab");
	const body = document.getElementById("detPhotosBody") as HTMLElement | null;

	function setOpen(open: boolean): void {
		if (!body || !tab) return;
		if (!open) cancelPlacement(); // closing the panel (incl. when Create Segment opens) ends placement
		body.hidden = !open;
		tab.setAttribute("aria-expanded", String(open));
		tab.classList.toggle("active", open);
	}

	// Drops the placement popup too: it is a MapLibre Popup, anchored to a coordinate, so it repositions
	// on every `move` — and a replay start (which calls closeAllActionPanels) must not leave one riding
	// the camera at 60 Hz with its × as a moving target.
	registerActionPanel("photos", () => { setOpen(false); popup?.remove(); });
	tab?.addEventListener("click", () => {
		const opening = body?.hidden !== false;
		if (opening) closeOtherActionPanels("photos");
		setOpen(opening);
	});

	void load(trackId);
}

async function load(trackId: number): Promise<void> {
	let photos: ActivityPhoto[];
	try {
		// The share endpoint takes no trackId — it reads it from the grant — but sending it is harmless
		// and keeps one call site for both routes.
		const resp = await fetch(`${photosApiBase}?trackId=${trackId}&pageSize=200`);
		if (!resp.ok) return;
		// Generated PhotoListResponse — this call site serves BOTH the authenticated and the /s/{token}
		// route; the source-of-truth type keeps the two shapes from drifting apart silently.
		const body: PhotoListResponse = await resp.json();
		photos = body.items ?? [];
		totalPhotos = body.total ?? photos.length;
	} catch {
		return;
	}

	// The API orders newest-first (PhotoQueryService: CapturedUtc desc). Within ONE activity the natural
	// reading is along the ride, and the grid, the map markers and the viewer's ‹ › all page this same
	// array — so flip it once, here, rather than in three places.
	allPhotos = [...photos].sort(byCaptureTime);

	const count = document.getElementById("detPhotosCount");
	if (count) count.textContent = photos.length ? String(photos.length) : "";
	const empty = document.getElementById("detPhotosEmpty");
	if (empty) (empty as HTMLElement).hidden = photos.length > 0;

	// Neither route pages past this one request (share never does, by design; this panel doesn't
	// either) — so a total exceeding what came back means the rest were left off, not that none exist.
	const truncated = document.getElementById("detPhotosTruncated") as HTMLElement | null;
	if (truncated) {
		const isTruncated = totalPhotos > photos.length;
		truncated.hidden = !isTruncated;
		if (isTruncated) truncated.textContent = `Showing first ${photos.length} of ${totalPhotos} photos`;
	}

	renderGrid();
	syncOverlay();
}

// ── Map overlay (shared clustering/thumbnail engine) ────────────────────────────
// The overlay is toggled from the map toolbar (default on). We feed it the activity's placed photos,
// re-syncing whenever placement changes; the shared engine handles clustering, zoom thumbnails and the
// click-to-fullscreen path.
function setupPhotosMapToggle(): void {
	const btn = document.getElementById("detPhotosMapToggle") as HTMLButtonElement | null;
	if (!btn || !overlay) return;
	const startOn = btn.getAttribute("aria-pressed") !== "false"; // markup default = on
	btn.classList.toggle("active", startOn);
	if (startOn) overlay.enable();
	const ov = overlay; // narrowed rebind for the click closure (G3)
	btn.addEventListener("click", () => {
		const on = !ov.isEnabled();
		btn.classList.toggle("active", on);
		btn.setAttribute("aria-pressed", String(on));
		if (on) ov.enable();
		else ov.disable();
	});
}

function overlayMarkers(): PhotoMarker[] {
	const out: PhotoMarker[] = [];
	for (const p of allPhotos) {
		if (p.markerLat == null || p.markerLng == null) continue; // unplaced/hidden → not on the map
		out.push({
			photoId: p.photoId, trackId: p.trackId, lat: p.markerLat, lng: p.markerLng,
			locationSource: p.locationSource, thumbHashBase64: p.thumbHashBase64, v: p.v,
		});
	}
	return out;
}

function syncOverlay(): void {
	overlay?.setMarkers(overlayMarkers());
}

// Rebuilds the thumbnail grid from allPhotos — called on load and again after a placement so the
// just-placed cell drops its unplaced ring and its pin flips from "place" to "move".
function renderGrid(): void {
	const grid = document.getElementById("detPhotosGrid");
	if (!grid) return;
	grid.innerHTML = "";
	allPhotos.forEach((p, i) => grid.appendChild(thumbCell(p, i)));
}

const imageUrl = photoImageUrl;

/** Oldest first, undated photos last — ISO-8601 off the wire compares correctly as a string. */
function byCaptureTime(a: ActivityPhoto, b: ActivityPhoto): number {
	if (!a.capturedUtc) return b.capturedUtc ? 1 : 0;
	if (!b.capturedUtc) return -1;
	return a.capturedUtc < b.capturedUtc ? -1 : a.capturedUtc > b.capturedUtc ? 1 : 0;
}

function thumbCell(p: ActivityPhoto, index: number): HTMLElement {
	const btn = document.createElement("button");
	btn.type = "button";
	btn.className = "det-photo-thumb";
	if (p.thumbHashBase64) {
		const url = thumbHashToDataURL(p.thumbHashBase64);
		if (url) btn.style.backgroundImage = `url(${url})`;
	}
	const img = document.createElement("img");
	img.className = "det-photo-img";
	img.loading = "lazy";
	img.alt = p.origFilename ?? "";
	img.src = imageUrl(p, "thumb");
	img.addEventListener("load", () => img.classList.add("is-loaded"));
	btn.appendChild(img);

	const unplaced = p.markerLat == null || p.markerLng == null;
	if (unplaced && canPlace) {
		// Never placed → pink ring (needs a location). Hidden-but-remembered → neutral ring (intentional).
		btn.classList.add(p.canRestore ? "det-photo-thumb--offmap" : "det-photo-thumb--unplaced");
	}

	// Single click/tap opens the full-size viewer straight away — one click, and a close button that is
	// always there. It used to open a MapLibre popup whose × is a small dark glyph that disappears over
	// a dark photo, with fullscreen only a second click away. Placement stays on the corner pin button.
	btn.addEventListener("click", () => openViewer(index));

	// Owner corner controls: pin (place/move) is always leftmost so it stays put across states; the
	// state-specific button follows — × to remove a shown photo, or ↩ to restore a hidden one to its last
	// spot. Single click/tap works on desktop + mobile.
	if (canPlace) {
		const actions = document.createElement("span");
		actions.className = "det-photo-actions";

		const pin = document.createElement("span");
		pin.className = "det-photo-place-btn";
		pin.title = unplaced ? "Place on map" : "Move pin";
		pin.innerHTML = `<i class="bi bi-geo-alt-fill" aria-hidden="true"></i>`;
		pin.addEventListener("click", (e) => { e.stopPropagation(); beginPlacement(p); });
		actions.appendChild(pin);

		if (!unplaced) {
			const unpinBtn = document.createElement("span");
			unpinBtn.className = "det-photo-unpin-btn";
			unpinBtn.title = "Remove from map (keep on activity)";
			unpinBtn.innerHTML = `<i class="bi bi-x-lg" aria-hidden="true"></i>`;
			unpinBtn.addEventListener("click", (e) => { e.stopPropagation(); void unpin(p); });
			actions.appendChild(unpinBtn);
		} else if (p.canRestore) {
			const restoreBtn = document.createElement("span");
			restoreBtn.className = "det-photo-restore-btn";
			restoreBtn.title = "Show on map again (last position)";
			restoreBtn.innerHTML = `<i class="bi bi-arrow-counterclockwise" aria-hidden="true"></i>`;
			restoreBtn.addEventListener("click", (e) => { e.stopPropagation(); void restore(p); });
			actions.appendChild(restoreBtn);
		}
		btn.appendChild(actions);
	}
	return btn;
}

/** The activity's photos as viewer items — same array, same order, so an index means one thing. */
function lightboxPhotos(): LightboxPhoto[] {
	return allPhotos.map((p) => ({
		url: imageUrl(p, "preview"),
		lngLat: p.markerLat != null && p.markerLng != null ? [p.markerLng, p.markerLat] : null,
		// The caption used to live in the map popup, which this path no longer opens.
		caption: p.description ?? null,
	}));
}

// Open the full-size viewer on the whole activity, starting at this photo. The map pans behind it on
// every step, so paging the viewer is also how you walk the ride's photos on the map. Unplaced photos
// have nowhere to pan to — they just show.
//
// Centre only, matching the map overlay's own onShow: the zoom is the user's, and a viewer that walks
// photos under a replay must hand the map back at the framing they set up, not at one it chose.
function openViewer(index: number): void {
	openPhotoFullscreen(lightboxPhotos(), index, {
		onShow: (photo) => {
			const map = getMaplibreMap();
			if (!map || !photo.lngLat) return;
			map.easeTo({ center: photo.lngLat, duration: 400 });
		},
	});
}

function openPopup(p: ActivityPhoto, lngLat: [number, number]): void {
	const map = getMaplibreMap();
	if (!map) return;
	if (popup) popup.remove();
	// Thin-bordered popup (className scopes the padding/close-button CSS); no filename shown, but the
	// user-authored caption (if any) is — it's the one photo-level field every viewer is meant to see.
	const shown = new maplibregl.Popup({ closeButton: true, closeOnClick: true, maxWidth: "320px", className: "det-photo-popup-wrap" })
		.setLngLat(lngLat)
		.setHTML(
			`<div class="det-photo-popup">` +
			`<img src="${imageUrl(p, "preview")}" alt="" title="Click to enlarge" />` +
			(p.description ? `<div class="det-photo-popup-caption">${escapeHtml(p.description)}</div>` : "") +
			`</div>`,
		)
		.addTo(map);
	// Click the popup image → open it full-size, paged with the rest of the activity.
	shown.getElement()?.querySelector("img")?.addEventListener("click", () => openViewer(Math.max(0, allPhotos.indexOf(p))));
	// Same dismissal as the overlay's popups: any map gesture drops it, not just its ×.
	dismissPopupOnMapGesture(map, shown);
	shown.on("close", () => { if (popup === shown) popup = null; });
	popup = shown;
}

// Placement confirmation only: after the user drops a pin, a popup at the new spot is the right
// feedback — a fullscreen takeover would hide the very thing they just placed.
function flyToAndOpen(p: ActivityPhoto): void {
	const map = getMaplibreMap();
	if (!map || p.markerLng == null || p.markerLat == null) return; // only just-placed photos reach here
	map.flyTo({ center: [p.markerLng, p.markerLat], zoom: Math.max(map.getZoom(), THUMB_ZOOM) });
	openPopup(p, [p.markerLng, p.markerLat]);
}

// ── Manual placement (unplaced photos) ──────────────────────────────────────────
// Arm a one-shot "next map click sets this photo's location" session: crosshair cursor, a hint banner
// over the map, Esc/Cancel to abort. The click posts the coords; the DB stores them as manual_lat/lng and
// the marker appears immediately.
function beginPlacement(p: ActivityPhoto): void {
	const map = getMaplibreMap();
	if (!map) return;
	cancelPlacement(); // never stack two sessions

	document.getElementById("mapCard")?.scrollIntoView({ behavior: "smooth", block: "center" });

	const onMapClick = (e: maplibregl.MapMouseEvent) => { void commitPlacement(p, e.lngLat.lat, e.lngLat.lng); };
	const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") cancelPlacement(); };
	map.on("click", onMapClick);
	document.addEventListener("keydown", onKey);
	map.getCanvas().style.cursor = "crosshair";

	placing = { photo: p, onMapClick, onKey, hint: showPlaceHint(p) };
}

function cancelPlacement(): void {
	if (!placing) return;
	const map = getMaplibreMap();
	map?.off("click", placing.onMapClick);
	if (map) map.getCanvas().style.cursor = "";
	document.removeEventListener("keydown", placing.onKey);
	placing.hint.remove();
	placing = null;
}

async function commitPlacement(p: ActivityPhoto, lat: number, lng: number): Promise<void> {
	try {
		const resp = await fetch(`/api/photos/${p.photoId}/place`, {
			method: "POST",
			headers: { "Content-Type": "application/json" },
			body: JSON.stringify({ lat, lng }),
		});
		if (!resp.ok) return; // leave the session armed so the user can click again
	} catch {
		return;
	}
	p.markerLat = lat;
	p.markerLng = lng;
	p.locationSource = "manual";
	cancelPlacement();
	renderGrid();     // the cell drops its unplaced styling + gains the fly-to handler
	syncOverlay();    // feed the new marker to the map overlay
	flyToAndOpen(p);
}

// Remove a photo from the map — the server hides it (keeps the coords) so it's "free": still on the
// activity, off the map, restorable to the same spot. Re-fetch so the cell drops its marker + shows the
// off-map ring with a restore affordance.
async function unpin(p: ActivityPhoto): Promise<void> {
	try {
		const resp = await fetch(`/api/photos/${p.photoId}/place`, { method: "DELETE" });
		if (!resp.ok) return;
	} catch {
		return;
	}
	if (popup) { popup.remove(); popup = null; }
	await load(p.trackId);
}

// Restore a hidden photo to the map at its remembered location. Re-fetch so the marker + on-map cell return.
async function restore(p: ActivityPhoto): Promise<void> {
	try {
		const resp = await fetch(`/api/photos/${p.photoId}/restore`, { method: "POST" });
		if (!resp.ok) return;
	} catch {
		return;
	}
	await load(p.trackId);
}

function showPlaceHint(p: ActivityPhoto): HTMLElement {
	const host = document.getElementById("mapCard") ?? document.body;
	const hint = document.createElement("div");
	hint.className = "det-photo-place-hint";
	hint.innerHTML =
		`<i class="bi bi-geo-alt-fill" aria-hidden="true"></i>` +
		`<span>Click the map to place <strong>${escapeHtml(p.origFilename || "this photo")}</strong></span>` +
		`<button type="button" class="det-photo-place-cancel">Cancel</button>`;
	hint.querySelector(".det-photo-place-cancel")?.addEventListener("click", cancelPlacement);
	host.appendChild(hint);
	return hint;
}
