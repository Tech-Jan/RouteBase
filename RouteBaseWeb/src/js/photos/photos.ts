// photos.ts — Photos gallery: paginated grid (ThumbHash blur-up + lazy thumbs), date filters, and a
// lightbox (preview + minimap of the assigned activity + link). Importing lives on the /Import page.
//
// The lightbox keeps its own layout (side panel: meta, minimap, delete, activity link) but borrows the
// shared control layer from photos/photo-viewer-chrome — so close/‹/› sit in the same places as the map
// viewer's and, more to the point, stay on screen when a phone pinch-zooms into the photo.

import { asyncCallback } from "../core/async-listener";
import { must } from '../core/invariant';
import { thumbHashToDataURL } from "./photo-thumbhash";
import { mountViewerChrome, type ViewerChrome } from "./photo-viewer-chrome";
import { mountPhotoZoom, type PhotoZoom } from "./photo-zoom";
import { resolveStyleUrl, defaultBasemapId } from "../core/basemap-registry";
import { installStyleFilterSanitizer } from "../maplibre/style-filter-sanitizer";
import { escapeHtml } from "../core/html";
import type { ActivityGpsPointDto } from "../core/core-types";
// Generated from C# PhotoDto / PhotoListResponse (Models/Photos/PhotoContracts.cs). AS-05 owner-only
// fields (cameraModel/origFilename) are null for non-owners — always render-guarded below.
import type { PhotoDto, PhotoListResponse } from "../generated/contracts";

const PAGE_SIZE = 60;

const grid       = document.getElementById("photoGrid") as HTMLElement;
const empty      = document.getElementById("photoGridEmpty") as HTMLElement;
const pager      = document.getElementById("photoPager") as HTMLElement;
const pageLabel  = document.getElementById("pageLabel") as HTMLElement;
const countLabel = document.getElementById("photosCount") as HTMLElement;
const filterFrom = document.getElementById("filterFrom") as HTMLInputElement;
const filterTo   = document.getElementById("filterTo") as HTMLInputElement;

const groupToggle = document.getElementById("groupByActivity") as HTMLInputElement;

let page = 1;
let total = 0;
let loadGen = 0; // PU-01: monotonic request generation — a slow response for an older page/filter is dropped
let items: PhotoDto[] = []; // the page currently in the grid, in grid order — what the lightbox's ‹ › page
const selected = new Set<number>();

async function load(): Promise<void> {
	const gen = ++loadGen;
	selected.clear(); // selection is per view — reset on any reload (page / filter / group / delete)
	updateSelectBar();
	const grouped = groupToggle.checked;
	const params = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE), sort: grouped ? "activity" : "captured" });
	// Plain yyyy-MM-dd both ways: the server filters by the photo's own local calendar day with an
	// inclusive `to` (IPhotoQueryService) — the old `T23:59:59` end-of-day hack is the server's job now.
	if (filterFrom.value) params.set("from", filterFrom.value);
	if (filterTo.value) params.set("to", filterTo.value);

	let data: PhotoListResponse;
	try {
		const resp = await fetch(`/api/photos?${params}`);
		if (!resp.ok) throw new Error("photos load HTTP " + resp.status);
		data = await resp.json();
	} catch {
		// FUX-06: a failed load used to leave the grid blank with only a console warning. Say so instead
		// — but only for the CURRENT request (PU-01: a superseded stale failure must stay quiet).
		if (gen === loadGen) showGridMessage("Couldn't load photos. Check your connection and try again.");
		return;
	}

	// PU-01: a newer load() was issued while this one awaited — drop this (stale) response so the
	// grid and the pager label/buttons never end up describing two different pages.
	if (gen !== loadGen) return;

	total = data.total;
	items = data.items;
	grid.innerHTML = "";
	let lastTrackId = -1;
	items.forEach((p, i) => {
		if (grouped && p.trackId !== lastTrackId) {
			grid.appendChild(groupHeader(p));
			lastTrackId = p.trackId;
		}
		grid.appendChild(cell(p, i));
	});

	empty.style.display = data.total === 0 ? "" : "none";
	countLabel.textContent = data.total ? `${data.total} photo${data.total === 1 ? "" : "s"}` : "";

	const pages = Math.max(1, Math.ceil(total / PAGE_SIZE));
	pager.style.display = pages > 1 ? "" : "none";
	pageLabel.textContent = `Page ${page} of ${pages}`;
	(document.getElementById("btnPrevPage") as HTMLButtonElement).disabled = page <= 1;
	(document.getElementById("btnNextPage") as HTMLButtonElement).disabled = page >= pages;
}

/** FUX-06: replace the grid with a one-line notice (a failed load). The next successful load clears it. */
function showGridMessage(text: string): void {
	grid.replaceChildren();
	empty.style.display = "none";
	pager.style.display = "none";
	countLabel.textContent = "";
	const msg = document.createElement("div");
	msg.className = "photos-empty";
	msg.setAttribute("role", "alert"); // #photoGrid is aria-live=polite, so the notice is announced
	const p = document.createElement("p");
	p.textContent = text;
	msg.appendChild(p);
	grid.appendChild(msg);
}

function imageUrl(p: PhotoDto, tier: "thumb" | "preview"): string {
	return `/api/photos/${p.photoId}/image?tier=${tier}&v=${encodeURIComponent(p.v)}`;
}

function groupHeader(p: PhotoDto): HTMLElement {
	const h = document.createElement("div");
	h.className = "photos-group-header";
	const date = p.activityStartUtc ? parseUtc(p.activityStartUtc).toLocaleDateString() : "";
	// eslint-disable-next-line no-unsanitized/property -- trackName escapeHtml'd; trackId numeric; date is toLocaleDateString output
	h.innerHTML =
		`<a class="photos-group-title" href="/Activities/Details/${p.trackId}">${escapeHtml(p.trackName ?? `Activity ${p.trackId}`)}</a>` +
		(date ? `<span class="photos-group-date">${date}</span>` : "");
	return h;
}

function cell(p: PhotoDto, index: number): HTMLElement {
	// A wrapper so the corner delete control can be a real <button> that is a SIBLING of the image button
	// — a <button> nested inside a <button> is invalid, which is why the delete used to be a role=button
	// span (with no tabindex, so keyboard/AT could not reach it).
	const wrap = document.createElement("div");
	wrap.className = "photos-cell-wrap";

	const btn = document.createElement("button");
	btn.type = "button";
	btn.className = "photos-cell";
	if (p.thumbHashBase64) {
		const url = thumbHashToDataURL(p.thumbHashBase64);
		if (url) btn.style.backgroundImage = `url(${url})`;
	}
	const img = document.createElement("img");
	img.className = "photos-cell-img";
	img.loading = "lazy";
	img.alt = p.origFilename ?? "";
	img.src = imageUrl(p, "thumb");
	img.addEventListener("load", () => img.classList.add("is-loaded"));
	btn.appendChild(img);
	btn.addEventListener("click", () => openLightbox(index));
	wrap.appendChild(btn);

	// Corner trash toggle — marks the photo for the bulk "Delete" bar. A real <button> (keyboard-focusable
	// and AT-labelled for free), revealed by the CSS on :focus-within / selection / touch rather than
	// :hover alone — so keyboard and phone users can reach this destructive control, not just mouse users.
	const del = document.createElement("button");
	del.type = "button";
	del.className = "photos-cell-del";
	del.setAttribute("aria-label", "Mark for deletion");
	del.setAttribute("aria-pressed", "false");
	del.innerHTML = `<i class="bi bi-trash" aria-hidden="true"></i>`;
	del.addEventListener("click", (e) => { e.stopPropagation(); toggleSelect(p.photoId, wrap, del); });
	wrap.appendChild(del);

	return wrap;
}

// ── Multi-select delete ───────────────────────────────────────────────────────
function toggleSelect(photoId: number, wrap: HTMLElement, del: HTMLElement): void {
	if (selected.has(photoId)) {
		selected.delete(photoId);
		wrap.classList.remove("is-marked");
		del.setAttribute("aria-pressed", "false");
	} else {
		selected.add(photoId);
		wrap.classList.add("is-marked");
		del.setAttribute("aria-pressed", "true");
	}
	updateSelectBar();
}

function updateSelectBar(): void {
	const bar = document.getElementById("photoSelectBar") as HTMLElement | null;
	if (!bar) return;
	const count = document.getElementById("photoSelectCount");
	if (count) count.textContent = `${selected.size} selected`;
	bar.style.display = selected.size > 0 ? "" : "none";
}

async function deleteSelected(): Promise<void> {
	if (selected.size === 0) return;
	if (!confirm(`Delete ${selected.size} photo${selected.size === 1 ? "" : "s"}? This cannot be undone.`)) return;
	try {
		const resp = await fetch("/api/photos/delete", {
			method: "POST",
			headers: { "Content-Type": "application/json" },
			body: JSON.stringify({ photoIds: [...selected] }),
		});
		// PU-02: the open lightbox may be showing one of the just-deleted photos; close it
		// unconditionally (idempotent) so it can't linger with a 404'd image + dead Delete button.
		if (resp.ok) { closeLightbox(); void load(); }
		else alert("Couldn't delete the selected photos. Please try again."); // FUX-06: was a silent no-op
	} catch {
		alert("Couldn't delete the selected photos. Please try again.");
	}
}

// ── Lightbox ──────────────────────────────────────────────────────────────────
let miniMap: { remove(): void } | null = null;
let miniMapGen = 0; // bumped on every open/close so an in-flight async render can detect it was superseded
let lastFocused: HTMLElement | null = null; // PU-04: element to restore focus to on close
let chrome: ViewerChrome | null = null; // non-null exactly while the lightbox is open
let zoom: PhotoZoom | null = null;
let shownIndex = -1;

function openLightbox(index: number): void {
	if (index < 0 || index >= items.length) return;
	const box = document.getElementById("lightbox") as HTMLElement;

	// PU-04: remember the trigger (grid cell) so close can return focus to it. The lightbox
	// covers the grid, so an open→open replace isn't reachable here — always safe to save.
	lastFocused = document.activeElement as HTMLElement | null;

	chrome?.destroy();
	chrome = mountViewerChrome({
		host: box,
		onPrev: () => showPhoto(shownIndex - 1),
		onNext: () => showPhoto(shownIndex + 1),
		onClose: closeLightbox,
	});
	// The viewer owns a pinch in here; the browser's own would zoom the whole page and outlive the close.
	zoom?.destroy();
	zoom = mountPhotoZoom({ image: document.getElementById("lightboxImg") as HTMLImageElement, surface: box });

	box.style.display = "";
	document.body.style.overflow = "hidden";
	showPhoto(index);
	// PU-04: move focus into the dialog (Photos.cshtml marks #lightbox role=dialog aria-modal).
	chrome.focusClose();
}

/** Page the open lightbox to another photo of the current grid page (wraps at both ends). */
function showPhoto(to: number): void {
	if (!chrome || items.length === 0) return;
	shownIndex = (to + items.length) % items.length;
	const p = must(items[shownIndex], 'modulo index within items (guarded non-empty)');

	const img = document.getElementById("lightboxImg") as HTMLImageElement;
	const meta = document.getElementById("lightboxMeta") as HTMLElement;
	const link = document.getElementById("lightboxActivity") as HTMLAnchorElement;

	zoom?.reset(); // a pinch belongs to the photo it was made on, not to the next one
	img.src = imageUrl(p, "preview");
	img.alt = p.origFilename ?? "";
	link.href = `/Activities/Details/${p.trackId}`;

	const when = p.capturedUtc ? formatLocal(p.capturedUtc, p.tzOffsetHours) : "Unknown time";
	// eslint-disable-next-line no-unsanitized/property -- when/cameraModel/origFilename/description escapeHtml'd; ternary alternates are static literals
	meta.innerHTML =
		(p.description ? `<div class="photos-meta-row photos-meta-caption">${escapeHtml(p.description)}</div>` : "") +
		`<div class="photos-meta-row"><i class="bi bi-clock"></i> ${escapeHtml(when)}</div>` +
		(p.cameraModel ? `<div class="photos-meta-row"><i class="bi bi-camera"></i> ${escapeHtml(p.cameraModel)}</div>` : "") +
		(p.hasExifGps ? `<div class="photos-meta-row"><i class="bi bi-geo"></i> GPS tagged</div>` : "") +
		`<div class="photos-meta-row photos-meta-file">${escapeHtml(p.origFilename ?? "")}</div>` +
		`<button type="button" id="lightboxDelete" class="btn-cockpit btn-cockpit-ghost btn-cockpit-sm photos-delete">` +
		`<i class="bi bi-trash"></i> Delete</button>`;
	// The meta block was just replaced wholesale, so this binds to the new button — no stacking.
	document.getElementById("lightboxDelete")?.addEventListener("click", asyncCallback('delete photo', () => deletePhoto(p)));

	chrome.setPosition(shownIndex, items.length);
	// The side panel already shows the caption in full — no need to repeat it over the image.
	chrome.setCaption(null);
	void renderMinimap(p);
}

// PU-04: the lightbox markup declares role="dialog" aria-modal="true" (Photos.cshtml) but is a plain
// div toggled via display — unlike the shared photo-lightbox.ts it is NOT a native <dialog>, so the
// browser does not trap focus. Without this, Tab past the last control (close/‹/›) walks into the grid
// behind the open viewer. Wrap Tab within #lightbox while it is open; the arrows the visible chrome
// controls skip (hidden for a single photo) drop out of the query so focus can't land on them.
function lightboxFocusables(box: HTMLElement): HTMLElement[] {
	return Array.from(
		box.querySelectorAll<HTMLElement>('button, a[href], input, [tabindex]:not([tabindex="-1"])'),
	).filter((el) => !el.hasAttribute("hidden") && !(el as HTMLButtonElement).disabled);
}

function trapLightboxTab(e: KeyboardEvent): void {
	const box = document.getElementById("lightbox");
	if (!box) return;
	const items = lightboxFocusables(box);
	if (items.length === 0) return;
	const first = must(items[0], 'non-empty focusables (guarded above)');
	const last = must(items[items.length - 1], 'non-empty focusables (guarded above)');
	const active = document.activeElement as HTMLElement | null;
	if (e.shiftKey) {
		if (active === first || !box.contains(active)) { last.focus(); e.preventDefault(); }
	} else if (active === last || !box.contains(active)) {
		first.focus();
		e.preventDefault();
	}
}

function closeLightbox(): void {
	(document.getElementById("lightbox") as HTMLElement).style.display = "none";
	document.body.style.overflow = "";
	chrome?.destroy();
	chrome = null;
	zoom?.destroy();
	zoom = null;
	shownIndex = -1;
	miniMapGen++; // abort any render still awaiting
	if (miniMap) { miniMap.remove(); miniMap = null; }
	// PU-04: restore focus to the grid cell that opened the lightbox (if still in the DOM).
	if (lastFocused?.isConnected) lastFocused.focus();
	lastFocused = null;
}

async function renderMinimap(p: PhotoDto): Promise<void> {
	const container = document.getElementById("lightboxMap") as HTMLElement;
	container.innerHTML = "";
	if (miniMap) { miniMap.remove(); miniMap = null; }
	const gen = ++miniMapGen; // supersede any in-flight render so a fast re-open/close can't leak a map

	const styleUrl = resolveStyleUrl(defaultBasemapId());
	if (!styleUrl) return;

	try {
		const [{ default: maplibregl }, gpsResp] = await Promise.all([
			import("maplibre-gl"),
			fetch(`/api/activities/${p.trackId}/gps`),
		]);
		if (gen !== miniMapGen) return; // superseded while awaiting the import/fetch
		if (!gpsResp.ok) return; // an error body ({code,message}) is not a point array — minimap is best-effort
		const pts: ActivityGpsPointDto[] = await gpsResp.json();
		if (gen !== miniMapGen) return;
		const map = new maplibregl.Map({
			container, style: styleUrl, interactive: false, attributionControl: false,
		});
		miniMap = map;
		installStyleFilterSanitizer(map); // null-guard basemap numeric filters (poi rank / boundary admin_level)

		map.on("load", () => {
			const coords = pts.filter((q) => q.gpsLat != null && q.gpsLong != null).map((q) => [q.gpsLong, q.gpsLat] as [number, number]);
			if (coords.length > 1) {
				map.addSource("track", { type: "geojson", data: { type: "Feature", properties: {}, geometry: { type: "LineString", coordinates: coords } } });
				map.addLayer({ id: "track", type: "line", source: "track", paint: { "line-color": "#38bdf8", "line-width": 3 } });
				const b = coords.reduce((bb, c) => bb.extend(c), new maplibregl.LngLatBounds(coords[0], coords[0]));
				map.fitBounds(b, { padding: 24, animate: false });
			}
			if (p.markerLat != null && p.markerLng != null) {
				new maplibregl.Marker({ color: "#f59e0b" }).setLngLat([p.markerLng, p.markerLat]).addTo(map);
			}
		});
	} catch {
		// minimap is best-effort — the preview + activity link still work
	}
}

async function deletePhoto(p: PhotoDto): Promise<void> {
	if (!confirm("Delete this photo? This cannot be undone.")) return;
	try {
		const resp = await fetch(`/api/photos/${p.photoId}`, { method: "DELETE" });
		if (resp.ok) { closeLightbox(); void load(); }
		else alert("Couldn't delete the photo. Please try again."); // FUX-06: was a silent no-op
	} catch {
		alert("Couldn't delete the photo. Please try again.");
	}
}

// ── Helpers ──────────────────────────────────────────────────────────────────

/** UTC instant off the wire — an explicit offset since AR-02 ("+00:00"); tolerate the legacy
 * offset-less form too, but never double-mark a string that already carries one. */
function parseUtc(utcIso: string): Date {
	return new Date(/[Zz]$|[+-]\d\d:\d\d$/.test(utcIso) ? utcIso : `${utcIso}Z`);
}

function formatLocal(utcIso: string, offsetHours: number | null): string {
	const d = parseUtc(utcIso);
	if (offsetHours != null) {
		const local = new Date(d.getTime() + offsetHours * 3600_000);
		return local.toISOString().replace("T", " ").slice(0, 16);
	}
	return d.toLocaleString();
}

// ── Wire-up ──────────────────────────────────────────────────────────────────
document.getElementById("btnApplyFilter")?.addEventListener("click", () => { page = 1; void load(); });
groupToggle.addEventListener("change", () => { page = 1; void load(); });
document.getElementById("btnDeleteSelected")?.addEventListener("click", () => void deleteSelected());
document.getElementById("btnClearSelected")?.addEventListener("click", () => {
	// PU-08: clearing the selection is client-only — drop the marks in place instead of a full reload.
	selected.clear();
	for (const el of grid.querySelectorAll<HTMLElement>(".photos-cell-wrap.is-marked")) {
		el.classList.remove("is-marked");
		el.querySelector(".photos-cell-del")?.setAttribute("aria-pressed", "false");
	}
	updateSelectBar();
});
document.getElementById("btnPrevPage")?.addEventListener("click", () => { if (page > 1) { page--; void load(); } });
document.getElementById("btnNextPage")?.addEventListener("click", () => { page++; void load(); });
document.getElementById("lightbox")?.addEventListener("click", (e) => { if (e.target === e.currentTarget) closeLightbox(); });
// Escape closes and ‹ › page — but only while the viewer is up (chrome is null otherwise), and only
// for the keys it acts on. Tab is trapped inside the open viewer so focus can't reach the grid behind
// it (PU-04). Everything else stays the page's.
document.addEventListener("keydown", (e) => {
	if (!chrome) return;
	if (chrome.handleKey(e)) { e.preventDefault(); return; }
	if (e.key === "Tab") trapLightboxTab(e);
});

void load();

export {};
